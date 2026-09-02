import {
  LoggingDebugSession, InitializedEvent, StoppedEvent, TerminatedEvent,
  OutputEvent, Thread, StackFrame, Source, Scope, Variable, Breakpoint, Handles
} from '@vscode/debugadapter';
import { DebugProtocol } from '@vscode/debugprotocol';
import * as path from 'path';
import { SidecarClient, PausedEvent, LocalVar, OutputEvent as SidecarOutput } from './sidecarClient';
import { BreakpointMapper } from './breakpointMapper';

const THREAD_ID = 1;
const MAX_ROW_SUMMARY_LENGTH = 80;

type TableRow = Record<string, unknown>;

// Vad ett variablesReference pekar på: Locals-scopet, en TABLE-variabels
// rader, eller kolumnerna i en enskild rad.
type VariableContainer =
  | { kind: 'locals' }
  | { kind: 'rows'; rows: TableRow[] }
  | { kind: 'row'; row: TableRow };

interface TsqlLaunchArgs extends DebugProtocol.LaunchRequestArguments {
  program: string;
  connectionString: string;
  mode?: 'invoke' | 'module' | 'attach';
  params?: Record<string, unknown>;
  sidecarUrl?: string;
  stopOnEntry?: boolean;
}

export class TsqlDebugSession extends LoggingDebugSession {
  private sidecar!: SidecarClient;
  private mapper = new BreakpointMapper();
  private programPath = '';
  private currentStack: PausedEvent['stack'] = [];
  private variableHandles = new Handles<VariableContainer>();
  private stopOnEntry = false;

  protected initializeRequest(response: DebugProtocol.InitializeResponse): void {
    response.body = {
      supportsConfigurationDoneRequest: true,
      supportsConditionalBreakpoints: false, // TODO: fas 2
      supportsEvaluateForHovers: false
    };
    this.sendResponse(response);
    // InitializedEvent skickas först när sidecaren parsat filen (i launchRequest):
    // VS Code svarar på den med setBreakpoints, och då måste mappern vara laddad
    // och sessionen finnas - annars tappas breakpoints satta före F5.
  }

  protected async launchRequest(
    response: DebugProtocol.LaunchResponse, args: TsqlLaunchArgs
  ): Promise<void> {
    this.programPath = args.program;
    this.sidecar = new SidecarClient(args.sidecarUrl ?? 'http://localhost:5199');

    this.sidecar.on('paused', (e: PausedEvent) => {
      this.currentStack = e.stack;
      this.variableHandles.reset(); // gamla referenser är ogiltiga vid nytt stopp
      this.sendEvent(new StoppedEvent(e.reason, THREAD_ID, e.text ?? undefined));
    });
    this.sidecar.on('output', (o: SidecarOutput) => {
      this.sendEvent(new OutputEvent(o.text.endsWith('\n') ? o.text : o.text + '\n', o.category));
    });
    this.sidecar.on('terminated', () => this.sendEvent(new TerminatedEvent()));
    this.sidecar.on('error', (msg: string) => {
      this.sendEvent(new OutputEvent(`[sidecar] ${msg}\n`, 'stderr'));
      this.sendEvent(new TerminatedEvent());
    });

    try {
      const parsed = await this.sidecar.startSession({
        programPath: args.program,
        connectionString: args.connectionString,
        mode: args.mode ?? 'invoke',
        params: args.params ?? {}
      });
      this.mapper.load(args.program, parsed.lineMap);
      this.stopOnEntry = args.stopOnEntry === true;
      this.sendResponse(response);
      // Nu kan breakpoints tas emot; configurationDone startar körningen.
      this.sendEvent(new InitializedEvent());
    } catch (err) {
      this.sendErrorResponse(response, 1001,
        `Kunde inte starta debug-session: ${(err as Error).message}`);
    }
  }

  protected async configurationDoneRequest(
    response: DebugProtocol.ConfigurationDoneResponse
  ): Promise<void> {
    try {
      await this.sidecar.run(this.stopOnEntry);
      this.sendResponse(response);
    } catch (err) {
      this.sendErrorResponse(response, 1002,
        `Kunde inte starta körningen: ${(err as Error).message}`);
    }
  }

  protected async setBreakPointsRequest(
    response: DebugProtocol.SetBreakpointsResponse,
    args: DebugProtocol.SetBreakpointsArguments
  ): Promise<void> {
    const requested = args.breakpoints ?? [];
    const verified: Breakpoint[] = [];
    const stmtIds: number[] = [];

    for (const bp of requested) {
      const snapped = this.mapper.snapToStatement(args.source.path!, bp.line);
      if (snapped) {
        verified.push(new Breakpoint(true, snapped.line));
        stmtIds.push(snapped.stmtId);
      } else {
        verified.push(new Breakpoint(false, bp.line));
      }
    }

    // Sidecaren håller breakpoints i minnet tills körningen startar, och
    // uppdaterar Control-raden under pågående session.
    if (this.sidecar) {
      await this.sidecar.setBreakpoints(stmtIds).catch(err =>
        this.sendEvent(new OutputEvent(`[sidecar] breakpoints: ${(err as Error).message}\n`, 'stderr')));
    }

    response.body = { breakpoints: verified };
    this.sendResponse(response);
  }

