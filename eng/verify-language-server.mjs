import assert from "node:assert/strict";
import { spawn } from "node:child_process";

const server = process.argv[2];
assert.ok(server, "Supply the packaged language-server DLL or native executable");
const child = spawn(server.endsWith(".dll") ? "dotnet" : server,
    server.endsWith(".dll") ? [server] : [], { stdio: ["pipe", "pipe", "pipe"] });
let stderr = "";
let buffer = Buffer.alloc(0);
let exited = false;
let shutdown = false;

function send(message) {
    const body = Buffer.from(JSON.stringify({ jsonrpc: "2.0", ...message }));
    child.stdin.write(`Content-Length: ${body.length}\r\n\r\n`);
    child.stdin.write(body);
}

try {
    await new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error("Packaged language-server LSP smoke test timed out")), 30000);
        child.on("error", reject);
        child.stderr.on("data", chunk => { stderr += chunk; });
        child.stdin.on("error", reject);
        child.on("close", code => {
            exited = true;
            clearTimeout(timer);
            if (code !== 0 || !shutdown) reject(new Error(`Language server exited ${code}: ${stderr}`));
            else resolve();
        });
        child.stdout.on("data", chunk => {
            try {
                buffer = Buffer.concat([buffer, chunk]);
                while (true) {
                    const end = buffer.indexOf("\r\n\r\n");
                    if (end < 0) break;
                    const header = buffer.subarray(0, end).toString("ascii");
                    const match = /Content-Length:\s*(\d+)/i.exec(header);
                    assert.ok(match, `Invalid LSP header: ${header}`);
                    const length = Number(match[1]);
                    if (buffer.length < end + 4 + length) break;
                    const message = JSON.parse(buffer.subarray(end + 4, end + 4 + length).toString("utf8"));
                    buffer = buffer.subarray(end + 4 + length);
                    assert.equal(message.error, undefined, JSON.stringify(message));
                    if (message.id === 1) {
                        assert.equal(message.result.serverInfo.name, "cala-ls");
                        send({ method: "initialized", params: {} });
                        send({ method: "textDocument/didOpen", params: { textDocument: {
                            uri: "file:///virtual/native-smoke.cala", languageId: "caladesi", version: 1,
                            text: "const a = 1;\nlet b: any = a;\n"
                        } } });
                    } else if (message.method === "textDocument/publishDiagnostics") {
                        assert.ok(message.params.diagnostics.some(d => d.code === "CAL0201"));
                        send({ id: 2, method: "textDocument/hover", params: {
                            textDocument: { uri: "file:///virtual/native-smoke.cala" }, position: { line: 0, character: 6 }
                        } });
                    } else if (message.id === 2) {
                        assert.match(message.result.contents.value, /\ba\b/);
                        send({ id: 3, method: "shutdown" });
                    } else if (message.id === 3) {
                        shutdown = true;
                        send({ method: "exit" });
                    }
                }
            } catch (error) {
                clearTimeout(timer);
                reject(error);
            }
        });
        send({ id: 1, method: "initialize", params: { processId: null, rootUri: null, capabilities: {} } });
    });
    console.log("Packaged native language server: initialize, diagnostics, hover and shutdown passed");
} finally {
    if (!exited) child.kill();
}
