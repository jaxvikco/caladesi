import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import {
  LanguageClient,
  type LanguageClientOptions,
  type ServerOptions,
  TransportKind,
} from 'vscode-languageclient/node';

const SERVER_NAME = 'cala-ls';
const OUTPUT_NAME = 'Caladesi Language Server';

let client: LanguageClient | undefined;
let output: vscode.OutputChannel;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  output = vscode.window.createOutputChannel(OUTPUT_NAME);
  context.subscriptions.push(
    output,
    vscode.commands.registerCommand('caladesi.restartServer', async () => {
      await stopClient();
      await startClient(context);
    }),
    vscode.workspace.onDidChangeConfiguration(async (e) => {
      if (e.affectsConfiguration('caladesi.server.path')) {
        const choice = await vscode.window.showInformationMessage(
          'Caladesi server path changed. Restart the language server to apply it?',
          'Restart',
        );
        if (choice === 'Restart') {
          await vscode.commands.executeCommand('caladesi.restartServer');
        }
      }
    }),
  );
  await startClient(context);
}

export function deactivate(): Thenable<void> | undefined {
  return stopClient();
}

async function stopClient(): Promise<void> {
  const current = client;
  client = undefined;
  if (current) {
    await current.stop();
  }
}

async function startClient(context: vscode.ExtensionContext): Promise<void> {
  const command = resolveServerPath(context);
  if (!command) {
    const message =
      `Could not find the Caladesi language server (${SERVER_NAME}). ` +
      'Set "caladesi.server.path" or put it on your PATH. Syntax highlighting still works.';
    output.appendLine(message);
    const choice = await vscode.window.showWarningMessage(message, 'Open Settings');
    if (choice === 'Open Settings') {
      await vscode.commands.executeCommand('workbench.action.openSettings', 'caladesi.server.path');
    }
    return;
  }

  output.appendLine(`Starting ${command}`);
  const serverOptions: ServerOptions = {
    command,
    args: [],
    transport: TransportKind.stdio,
  };
  const clientOptions: LanguageClientOptions = {
    documentSelector: [{ scheme: 'file', language: 'caladesi' }],
    outputChannel: output,
    traceOutputChannel: output,
  };

  const next = new LanguageClient('caladesi', OUTPUT_NAME, serverOptions, clientOptions);
  client = next;
  try {
    await next.start();
  } catch (err) {
    client = undefined;
    const detail = err instanceof Error ? err.message : String(err);
    output.appendLine(`Failed to start language server: ${detail}`);
    void vscode.window.showErrorMessage(`Failed to start the Caladesi language server: ${detail}`);
  }
}

/** Resolves the server: configured path, then bundled `server/cala-ls`, then `cala-ls` on PATH. */
function resolveServerPath(context: vscode.ExtensionContext): string | undefined {
  const exe = process.platform === 'win32' ? `${SERVER_NAME}.exe` : SERVER_NAME;

  const configured = vscode.workspace
    .getConfiguration('caladesi')
    .get<string>('server.path', '')
    .trim();
  if (configured) {
    return isFile(configured) ? configured : undefined;
  }

  const bundled = path.join(context.extensionPath, 'server', exe);
  if (isFile(bundled)) {
    return bundled;
  }

  const extensions = process.platform === 'win32' ? ['.exe', '.cmd', '.bat'] : [''];
  for (const dir of (process.env.PATH ?? '').split(path.delimiter).filter(Boolean)) {
    for (const ext of extensions) {
      const candidate = path.join(dir, SERVER_NAME + ext);
      if (isFile(candidate)) {
        return candidate;
      }
    }
  }
  return undefined;
}

function isFile(p: string): boolean {
  try {
    return fs.statSync(p).isFile();
  } catch {
    return false;
  }
}
