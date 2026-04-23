import * as vscode from 'vscode';
import WebSocket from 'ws';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { exec } from 'child_process';

interface BridgeMessage {
    type: string;
    requestId?: string;
    action?: string;
    params?: Record<string, any>;
    sessionId?: string;
    connectionId?: string;
}

type ActionResult = Record<string, any>;

interface BackendDiscoveryEntry {
    instanceId: string;
    baseUrl: string;
    apiKeyFilePath: string;
}

export class BridgeClient {
    private ws: WebSocket | null = null;
    private readonly outputChannel: vscode.OutputChannel;
    private readonly onStatusChanged: (connected: boolean) => void;
    private sessionId: string | null = null;
    private connectionId: string | null = null;

    constructor(outputChannel: vscode.OutputChannel, onStatusChanged: (connected: boolean) => void) {
        this.outputChannel = outputChannel;
        this.onStatusChanged = onStatusChanged;
    }

    get isConnected(): boolean {
        return this.ws !== null && this.ws.readyState === WebSocket.OPEN;
    }

    // ── Connection lifecycle ──────────────────────────────────────────────

    async connect(host: string, port: number, apiKeyFilePath: string, backendInstanceId: string, timeoutSeconds: number): Promise<void> {
        if (this.isConnected) {
            await this.disconnect();
        }

        const resolvedApiKeyPath = this.resolveApiKeyPath(apiKeyFilePath, backendInstanceId, host, port);
        const apiKey = resolvedApiKeyPath ? this.readApiKey(resolvedApiKeyPath) : null;
        if (!apiKey) {
            this.log('API key read FAILED — no matching backend runtime auth file could be resolved.');
            throw new Error(
                'API key not found. Configure sharpclaw.apiKeyFilePath explicitly or ensure the selected backend publishes discovery metadata.'
            );
        }
        this.log(`API key read OK (${apiKey.length} chars).`);

        const uri = `ws://${host}:${port}/editor/ws`;
        this.log(`Connecting to ${uri}...`);

        return new Promise<void>((resolve, reject) => {
            const timeout = setTimeout(() => {
                this.ws?.terminate();
                reject(new Error(`Connection timed out after ${timeoutSeconds}s`));
            }, timeoutSeconds * 1000);

            this.ws = new WebSocket(uri, { headers: { 'X-Api-Key': apiKey } });

            this.ws.on('open', () => {
                this.log('WebSocket connected. Sending registration...');
                const workspacePath = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? '';
                const registration = {
                    type: 'register',
                    editorType: 'visualStudioCode',
                    editorVersion: vscode.version,
                    workspacePath
                };
                this.ws!.send(JSON.stringify(registration));
                this.log(`Registration sent: editorType=${registration.editorType}, version=${registration.editorVersion}, workspace=${workspacePath || '(none)'}`);
            });

            this.ws.on('message', (data: WebSocket.Data) => {
                const text = data.toString();
                let msg: BridgeMessage;
                try {
                    msg = JSON.parse(text);
                } catch {
                    this.log(`Failed to parse message: ${this.truncate(text, 200)}`);
                    return;
                }

                if (msg.type === 'registered') {
                    clearTimeout(timeout);
                    this.sessionId = msg.sessionId ?? null;
                    this.connectionId = msg.connectionId ?? null;
                    this.log(`Registered \u2014 session=${this.sessionId}, connection=${this.connectionId}`);
                    this.onStatusChanged(true);
                    resolve();
                    return;
                }

                if (msg.type === 'request') {
                    this.handleRequest(msg);
                    return;
                }

                this.log(`Unknown message type: ${msg.type}`);
            });

            this.ws.on('error', (err) => {
                clearTimeout(timeout);
                this.log(`WebSocket error: ${err.message}`);
                this.onStatusChanged(false);
                reject(err);
            });

            this.ws.on('close', (code, reason) => {
                this.log(`WebSocket closed \u2014 code=${code}, reason=${reason.toString()}`);
                this.ws = null;
                this.sessionId = null;
                this.connectionId = null;
                this.onStatusChanged(false);
            });
        });
    }

