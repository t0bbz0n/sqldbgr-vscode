import { ChildProcess, spawn } from 'child_process';
import * as http from 'http';
import * as vscode from 'vscode';

const DEFAULT_COMMAND = ['npx', '-y', 'tsql-debugger-sidecar'];
const HEALTH_PROBE_TIMEOUT_MS = 1000;
const HEALTH_POLL_INTERVAL_MS = 250;
// Generöst: första npx-körningen laddar ner paketet.
const STARTUP_TIMEOUT_MS = 60_000;

/**
 * Ser till att sidecaren kör innan en debug-session startar. Om inget svarar
 * på /health spawnas den (via npx som default) och processen ägs av
 * extensionen: den städas undan vid deactivate. En sidecar som användaren
 * startat själv rörs aldrig - då svarar health-proben och vi spawnar inget.
 */
export class SidecarManager implements vscode.Disposable {
  private proc: ChildProcess | null = null;
  private output: vscode.OutputChannel | null = null;

  async ensureRunning(sidecarUrl: string, command?: string[]): Promise<void> {
    if (await this.isHealthy(sidecarUrl)) return;

    if (!this.proc || this.proc.exitCode !== null) {
      this.startProcess(sidecarUrl, command);
    }
    await this.waitForHealthy(sidecarUrl);
  }

  dispose(): void {
    this.proc?.kill();
    this.proc = null;
    this.output?.dispose();
    this.output = null;
  }

  private startProcess(sidecarUrl: string, command?: string[]): void {
    const port = new URL(sidecarUrl).port || '5199';
    const [exe, ...args] = command?.length ? [...command] : [...DEFAULT_COMMAND];
    args.push('--port', port);

    this.output ??= vscode.window.createOutputChannel('T-SQL Debugger Sidecar');
    this.output.appendLine(`[start] ${exe} ${args.join(' ')}`);

    // shell krävs för npx.cmd på Windows
    this.proc = spawn(exe, args, { shell: process.platform === 'win32' });
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
