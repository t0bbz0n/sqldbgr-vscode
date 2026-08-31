import { ChildProcess, spawn } from 'child_process';
import * as fs from 'fs';
import * as http from 'http';
import * as path from 'path';
import * as vscode from 'vscode';

const NPX_FALLBACK_COMMAND = ['npx', '-y', 'tsql-debugger-sidecar'];
const HEALTH_PROBE_TIMEOUT_MS = 1000;
const HEALTH_POLL_INTERVAL_MS = 250;
// Generöst: första körningen kan behöva ladda ner .NET-runtimen/npx-paketet.
const STARTUP_TIMEOUT_MS = 120_000;

/**
 * Ser till att sidecaren kör innan en debug-session startar. Om inget svarar
 * på /health spawnas den och processen ägs av extensionen: den städas undan
 * vid deactivate. En sidecar som användaren startat själv rörs aldrig - då
 * svarar health-proben och vi spawnar inget.
 *
 * Startkommandot väljs i ordning:
 *  1. sidecarCommand från launch-konfigurationen (dev-override)
 *  2. den buntade sidecaren i VSIX:en (sidecar-dist/), körd med en runtime
 *     som .NET Install Tool-extensionen hämtar automatiskt vid första
 *     körningen (fallback: systemets `dotnet`)
 *  3. npx tsql-debugger-sidecar (obundlad/dev-miljö)
 */
export class SidecarManager implements vscode.Disposable {
  private proc: ChildProcess | null = null;
  private output: vscode.OutputChannel | null = null;

  constructor(
    private readonly extensionPath: string,
    private readonly extensionId: string
  ) {}

  async ensureRunning(sidecarUrl: string, command?: string[]): Promise<void> {
    if (await this.isHealthy(sidecarUrl)) return;

    if (!this.proc || this.proc.exitCode !== null) {
      const port = new URL(sidecarUrl).port || '5199';
      await this.startProcess(await this.resolveCommand(port, command));
    }
    await this.waitForHealthy(sidecarUrl);
  }

  dispose(): void {
    this.proc?.kill();
    this.proc = null;
    this.output?.dispose();
    this.output = null;
  }

  private async resolveCommand(port: string, override?: string[]): Promise<string[]> {
    if (override?.length) return [...override, '--port', port];

    const bundledDll = path.join(this.extensionPath, 'sidecar-dist', 'SqlDebugger.Sidecar.dll');
    if (fs.existsSync(bundledDll)) {
      return [await this.acquireDotnet(), bundledDll, '--port', port];
    }
    return [...NPX_FALLBACK_COMMAND, '--port', port];
  }

  /**
   * Hämtar en ASP.NET Core 8-runtime via .NET Install Tool-extensionen
   * (ms-dotnettools.vscode-dotnet-runtime). Den laddar ner runtimen till en
   * privat mapp vid första anropet och svarar direkt med sökvägen därefter -
   * användaren behöver alltså inte ha .NET installerat. Om extensionen
   * saknas (t.ex. i dev-hosten) provas systemets `dotnet`.
   */
  private async acquireDotnet(): Promise<string> {
    try {
      const result = await vscode.commands.executeCommand<{ dotnetPath: string } | undefined>(
        'dotnet.acquire',
        { version: '8.0', mode: 'aspnetcore', requestingExtensionId: this.extensionId });
      if (result?.dotnetPath) {
        this.channel().appendLine(`[dotnet] använder ${result.dotnetPath}`);
        return result.dotnetPath;
      }
    } catch (err) {
      this.channel().appendLine(
        `[dotnet] kunde inte hämta runtime via .NET Install Tool (${(err as Error).message}) - provar systemets dotnet.`);
    }
    return 'dotnet';
  }

  private channel(): vscode.OutputChannel {
    this.output ??= vscode.window.createOutputChannel('T-SQL Debugger Sidecar');
    return this.output;
  }

  private async startProcess(command: string[]): Promise<void> {
    const [exe, ...args] = command;
    this.channel().appendLine(`[start] ${exe} ${args.join(' ')}`);

    // shell krävs för npx.cmd på Windows; en exe-sökväg ska INTE gå via shell
    // (sökvägar med mellanslag går sönder av shell-quoting)
    this.proc = spawn(exe, args, { shell: process.platform === 'win32' && exe === 'npx' });
    this.proc.stdout?.on('data', (d: Buffer) => this.output?.append(d.toString()));
    this.proc.stderr?.on('data', (d: Buffer) => this.output?.append(d.toString()));
    this.proc.on('error', err => this.output?.appendLine(`[fel] ${err.message}`));
    this.proc.on('exit', code => this.output?.appendLine(`[avslutad] exit code ${code}`));
  }

  private async waitForHealthy(sidecarUrl: string): Promise<void> {
    const deadline = Date.now() + STARTUP_TIMEOUT_MS;
    while (Date.now() < deadline) {
      if (this.proc && this.proc.exitCode !== null) {
        this.output?.show(true);
        throw new Error(
          `sidecar-processen avslutades med kod ${this.proc.exitCode} - se output-kanalen "T-SQL Debugger Sidecar".`);
      }
      if (await this.isHealthy(sidecarUrl)) return;
      await new Promise(r => setTimeout(r, HEALTH_POLL_INTERVAL_MS));
    }
    this.output?.show(true);
    throw new Error(
      `sidecaren svarade inte på ${sidecarUrl}/health inom ${STARTUP_TIMEOUT_MS / 1000}s.`);
  }

  private isHealthy(sidecarUrl: string): Promise<boolean> {
    return new Promise(resolve => {
      const req = http.get(new URL('/health', sidecarUrl),
        { timeout: HEALTH_PROBE_TIMEOUT_MS },
        res => {
          res.resume();
          resolve(res.statusCode === 200);
        });
      req.on('timeout', () => req.destroy());
      req.on('error', () => resolve(false));
    });
  }
}