    async disconnect(): Promise<void> {
        if (!this.ws) { return; }
        this.log('Disconnecting...');
        const ws = this.ws;
        this.ws = null;

        if (ws.readyState === WebSocket.OPEN) {
            ws.close(1000, 'Client disconnect');
        } else {
            ws.terminate();
        }

        this.sessionId = null;
        this.connectionId = null;
        this.onStatusChanged(false);
        this.log('Disconnected.');
    }

    // ── Request dispatch ──────────────────────────────────────────────────

    private async handleRequest(msg: BridgeMessage): Promise<void> {
        const { requestId, action, params } = msg;
        if (!requestId || !action) {
            this.log('Received request with missing requestId or action.');
            return;
        }

        this.log(`\u2192 ${action} [${requestId}]`);
        const startMs = performance.now();

        try {
            const result = await this.handleAction(action, params ?? {});
            const elapsedMs = Math.round(performance.now() - startMs);
            this.sendResponse(requestId, true, result);
            this.log(`\u2190 ${action} [${requestId}] OK (${elapsedMs}ms)`);
        } catch (err: any) {
            const elapsedMs = Math.round(performance.now() - startMs);
            const errorMessage = err.message ?? String(err);
            this.sendResponse(requestId, false, undefined, errorMessage);
            this.log(`\u2190 ${action} [${requestId}] FAILED (${elapsedMs}ms) \u2014 ${errorMessage}`);
        }
    }

    private async handleAction(action: string, params: Record<string, any>): Promise<ActionResult> {
        switch (action) {
            case 'read_file':      return this.handleReadFile(params);
            case 'write_file':     return this.handleWriteFile(params);
            case 'get_open_files': return this.handleGetOpenFiles();
            case 'get_selection':  return this.handleGetSelection();
            case 'create_file':    return this.handleCreateFile(params);
            case 'delete_file':    return this.handleDeleteFile(params);
            case 'apply_edit':     return this.handleApplyEdit(params);
            case 'get_diagnostics': return this.handleGetDiagnostics(params);
            case 'show_diff':      return this.handleShowDiff(params);
            case 'run_build':      return this.handleRunBuild(params);
            case 'run_terminal':   return this.handleRunTerminal(params);
            default:
                throw new Error(`Unknown action: ${action}`);
        }
    }

    // ── Action handlers ───────────────────────────────────────────────────

    private handleReadFile(params: Record<string, any>): ActionResult {
        const filePath = this.resolvePath(this.getRequiredString(params, 'path'));
        const startLine = this.getOptionalInt(params, 'startLine');
        const endLine = this.getOptionalInt(params, 'endLine');
        this.log(`  read_file: path=${filePath}, startLine=${startLine ?? 'n/a'}, endLine=${endLine ?? 'n/a'}`);

        if (!fs.existsSync(filePath)) {
            throw new Error(`File not found: ${filePath}`);
        }

        const allLines = fs.readFileSync(filePath, 'utf-8').split(/\r?\n/);
        let lines = allLines;

        if (startLine !== undefined || endLine !== undefined) {
            const start = Math.max(0, (startLine ?? 1) - 1);
            const end = Math.min(allLines.length, endLine ?? allLines.length);
            lines = allLines.slice(start, end);
        }

        return { content: lines.join('\n'), lineCount: allLines.length };
    }

    private handleWriteFile(params: Record<string, any>): ActionResult {
        const filePath = this.resolvePath(this.getRequiredString(params, 'path'));
        const content = this.getRequiredString(params, 'content');
        this.log(`  write_file: path=${filePath}, contentLength=${content.length}`);

        this.ensureDirectory(filePath);
        fs.writeFileSync(filePath, content, 'utf-8');
        return { path: filePath, written: true };
    }

    private handleGetOpenFiles(): ActionResult {
        const files = vscode.workspace.textDocuments
            .filter(doc => doc.uri.scheme === 'file')
            .map(doc => ({
                path: doc.uri.fsPath,
                name: path.basename(doc.uri.fsPath),
                saved: !doc.isDirty
            }));

        this.log(`  get_open_files: count=${files.length}`);
        return { files };
    }

    private handleGetSelection(): ActionResult {
        const editor = vscode.window.activeTextEditor;
        if (!editor) {
            throw new Error('No active text editor.');
        }

        const selection = editor.selection;
        const selectedText = editor.document.getText(selection);

        const result = {
            file: editor.document.uri.fsPath,
            line: selection.active.line + 1,
            column: selection.active.character + 1,
            selectedText
        };

        this.log(`  get_selection: file=${result.file}, line=${result.line}, col=${result.column}, selectedLength=${selectedText.length}`);
        return result;
    }

