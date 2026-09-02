import { EventEmitter } from 'events';
import * as http from 'http';

export interface StartSessionRequest {
  programPath: string;
  connectionString: string;
  mode: 'invoke' | 'module' | 'attach';
  params: Record<string, unknown>;
}

export interface ModuleParameter {
  name: string;
  typeName: string;
  defaultValue: string | null;
}

export interface ModuleInfo {
  kind: 'function' | 'procedure';
  name: string;
  parameters: ModuleParameter[];
  canScriptify: boolean;
  reason: string | null;
}

export interface ParseResult {
  sessionId: string;
  // rad (1-baserad) -> statementId, per källa
  lineMap: Array<{ line: number; stmtId: number }>;
}

export interface LocalVar {
  name: string;
  typeName: string;
  value: string | null;
}

export interface StackFrameInfo {
  frameName: string;
  sourcePath: string | null;
  line: number;
  column: number;
  endLine: number | null;
  endColumn: number | null;
}

export interface PausedEvent {
  reason: 'breakpoint' | 'step' | 'entry' | 'exception';
  /** Felmeddelande vid reason === 'exception'. */
  text: string | null;
  stack: StackFrameInfo[];
}

export interface OutputEvent {
  category: 'stdout' | 'stderr';
  text: string;
}

export interface HealthInfo {
  status: string;
  service: string;
  version: string;
}

/**
 * Pratar med sidecaren över HTTP + Server-Sent Events.
 * Events: 'paused' (PausedEvent), 'output' (OutputEvent), 'terminated', 'error' (string)
 */
export class SidecarClient extends EventEmitter {
  private sessionId: string | null = null;
  private sseRequest: http.ClientRequest | null = null;

  constructor(private baseUrl: string) { super(); }

  /** Sessionslös: hittar en ev. funktions-/procedurdefinition i filen. */
  async inspect(programPath: string): Promise<{ module: ModuleInfo | null }> {
    return this.post('/inspect', { programPath });
  }

  /** Parsar och instrumenterar; körningen startar först vid run(). */
  async startSession(req: StartSessionRequest): Promise<ParseResult> {
    const result = await this.post<ParseResult>('/session/start', req);
    this.sessionId = result.sessionId;
    this.openEventStream(result.sessionId);
    return result;
  }

  /** Startar körningen - anropas när alla breakpoints är satta (configurationDone). */
  async run(stopOnEntry: boolean): Promise<void> {
    if (!this.sessionId) return;
    await this.post(`/session/${this.sessionId}/run`, { stopOnEntry });
  }

  async health(): Promise<HealthInfo> {
    return this.get<HealthInfo>('/health');
  }

  /** Ber sidecaren avsluta sig själv (används för att byta ut en äldre version). */
  async shutdown(): Promise<void> {
    await this.post('/shutdown', {});
  }

  async setBreakpoints(stmtIds: number[]): Promise<void> {
    if (!this.sessionId) return;
    await this.post(`/session/${this.sessionId}/breakpoints`, { stmtIds });
  }

  async signal(command: 'continue' | 'stepOver' | 'stepIn'): Promise<void> {
    if (!this.sessionId) return;
    await this.post(`/session/${this.sessionId}/signal`, { command });
  }

  async getLocals(): Promise<LocalVar[]> {
    if (!this.sessionId) return [];
    return this.get<LocalVar[]>(`/session/${this.sessionId}/locals`);
  }

  async stopSession(): Promise<void> {
    if (!this.sessionId) return;
    this.sseRequest?.destroy();
    await this.post(`/session/${this.sessionId}/stop`, {});
    this.sessionId = null;
  }

  private openEventStream(sessionId: string): void {
    const url = new URL(`/session/${sessionId}/events`, this.baseUrl);
    this.sseRequest = http.get(url, res => {
      let buffer = '';
      res.setEncoding('utf8');
      res.on('data', (chunk: string) => {
        buffer += chunk;
        let idx: number;
        while ((idx = buffer.indexOf('\n\n')) >= 0) {
          const raw = buffer.slice(0, idx);
          buffer = buffer.slice(idx + 2);
          this.dispatchSse(raw);
        }
      });
      res.on('end', () => this.emit('terminated'));
    });
    this.sseRequest.on('error', err => this.emit('error', err.message));
  }

  private dispatchSse(raw: string): void {
    let event = 'message';
    let data = '';
    for (const line of raw.split('\n')) {
      if (line.startsWith('event:')) event = line.slice(6).trim();
      else if (line.startsWith('data:')) data += line.slice(5).trim();
    }
    if (!data) return;
    switch (event) {
      case 'paused': this.emit('paused', JSON.parse(data) as PausedEvent); break;
      case 'output': this.emit('output', JSON.parse(data) as OutputEvent); break;
      case 'terminated': this.emit('terminated'); break;
      case 'error': this.emit('error', JSON.parse(data) as string); break;
    }
  }

  private post<T = unknown>(path: string, body: unknown): Promise<T> {
    return this.request<T>('POST', path, body);
  }

  private get<T>(path: string): Promise<T> {
    return this.request<T>('GET', path);
  }

  private request<T>(method: string, path: string, body?: unknown): Promise<T> {
    return new Promise((resolve, reject) => {
      const url = new URL(path, this.baseUrl);
      const payload = body === undefined ? undefined : JSON.stringify(body);
      const req = http.request(url, {
        method,
        headers: payload
          ? { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(payload) }
          : {}
      }, res => {
        let data = '';
        res.setEncoding('utf8');
        res.on('data', c => (data += c));
        res.on('end', () => {
          if (res.statusCode && res.statusCode >= 400) {
            reject(new Error(`Sidecar ${res.statusCode}: ${data}`));
          } else {
            resolve(data ? JSON.parse(data) as T : (undefined as T));
          }
        });
      });
      req.on('error', reject);
      if (payload) req.write(payload);
      req.end();
    });
  }
}
