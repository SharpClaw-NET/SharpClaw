import * as vscode from 'vscode';
import { BridgeClient } from './bridgeClient';

let client: BridgeClient | undefined;
let statusBarItem: vscode.StatusBarItem;
let outputChannel: vscode.OutputChannel;

export function activate(context: vscode.ExtensionContext): void {
    outputChannel = vscode.window.createOutputChannel('SharpClaw');
    context.subscriptions.push(outputChannel);

    statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
    statusBarItem.command = 'sharpclaw.connect';
    updateStatusBar(false);
    statusBarItem.show();
    context.subscriptions.push(statusBarItem);

    client = new BridgeClient(outputChannel, (connected) => updateStatusBar(connected));

    context.subscriptions.push(
        vscode.commands.registerCommand('sharpclaw.connect', async () => {
            if (client?.isConnected) {
                vscode.window.showInformationMessage('SharpClaw: Already connected.');
                return;
            }
            await connectClient();
        }),
        vscode.commands.registerCommand('sharpclaw.disconnect', async () => {
            if (!client?.isConnected) {
                vscode.window.showInformationMessage('SharpClaw: Not connected.');
                return;
            }
            await client.disconnect();
            vscode.window.showInformationMessage('SharpClaw: Disconnected.');
        })
    );

    const extVersion = vscode.extensions.getExtension('sharpclaw.sharpclaw')?.packageJSON?.version ?? 'unknown';
    log(`SharpClaw extension v${extVersion} activating (VS Code ${vscode.version})...`);

    const config = vscode.workspace.getConfiguration('sharpclaw');
    const autoConnect = config.get<boolean>('autoConnect', true);
    const delaySeconds = config.get<number>('autoConnectDelaySeconds', 3) ?? 3;

    log(`  Config: host=${config.get<string>('host', '127.0.0.1')}, port=${config.get<number>('port', 48923)}`);
    log(`  Config: autoConnect=${autoConnect}, delaySeconds=${delaySeconds}, timeoutSeconds=${config.get<number>('connectionTimeoutSeconds', 10)}`);
    log(`  Config: apiKeyFilePath=${config.get<string>('apiKeyFilePath', '') || '(default)'}`);

    if (autoConnect) {
        const delayMs = delaySeconds * 1000;
        log(`  Auto-connect scheduled in ${delaySeconds}s.`);
        setTimeout(() => connectClient(), delayMs);
    } else {
        log('  Auto-connect disabled. Use "SharpClaw: Connect" command.');
    }

    log('SharpClaw extension activated.');
}

async function connectClient(): Promise<void> {
    if (!client) { return; }

    const config = vscode.workspace.getConfiguration('sharpclaw');
    const host = config.get<string>('host', '127.0.0.1')!;
    const port = config.get<number>('port', 48923)!;
    const apiKeyFilePath = config.get<string>('apiKeyFilePath', '')!;
    const timeoutSeconds = config.get<number>('connectionTimeoutSeconds', 10)!;

    log(`Connection sequence starting...`);
    log(`  Target: ws://${host}:${port}/editor/ws`);
    log(`  Timeout: ${timeoutSeconds}s`);
    log(`  API key path: ${apiKeyFilePath || '(default)'}`);
    log(`  Workspace: ${vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? '(none)'}`);

    try {
        await client.connect(host, port, apiKeyFilePath, timeoutSeconds);
        vscode.window.showInformationMessage('SharpClaw: Connected to bridge.');
    } catch (err: any) {
        log(`Connection failed: ${err.message}`);
        vscode.window.showErrorMessage(`SharpClaw: Connection failed \u2014 ${err.message}`);
    }
}

function updateStatusBar(connected: boolean): void {
    if (connected) {
        statusBarItem.text = '$(plug) SharpClaw';
        statusBarItem.tooltip = 'SharpClaw: Connected (click to reconnect)';
        statusBarItem.backgroundColor = undefined;
    } else {
        statusBarItem.text = '$(debug-disconnect) SharpClaw';
        statusBarItem.tooltip = 'SharpClaw: Disconnected (click to connect)';
        statusBarItem.backgroundColor = new vscode.ThemeColor('statusBarItem.warningBackground');
    }
}

function log(message: string): void {
    const ts = new Date().toISOString().replace('T', ' ').substring(0, 23);
    outputChannel.appendLine(`[${ts}] ${message}`);
}

export function deactivate(): void {
    log('SharpClaw extension deactivating...');
    client?.disconnect();
}