    private handleCreateFile(params: Record<string, any>): ActionResult {
        const filePath = this.resolvePath(this.getRequiredString(params, 'path'));
        const content = this.getOptionalString(params, 'content') ?? '';
        this.log(`  create_file: path=${filePath}, contentLength=${content.length}`);

        this.ensureDirectory(filePath);
        fs.writeFileSync(filePath, content, 'utf-8');
        return { path: filePath, created: true };
    }

    private handleDeleteFile(params: Record<string, any>): ActionResult {
        const filePath = this.resolvePath(this.getRequiredString(params, 'path'));
        this.log(`  delete_file: path=${filePath}`);

        if (!fs.existsSync(filePath)) {
            throw new Error(`File not found: ${filePath}`);
        }

        fs.unlinkSync(filePath);
        return { path: filePath, deleted: true };
    }

    private handleApplyEdit(params: Record<string, any>): ActionResult {
        const filePath = this.resolvePath(this.getRequiredString(params, 'path'));
        const startLine = this.getRequiredInt(params, 'startLine');
        const endLine = this.getRequiredInt(params, 'endLine');
        const newText = this.getRequiredString(params, 'newText');
        this.log(`  apply_edit: path=${filePath}, startLine=${startLine}, endLine=${endLine}, newTextLength=${newText.length}`);

        if (!fs.existsSync(filePath)) {
            throw new Error(`File not found: ${filePath}`);
        }

        const raw = fs.readFileSync(filePath, 'utf-8');
        const eol = raw.includes('\r\n') ? '\r\n' : '\n';
        const lines = raw.split(/\r?\n/);

        const start = Math.max(0, startLine - 1);
        const end = Math.min(lines.length, endLine);
        const newLines = newText.length > 0 ? newText.split(/\r?\n/) : [];

        lines.splice(start, end - start, ...newLines);
        fs.writeFileSync(filePath, lines.join(eol), 'utf-8');
        return { path: filePath, applied: true };
    }

    private handleGetDiagnostics(params: Record<string, any>): ActionResult {
        const filePath = this.getOptionalString(params, 'path');
        this.log(`  get_diagnostics: path=${filePath ?? '(all)'}`);

        let entries: [vscode.Uri, vscode.Diagnostic[]][];

        if (filePath) {
            const resolved = this.resolvePath(filePath);
            const uri = vscode.Uri.file(resolved);
            entries = [[uri, vscode.languages.getDiagnostics(uri)]];
        } else {
            entries = vscode.languages.getDiagnostics();
        }

        const diagnostics: any[] = [];
        for (const [uri, diags] of entries) {
            for (const d of diags) {
                diagnostics.push({
                    severity: vscode.DiagnosticSeverity[d.severity],
                    message: d.message,
                    file: uri.fsPath,
                    line: d.range.start.line + 1,
                    column: d.range.start.character + 1,
                    source: d.source ?? ''
                });
            }
        }

        return { diagnostics, count: diagnostics.length };
    }

    private async handleShowDiff(params: Record<string, any>): Promise<ActionResult> {
        const leftPath = this.getOptionalString(params, 'leftPath');
        const leftContent = this.getOptionalString(params, 'leftContent');
        const rightContent = this.getRequiredString(params, 'rightContent');
        const title = this.getOptionalString(params, 'title') ?? 'SharpClaw Diff';
        this.log(`  show_diff: leftPath=${leftPath ?? '(content)'}, title=${title}`);

        let leftText: string;
        if (leftPath) {
            const resolved = this.resolvePath(leftPath);
            leftText = fs.existsSync(resolved) ? fs.readFileSync(resolved, 'utf-8') : (leftContent ?? '');
        } else {
            leftText = leftContent ?? '';
        }

        const tmpDir = os.tmpdir();
        const stamp = Date.now();
        const leftFile = path.join(tmpDir, `sharpclaw-diff-left-${stamp}.tmp`);
        const rightFile = path.join(tmpDir, `sharpclaw-diff-right-${stamp}.tmp`);

        fs.writeFileSync(leftFile, leftText, 'utf-8');
        fs.writeFileSync(rightFile, rightContent, 'utf-8');

        await vscode.commands.executeCommand(
            'vscode.diff',
            vscode.Uri.file(leftFile),
            vscode.Uri.file(rightFile),
            title
        );

        return { shown: true };
    }

