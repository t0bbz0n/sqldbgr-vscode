import { ChildProcess, spawn } from 'child_process';
import { randomBytes } from 'crypto';
import * as fs from 'fs';
import * as http from 'http';
import * as path from 'path';
import * as vscode from 'vscode';
import { SidecarClient } from './sidecarClient';

const NPX_FALLBACK_COMMAND = ['npx', '-y', 'sqldbgr-sidecar'];
const HEALTH_PROBE_TIMEOUT_MS = 1000;
const HEALTH_POLL_INTERVAL_MS = 250;
// Generous: the first run may have to download the .NET runtime or the npx package.
const STARTUP_TIMEOUT_MS = 120_000;
const t = vscode.l10n.t;

export interface SidecarEndpoint { url: string; token?: string; }

/**
 * Makes sure a sidecar is running before a debug session starts, and returns
 * its URL. By default every VS Code window starts its OWN sidecar on a random
 * port (--port 0; the address is read from stdout), so windows can never take
 * over or kill each other's. An explicit `sidecarUrl` is used as given - a
 * sidecar you started yourself - and is started on that port if nothing
 * answers. The process is owned by the extension and cleaned up on deactivate.
 *
 * The start command is chosen in this order:
 *  1. sidecarCommand from the launch configuration, a development override.
 *  2. The sidecar bundled in the VSIX (sidecar-dist/), run against a runtime
 *     the .NET Install Tool extension fetches on first use, falling back to
 *     the system `dotnet`.
 *  3. npx sqldbgr-sidecar, for an unbundled or development environment.
 */
export class SidecarManager implements vscode.Disposable {
  private proc: ChildProcess | null = null;
  private output: vscode.OutputChannel | null = null;
  /** The URL of the sidecar we started ourselves, random port or explicit. */
  private ownUrl: string | null = null;
  private ownToken: string | undefined;
  private urlFromStdout: Promise<string> | null = null;

  constructor(
    private readonly extensionPath: string,
    private readonly extensionId: string,
    private readonly expectedVersion: string
  ) {}

  async ensureRunning(explicitUrl: string | undefined, command?: string[]): Promise<SidecarEndpoint> {
    if (explicitUrl) {
      if (await this.isHealthy(explicitUrl)) {
        // An older sidecar left behind answers healthily but lacks the newer
        // endpoints, so replace it. A development override skips the check,
        // because `dotnet run` stamps no version.
        if (command?.length || !(await this.isStale(explicitUrl))) {
          return { url: explicitUrl, token: this.ownUrl === explicitUrl ? this.ownToken : process.env.SQLDBGR_TOKEN };
        }
        await this.replaceStale(explicitUrl);
      }
      if (!this.isOwnProcessAlive()) {
        await this.startProcess(await this.resolveCommand(new URL(explicitUrl).port || '5199', command));
        this.ownUrl = explicitUrl;
      }
      await this.waitForHealthy(explicitUrl);
      return { url: explicitUrl, token: this.ownToken };
    }

    // One sidecar per window, on a random port.
    if (this.isOwnProcessAlive() && this.ownUrl && await this.isHealthy(this.ownUrl)) {
      return { url: this.ownUrl, token: this.ownToken };
    }
    await this.startProcess(await this.resolveCommand('0', command));
    this.ownUrl = await this.waitForUrl();
    await this.waitForHealthy(this.ownUrl);
    return { url: this.ownUrl, token: this.ownToken };
  }

  private isOwnProcessAlive(): boolean {
    return this.proc !== null && this.proc.exitCode === null;
  }

  /** The sidecar writes "SQLDBGR_SIDECAR_URL=http://127.0.0.1:<port>" once it listens. */
  private async waitForUrl(): Promise<string> {
    const timeout = new Promise<never>((_, reject) => setTimeout(() => {
      this.output?.show(true);
      reject(new Error(t('the sidecar did not report an address within {0}s - see the "sqldbgr Sidecar" output channel.', STARTUP_TIMEOUT_MS / 1000)));
    }, STARTUP_TIMEOUT_MS));
    return Promise.race([this.urlFromStdout!, timeout]);
  }

  private async isStale(sidecarUrl: string): Promise<boolean> {
    try {
      const health = await new SidecarClient(sidecarUrl).health();
      return health.version !== this.expectedVersion;
    } catch {
      return true; // answers, but with no version, so it is old
    }
  }

  private async replaceStale(sidecarUrl: string): Promise<void> {
    this.channel().appendLine(`[version] sidecar at ${sidecarUrl} is not version ${this.expectedVersion} - restarting`);
    try { await new SidecarClient(sidecarUrl).shutdown(); } catch { /* old, with no /shutdown */ }
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
   * Gets an ASP.NET Core 8 runtime through the .NET Install Tool extension
   * (ms-dotnettools.vscode-dotnet-runtime). It downloads the runtime into a
   * private folder on the first call and answers with the path immediately
   * after, so the user does not need .NET installed. Without that extension,
   * for example in the development host, the system `dotnet` is tried.
   */
  private async acquireDotnet(): Promise<string> {
    try {
      const result = await vscode.commands.executeCommand<{ dotnetPath: string } | undefined>(
        'dotnet.acquire',
        { version: '8.0', mode: 'aspnetcore', requestingExtensionId: this.extensionId });
      if (result?.dotnetPath) {
        this.channel().appendLine(`[dotnet] using ${result.dotnetPath}`);
        return result.dotnetPath;
      }
    } catch (err) {
      this.channel().appendLine(
        `[dotnet] could not acquire a runtime via the .NET Install Tool (${(err as Error).message}) - trying the system dotnet.`);
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

    // One auth token per process: only this extension can talk to the sidecar.
    this.ownToken = randomBytes(24).toString('hex');
    // A shell is required for npx.cmd on Windows. An executable path must NOT
    // go through a shell: shell quoting breaks paths containing spaces.
    this.proc = spawn(exe, args, {
      shell: process.platform === 'win32' && exe === 'npx',
      env: { ...process.env, SQLDBGR_TOKEN: this.ownToken }
    });
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
    this.proc.on('exit', code => rejectUrl(new Error(t('the sidecar process exited with code {0}', String(code)))));
    this.proc.stderr?.on('data', (d: Buffer) => this.output?.append(d.toString()));
    this.proc.on('error', err => this.output?.appendLine(`[error] ${err.message}`));
    this.proc.on('exit', code => this.output?.appendLine(`[exited] exit code ${code}`));
  }

  private async waitForHealthy(sidecarUrl: string): Promise<void> {
    const deadline = Date.now() + STARTUP_TIMEOUT_MS;
    while (Date.now() < deadline) {
      if (this.proc && this.proc.exitCode !== null) {
        this.output?.show(true);
        throw new Error(
          t('the sidecar process exited with code {0} - see the "sqldbgr Sidecar" output channel.', String(this.proc.exitCode)));
      }
      if (await this.isHealthy(sidecarUrl)) return;
      await new Promise(r => setTimeout(r, HEALTH_POLL_INTERVAL_MS));
    }
    this.output?.show(true);
    throw new Error(
      t('the sidecar did not answer on {0}/health within {1}s.', sidecarUrl, STARTUP_TIMEOUT_MS / 1000));
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
