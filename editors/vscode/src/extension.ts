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

interface ServerCommand {
  command: string;
  args: string[];
  description: string;
}

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
      if (
        e.affectsConfiguration('caladesi.server.path') ||
        e.affectsConfiguration('caladesi.server.dotnetPath') ||
        e.affectsConfiguration('caladesi.server.runtimePath')
      ) {
        const choice = await vscode.window.showInformationMessage(
          'Caladesi server settings changed. Restart the language server to apply them?',
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
  const server = resolveServer(context);
  if (!server) {
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

  output.appendLine(`Starting ${server.description}`);
  const serverOptions: ServerOptions = {
    command: server.command,
    args: server.args,
    transport: TransportKind.stdio,
  };
  const config = vscode.workspace.getConfiguration('caladesi');
  const runtimePath = config.get<string>('server.runtimePath', '').trim();
  const clientOptions: LanguageClientOptions = {
    documentSelector: [
      { scheme: 'file', language: 'caladesi' },
      { scheme: 'untitled', language: 'caladesi' },
    ],
    outputChannel: output,
    traceOutputChannel: output,
    initializationOptions: {
      runtimePath: runtimePath || undefined,
      dotnetPath: config.get<string>('server.dotnetPath', '').trim() || undefined,
    },
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

/**
 * Resolves how to start the server: the configured path (an executable, or a `.dll` run with `dotnet`), then
 * `server/cala-ls` or `server/cala-ls.dll` inside the extension, then `cala-ls` on the PATH, then the development
 * build of this repository (`src/Caladesi.LanguageServer/bin/<Debug|Release>/net10.0/cala-ls.dll`).
 */
function resolveServer(context: vscode.ExtensionContext): ServerCommand | undefined {
  const config = vscode.workspace.getConfiguration('caladesi');
  const dotnet = config.get<string>('server.dotnetPath', '').trim() || 'dotnet';
  const exe = process.platform === 'win32' ? `${SERVER_NAME}.exe` : SERVER_NAME;
  const viaDotnet = (dll: string): ServerCommand => ({
    command: dotnet,
    args: [dll],
    description: `${dotnet} ${dll}`,
  });
  const native = (file: string): ServerCommand => ({ command: file, args: [], description: file });

  const configured = config.get<string>('server.path', '').trim();
  if (configured) {
    if (!isFile(configured)) {
      return undefined;
    }
    return configured.toLowerCase().endsWith('.dll') ? viaDotnet(configured) : native(configured);
  }

  const bundled = path.join(context.extensionPath, 'server', exe);
  if (isFile(bundled)) {
    return native(bundled);
  }
  const bundledDll = path.join(context.extensionPath, 'server', `${SERVER_NAME}.dll`);
  if (isFile(bundledDll)) {
    return viaDotnet(bundledDll);
  }

  const extensions = process.platform === 'win32' ? ['.exe', '.cmd', '.bat'] : [''];
  for (const dir of (process.env.PATH ?? '').split(path.delimiter).filter(Boolean)) {
    for (const ext of extensions) {
      const candidate = path.join(dir, SERVER_NAME + ext);
      if (isFile(candidate)) {
        return native(candidate);
      }
    }
  }

  const repoRoot = path.resolve(context.extensionPath, '..', '..');
  for (const configuration of ['Debug', 'Release']) {
    const dev = path.join(repoRoot, 'src', 'Caladesi.LanguageServer', 'bin', configuration, 'net10.0', `${SERVER_NAME}.dll`);
    if (isFile(dev)) {
      return viaDotnet(dev);
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