    private handleRunBuild(params: Record<string, any>): Promise<ActionResult> {
        const project = this.getOptionalString(params, 'project');
        const workspacePath = this.getWorkspacePath();

        let command = 'dotnet build';
        if (project) {
            command += ` "${this.resolvePath(project)}"`;
        }

        this.log(`  run_build: command=${command}, cwd=${workspacePath}`);

        return new Promise<ActionResult>((resolve) => {
            exec(command, { cwd: workspacePath, maxBuffer: 1024 * 1024, timeout: 120_000 }, (error, stdout, stderr) => {
                const output = stdout + stderr;
                const succeeded = !error;

                const diagnostics: any[] = [];
                const pattern = /^(.+?)\((\d+),(\d+)\):\s+(error|warning)\s+(\w+):\s+(.+?)(?:\s+\[(.+?)\])?$/gm;
                let match: RegExpExecArray | null;

                while ((match = pattern.exec(output)) !== null) {
                    diagnostics.push({
                        file: match[1].trim(),
                        line: parseInt(match[2], 10),
                        column: parseInt(match[3], 10),
                        severity: match[4],
                        code: match[5],
                        message: match[6].trim(),
                        project: match[7]?.trim() ?? ''
                    });
                }

                this.log(`  run_build: succeeded=${succeeded}, diagnosticCount=${diagnostics.length}`);

                resolve({
                    succeeded,
                    diagnosticCount: diagnostics.length,
                    diagnostics,
                    output: this.truncate(output, 100_000)
                });
            });
        });
    }

    private handleRunTerminal(params: Record<string, any>): Promise<ActionResult> {
        const command = this.getRequiredString(params, 'command');
        const cwd = this.getOptionalString(params, 'cwd');
        const timeoutSeconds = this.getOptionalInt(params, 'timeoutSeconds') ?? 60;
        const workingDir = cwd ? this.resolvePath(cwd) : this.getWorkspacePath();

        this.log(`  run_terminal: command=${this.truncate(command, 200)}, cwd=${workingDir}, timeout=${timeoutSeconds}s`);

        return new Promise<ActionResult>((resolve) => {
            exec(command, {
                cwd: workingDir,
                maxBuffer: 100 * 1024,
                timeout: timeoutSeconds * 1000,
                shell: process.platform === 'win32' ? 'cmd.exe' : '/bin/sh'
            }, (error, stdout, stderr) => {
                const exitCode = error
                    ? (typeof error.code === 'number' ? error.code : 1)
                    : 0;
                resolve({
                    exitCode,
                    stdout: this.truncate(stdout, 100_000),
                    stderr: this.truncate(stderr, 100_000)
                });
            });
        });
    }

    // ── Response helper ───────────────────────────────────────────────────

    private sendResponse(requestId: string, success: boolean, data?: any, error?: string): void {
        if (!this.ws || this.ws.readyState !== WebSocket.OPEN) { return; }

        const msg: any = { type: 'response', requestId, success };
        if (data !== undefined) { msg.data = data; }
        if (error !== undefined) { msg.error = error; }

        this.ws.send(JSON.stringify(msg));
    }

    // ── Path helpers ──────────────────────────────────────────────────────

    private resolvePath(filePath: string): string {
        if (path.isAbsolute(filePath)) { return filePath; }
        return path.join(this.getWorkspacePath(), filePath);
    }

    private getWorkspacePath(): string {
        return vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? process.cwd();
    }

    private ensureDirectory(filePath: string): void {
        const dir = path.dirname(filePath);
        if (!fs.existsSync(dir)) {
            fs.mkdirSync(dir, { recursive: true });
        }
    }

    // ── API key ───────────────────────────────────────────────────────────

    private readApiKey(keyPath: string): string | null {
        if (keyPath.startsWith('~') && process.platform !== 'win32') {
            keyPath = path.join(process.env.HOME ?? '', keyPath.slice(1));
        }

        try {
            return fs.readFileSync(keyPath, 'utf-8').trim();
        } catch {
            this.log(`API key file not found at: ${keyPath}`);
            return null;
        }
    }

