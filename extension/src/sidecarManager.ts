import { ChildProcess, spawn } from 'child_process';
import * as fs from 'fs';
import * as http from 'http';
import * as path from 'path';
import * as vscode from 'vscode';
import { SidecarClient } from './sidecarClient';

const NPX_FALLBACK_COMMAND = ['npx', '-y', 'sqldbgr-sidecar'];
const HEALTH_PROBE_TIMEOUT_MS = 1000;
const HEALTH_POLL_INTERVAL_MS = 250;
// Generöst: första körningen kan behöva ladda ner .NET-runtimen/npx-paketet.
const STARTUP_TIMEOUT_MS = 120_000;

/**
 * Ser till att en sidecar kör innan en debug-session startar och returnerar
 * dess URL. Default: varje VS Code-fönster startar sin EGEN sidecar på en
 * slumpport (--port 0; adressen läses från stdout) - då kan fönster aldrig
 * ta över eller döda varandras sidecar. Anges `sidecarUrl` explicit används
 * den (egenstartad sidecar), och startas på den porten om inget svarar.
 * Processen ägs av extensionen och städas undan vid deactivate.
 *
 * Startkommandot väljs i ordning:
 *  1. sidecarCommand från launch-konfigurationen (dev-override)
 *  2. den buntade sidecaren i VSIX:en (sidecar-dist/), körd med en runtime
 *     som .NET Install Tool-extensionen hämtar automatiskt vid första
 *     körningen (fallback: systemets `dotnet`)
 *  3. npx sqldbgr-sidecar (obundlad/dev-miljö)
 */
export class SidecarManager implements vscode.Disposable {
  private proc: ChildProcess | null = null;
  private output: vscode.OutputChannel | null = null;
  /** URL:en för sidecaren vi själva startat (slumpport eller explicit). */
  private ownUrl: string | null = null;
  private urlFromStdout: Promise<string> | null = null;

  constructor(
    private readonly extensionPath: string,
    private readonly extensionId: string,
    private readonly expectedVersion: string
  ) {}

  async ensureRunning(explicitUrl: string | undefined, command?: string[]): Promise<string> {
    if (explicitUrl) {
      if (await this.isHealthy(explicitUrl)) {
        // En kvarlämnad äldre sidecar svarar friskt men saknar nya endpoints -
        // byt ut den. Dev-override hoppar över kontrollen (dotnet run stämplar
        // ingen version).
        if (command?.length || !(await this.isStale(explicitUrl))) return explicitUrl;
        await this.replaceStale(explicitUrl);
      }
      if (!this.isOwnProcessAlive()) {
        await this.startProcess(await this.resolveCommand(new URL(explicitUrl).port || '5199', command));
        this.ownUrl = explicitUrl;
      }
      await this.waitForHealthy(explicitUrl);
      return explicitUrl;
    }

    // Egen sidecar per fönster på slumpport
    if (this.isOwnProcessAlive() && this.ownUrl && await this.isHealthy(this.ownUrl)) {
      return this.ownUrl;
    }
    await this.startProcess(await this.resolveCommand('0', command));
    this.ownUrl = await this.waitForUrl();
    await this.waitForHealthy(this.ownUrl);
    return this.ownUrl;
  }

  private isOwnProcessAlive(): boolean {
    return this.proc !== null && this.proc.exitCode === null;
  }

  /** Sidecaren skriver "SQLDBGR_SIDECAR_URL=http://127.0.0.1:<port>" när den lyssnar. */
  private async waitForUrl(): Promise<string> {
    const timeout = new Promise<never>((_, reject) => setTimeout(() => {
      this.output?.show(true);
      reject(new Error(`sidecaren rapporterade ingen adress inom ${STARTUP_TIMEOUT_MS / 1000}s - se output-kanalen "sqldbgr Sidecar".`));
    }, STARTUP_TIMEOUT_MS));
    return Promise.race([this.urlFromStdout!, timeout]);
  }

  private async isStale(sidecarUrl: string): Promise<boolean> {
    try {
      const health = await new SidecarClient(sidecarUrl).health();
      return health.version !== this.expectedVersion;
    } catch {
      return true; // svarar men utan version = gammal
    }
  }

  private async replaceStale(sidecarUrl: string): Promise<void> {
    this.channel().appendLine(`[version] sidecaren på ${sidecarUrl} är en annan version än ${this.expectedVersion} - startar om`);
    try { await new SidecarClient(sidecarUrl).shutdown(); } catch { /* gammal utan /shutdown */ }
    this.proc?.kill();
    this.proc = null;
    const deadline = Date.now() + 5000;
    while (Date.now() < deadline && await this.isHealthy(sidecarUrl)) {
      await new Promise(r => setTimeout(r, HEALTH_POLL_INTERVAL_MS));
    }
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
    this.output ??= vscode.window.createOutputChannel('sqldbgr Sidecar');
    return this.output;
  }

  private async startProcess(command: string[]): Promise<void> {
    const [exe, ...args] = command;
    this.channel().appendLine(`[start] ${exe} ${args.join(' ')}`);

    // shell krävs för npx.cmd på Windows; en exe-sökväg ska INTE gå via shell
    // (sökvägar med mellanslag går sönder av shell-quoting)
    this.proc = spawn(exe, args, { shell: process.platform === 'win32' && exe === 'npx' });
    this.ownUrl = null;

    let resolveUrl: (url: string) => void = () => {};
    let rejectUrl: (err: Error) => void = () => {};
    this.urlFromStdout = new Promise<string>((res, rej) => { resolveUrl = res; rejectUrl = rej; });
    this.urlFromStdout.catch(() => { /* hanteras av waitForUrl */ });
    let stdoutBuffer = '';
    this.proc.stdout?.on('data', (d: Buffer) => {
      const text = d.toString();
      this.output?.append(text);
      stdoutBuffer += text;
      const m = /SQLDBGR_SIDECAR_URL=(\S+)/.exec(stdoutBuffer);
      if (m) resolveUrl(m[1].replace(/\/$/, ''));
    });
    this.proc.on('exit', code => rejectUrl(new Error(`sidecar-processen avslutades med kod ${code}`)));
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
          `sidecar-processen avslutades med kod ${this.proc.exitCode} - se output-kanalen "sqldbgr Sidecar".`);
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