  protected threadsRequest(response: DebugProtocol.ThreadsResponse): void {
    response.body = { threads: [new Thread(THREAD_ID, 'T-SQL batch')] };
    this.sendResponse(response);
  }

  protected stackTraceRequest(response: DebugProtocol.StackTraceResponse): void {
    response.body = {
      stackFrames: this.currentStack.map((f, i) => {
        const frame = new StackFrame(
          i,
          f.frameName,
          new Source(path.basename(f.sourcePath ?? this.programPath),
                     f.sourcePath ?? this.programPath),
          f.line,
          f.column
        );
        if (f.endLine != null) frame.endLine = f.endLine;
        if (f.endColumn != null) frame.endColumn = f.endColumn;
        return frame;
      }),
      totalFrames: this.currentStack.length
    };
    this.sendResponse(response);
  }

  protected scopesRequest(response: DebugProtocol.ScopesResponse): void {
    response.body = {
      scopes: [new Scope('Locals', this.variableHandles.create({ kind: 'locals' }), false)]
    };
    this.sendResponse(response);
  }

  protected async variablesRequest(
    response: DebugProtocol.VariablesResponse,
    args: DebugProtocol.VariablesArguments
  ): Promise<void> {
    const container = this.variableHandles.get(args.variablesReference);
    if (!container) {
      response.body = { variables: [] };
      this.sendResponse(response);
      return;
    }

    switch (container.kind) {
      case 'locals': {
        const locals: LocalVar[] = await this.sidecar.getLocals();
        response.body = { variables: locals.map(v => this.toVariable(v)) };
        break;
      }
      case 'rows':
        response.body = {
          variables: container.rows.map((row, i) =>
            new Variable(`[${i}]`, this.rowSummary(row),
              this.variableHandles.create({ kind: 'row', row })))
        };
        break;
      case 'row':
        response.body = {
          variables: Object.entries(container.row).map(([column, cell]) =>
            new Variable(column, this.formatCell(cell)))
        };
        break;
    }
    this.sendResponse(response);
  }

  private toVariable(v: LocalVar): Variable {
    if (v.typeName !== 'TABLE') {
      return new Variable(v.name, v.value ?? 'NULL');
    }

    // Sidecaren serialiserar TABLE-variabler som en JSON-array av rader
    // (FOR JSON AUTO); NULL betyder tom tabell.
    const rows = this.parseTableRows(v.value);
    if (rows === null) {
      return new Variable(v.name, v.value ?? 'TABLE (0 rader)');
    }
    const ref = rows.length > 0
      ? this.variableHandles.create({ kind: 'rows', rows })
      : 0;
    return new Variable(v.name, `TABLE (${rows.length} rader)`, ref);
  }

  private parseTableRows(value: string | null): TableRow[] | null {
    if (value == null) return [];
    try {
      const parsed = JSON.parse(value);
      return Array.isArray(parsed) ? (parsed as TableRow[]) : null;
    } catch {
      return null;
    }
  }

  private rowSummary(row: TableRow): string {
    const summary = Object.entries(row)
      .map(([column, cell]) => `${column}=${this.formatCell(cell)}`)
      .join(', ');
    return summary.length > MAX_ROW_SUMMARY_LENGTH
      ? summary.slice(0, MAX_ROW_SUMMARY_LENGTH - 1) + '…'
      : summary;
  }

  private formatCell(cell: unknown): string {
    if (cell === null || cell === undefined) return 'NULL';
    if (typeof cell === 'object') return JSON.stringify(cell);
    return String(cell);
  }

  protected async continueRequest(response: DebugProtocol.ContinueResponse): Promise<void> {
    await this.sidecar.signal('continue');
    this.sendResponse(response);
  }

  protected async nextRequest(response: DebugProtocol.NextResponse): Promise<void> {
    await this.sidecar.signal('stepOver');
    this.sendResponse(response);
  }

  protected async stepInRequest(response: DebugProtocol.StepInResponse): Promise<void> {
    await this.sidecar.signal('stepIn');
    this.sendResponse(response);
  }

  protected async disconnectRequest(response: DebugProtocol.DisconnectResponse): Promise<void> {
    await this.sidecar?.stopSession().catch(() => {});
    this.sendResponse(response);
  }
}