    private resolveApiKeyPath(configuredPath: string, backendInstanceId: string, host: string, port: number): string | null {
        if (configuredPath) {
            return configuredPath;
        }

        const entries = this.enumerateBackendDiscoveryEntries();
        if (backendInstanceId) {
            const byInstanceId = entries.find(e => e.instanceId === backendInstanceId);
            if (byInstanceId) {
                this.log(`Resolved API key path from backend instance id '${backendInstanceId}'.`);
                return byInstanceId.apiKeyFilePath;
            }
        }

        const targetUrl = `http://${host}:${port}`;
        const byUrl = entries.find(e => this.sameBaseUrl(e.baseUrl, targetUrl));
        if (byUrl) {
            this.log(`Resolved API key path from backend discovery URL '${targetUrl}'.`);
            return byUrl.apiKeyFilePath;
        }

        if (entries.length === 1) {
            this.log(`Resolved API key path from the only discovered backend '${entries[0].instanceId}'.`);
            return entries[0].apiKeyFilePath;
        }

        return null;
    }

    private enumerateBackendDiscoveryEntries(): BackendDiscoveryEntry[] {
        const discoveryDirectory = path.join(this.getSharpClawSharedRoot(), 'discovery', 'instances');
        if (!fs.existsSync(discoveryDirectory)) {
            return [];
        }

        const results: BackendDiscoveryEntry[] = [];
        for (const fileName of fs.readdirSync(discoveryDirectory)) {
            if (!fileName.startsWith('backend-') || !fileName.endsWith('.json')) {
                continue;
            }

            try {
                const content = fs.readFileSync(path.join(discoveryDirectory, fileName), 'utf-8');
                const parsed = JSON.parse(content) as Partial<BackendDiscoveryEntry>;
                if (parsed.instanceId && parsed.baseUrl && parsed.apiKeyFilePath) {
                    results.push({
                        instanceId: parsed.instanceId,
                        baseUrl: parsed.baseUrl,
                        apiKeyFilePath: parsed.apiKeyFilePath
                    });
                }
            } catch {
                continue;
            }
        }

        return results;
    }

    private getSharpClawSharedRoot(): string {
        if (process.platform === 'win32') {
            const localAppData = process.env.LOCALAPPDATA;
            if (localAppData) {
                return path.join(localAppData, 'SharpClaw');
            }
        }

        const home = process.env.HOME ?? os.homedir();
        return path.join(home, '.local', 'share', 'SharpClaw');
    }

    private sameBaseUrl(left: string, right: string): boolean {
        return left.replace(/\/$/, '').toLowerCase() === right.replace(/\/$/, '').toLowerCase();
    }

    // ── Parameter extraction ──────────────────────────────────────────────

    private getRequiredString(params: Record<string, any>, key: string): string {
        const val = params[key];
        if (val === undefined || val === null) {
            throw new Error(`Missing required parameter: ${key}`);
        }
        return String(val);
    }

    private getRequiredInt(params: Record<string, any>, key: string): number {
        const val = params[key];
        if (val === undefined || val === null) {
            throw new Error(`Missing required parameter: ${key}`);
        }
        const num = Number(val);
        if (isNaN(num)) {
            throw new Error(`Parameter '${key}' must be a number.`);
        }
        return num;
    }

    private getOptionalString(params: Record<string, any>, key: string): string | undefined {
        const val = params[key];
        return val !== undefined && val !== null ? String(val) : undefined;
    }

    private getOptionalInt(params: Record<string, any>, key: string): number | undefined {
        const val = params[key];
        if (val === undefined || val === null) { return undefined; }
        const num = Number(val);
        return isNaN(num) ? undefined : num;
    }

    // ── Utilities ─────────────────────────────────────────────────────────

    private truncate(text: string, maxLength: number): string {
        if (text.length <= maxLength) { return text; }
        return text.substring(0, maxLength) + `... [truncated, ${text.length} total chars]`;
    }

    private log(message: string): void {
        const ts = new Date().toISOString().replace('T', ' ').substring(0, 23);
        this.outputChannel.appendLine(`[${ts}] ${message}`);
    }
}
