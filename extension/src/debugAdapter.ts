import {
  LoggingDebugSession, InitializedEvent, StoppedEvent, TerminatedEvent,
  OutputEvent, Thread, StackFrame, Source, Scope, Variable, Breakpoint, Handles
} from '@vscode/debugadapter';
import { DebugProtocol } from '@vscode/debugprotocol';
import * as path from 'path';
import {
  SidecarClient, PausedEvent, LocalVar, BreakpointSpec,
  OutputEvent as SidecarOutput, ResultSetEvent
} from './sidecarClient';
import { BreakpointMapper } from './breakpointMapper';
import { addResult, clearResults } from './resultStore';

const THREAD_ID = 1;
const MAX_ROW_SUMMARY_LENGTH = 80;
const RETURN_VARIABLE = '@__dbg_return';

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
  sidecarToken?: string;
  /** Attach: sessionen providern redan fångat; ingen egen körning startas. */
  attachSessionId?: string;
  stopOnEntry?: boolean;
  transaction?: 'none' | 'rollback' | 'commit';
  debugDatabase?: string;
}

export class TsqlDebugSession extends LoggingDebugSession {
  private sidecar!: SidecarClient;
  private mapper = new BreakpointMapper();
  private launchArgs!: TsqlLaunchArgs;
  private currentStack: PausedEvent['stack'] = [];
  private variableHandles = new Handles<VariableContainer>();
  /** Senast satta breakpoints per fil - skickas om vid restart. */
  private breakpointsBySource = new Map<string, DebugProtocol.SourceBreakpoint[]>();

  protected initializeRequest(response: DebugProtocol.InitializeResponse): void {
    response.body = {
      supportsConfigurationDoneRequest: true,
      supportsConditionalBreakpoints: true,
      supportsHitConditionalBreakpoints: true,
      supportsLogPoints: true,
      supportsEvaluateForHovers: true,
      supportsSetVariable: true,
      supportsRestartRequest: true
    };
    this.sendResponse(response);
    // InitializedEvent skickas först när sidecaren parsat filen (i launchRequest):
    // VS Code svarar på den med setBreakpoints, och då måste mappern vara laddad
    // och sessionen finnas - annars tappas breakpoints satta före F5.
  }

  protected async launchRequest(
    response: DebugProtocol.LaunchResponse, args: TsqlLaunchArgs
  ): Promise<void> {
    this.launchArgs = args;
    try {
      await this.startSession();
      this.sendResponse(response);
      // Nu kan breakpoints tas emot; configurationDone startar körningen.
      this.sendEvent(new InitializedEvent());
    } catch (err) {
      this.sendErrorResponse(response, 1001,
        `Could not start debug session: ${(err as Error).message}`);
    }
  }

  /** Parsar filen i sidecaren och kopplar upp eventströmmen (utan att köra). */
  private async startSession(): Promise<void> {
    const args = this.launchArgs;
    this.sidecar = new SidecarClient(args.sidecarUrl ?? 'http://localhost:5199', args.sidecarToken);
    this.currentStack = [];
    this.variableHandles.reset();
    clearResults();

    this.sidecar.on('paused', (e: PausedEvent) => {
      this.currentStack = e.stack;
      this.variableHandles.reset(); // gamla referenser är ogiltiga vid nytt stopp
      this.sendEvent(new StoppedEvent(e.reason, THREAD_ID, e.text ?? undefined));
    });
    this.sidecar.on('output', (o: SidecarOutput) => {
      this.sendEvent(new OutputEvent(o.text.endsWith('\n') ? o.text : o.text + '\n', o.category));
    });
    this.sidecar.on('resultset', (r: ResultSetEvent) => addResult(r));
    this.sidecar.on('terminated', () => this.sendEvent(new TerminatedEvent()));
    this.sidecar.on('error', (msg: string) => {
      this.sendEvent(new OutputEvent(`[sidecar] ${msg}\n`, 'stderr'));
      this.sendEvent(new TerminatedEvent());
    });

    const parsed = args.attachSessionId
      ? await this.sidecar.resumeSession(args.attachSessionId)
      : await this.sidecar.startSession({
          programPath: args.program,
          connectionString: args.connectionString,
          mode: args.mode ?? 'invoke',
          params: args.params ?? {},
          transaction: args.transaction ?? 'none',
          debugDatabase: args.debugDatabase
        });
    this.mapper.load(args.program, parsed.statements);
  }

  /**
   * Attach: attach-providern har redan fångat en körande session som står
   * pausad i sin sidecar. Vi kopplar bara upp oss - därefter är allt
   * (breakpoints, locals, stegning) identiskt med en lokal session.
   */
  protected async attachRequest(
    response: DebugProtocol.AttachResponse, args: TsqlLaunchArgs
  ): Promise<void> {
    this.launchArgs = args;
    if (!args.attachSessionId) {
      this.sendErrorResponse(response, 1004, 'attach: no session was caught');
      return;
    }
    try {
      await this.startSession();
      this.sendResponse(response);
      this.sendEvent(new InitializedEvent());
      // Sessionen står redan pausad; visa den direkt.
      this.sendEvent(new StoppedEvent('pause', THREAD_ID));
    } catch (err) {
      this.sendErrorResponse(response, 1005,
        `Could not attach to the caught session: ${(err as Error).message}`);
    }
  }

  protected async configurationDoneRequest(
    response: DebugProtocol.ConfigurationDoneResponse
  ): Promise<void> {
    try {
      // Attach: sessionen körs redan (och står pausad) - bara breakpoints skickades.
      if (!this.launchArgs.attachSessionId) {
        await this.sidecar.run(this.launchArgs.stopOnEntry === true);
      }
      this.sendResponse(response);
    } catch (err) {
      this.sendErrorResponse(response, 1002,
        `Could not start execution: ${(err as Error).message}`);
    }
  }

  /** Ctrl+Shift+F5: ny session med samma argument och breakpoints, utan omfrågning. */
  protected async restartRequest(response: DebugProtocol.RestartResponse): Promise<void> {
    if (this.launchArgs.attachSessionId) {
      this.sendErrorResponse(response, 1006,
        'Restart is not available when attached to a caught session - detach and arm again.');
      return;
    }
    try {
      await this.sidecar.stopSession().catch(() => {});
      this.sidecar.removeAllListeners();
      await this.startSession();
      for (const [source, bps] of this.breakpointsBySource) {
        await this.sidecar.setBreakpoints(this.mapBreakpoints(source, bps).specs);
      }
      await this.sidecar.run(this.launchArgs.stopOnEntry === true);
      this.sendResponse(response);
    } catch (err) {
      this.sendErrorResponse(response, 1003, `Restart failed: ${(err as Error).message}`);
    }
  }

  protected async setBreakPointsRequest(
    response: DebugProtocol.SetBreakpointsResponse,
    args: DebugProtocol.SetBreakpointsArguments
  ): Promise<void> {
    const source = args.source.path!;
    const requested = args.breakpoints ?? [];
    this.breakpointsBySource.set(source, requested);
    const { verified, specs } = this.mapBreakpoints(source, requested);

    // Sidecaren håller breakpoints i minnet tills körningen startar, och
    // uppdaterar Control-raden under pågående session.
    if (this.sidecar) {
      await this.sidecar.setBreakpoints(specs).catch(err =>
        this.sendEvent(new OutputEvent(`[sidecar] breakpoints: ${(err as Error).message}\n`, 'stderr')));
    }

    response.body = { breakpoints: verified };
    this.sendResponse(response);
  }

  private mapBreakpoints(source: string, requested: DebugProtocol.SourceBreakpoint[]) {
    const verified: Breakpoint[] = [];
    const specs: BreakpointSpec[] = [];
    for (const bp of requested) {
      const snapped = this.mapper.snapToStatement(source, bp.line);
      if (snapped) {
        verified.push(new Breakpoint(true, snapped.line));
        specs.push({
          stmtId: snapped.stmtId,
          condition: bp.condition || null,
          hitCondition: bp.hitCondition || null,
          logMessage: bp.logMessage || null
        });
      } else {
        verified.push(new Breakpoint(false, bp.line));
      }
    }
    return { verified, specs };
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
          new Source(path.basename(f.sourcePath ?? this.launchArgs.program),
                     f.sourcePath ?? this.launchArgs.program),
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

  private toVariable(v: LocalVar): DebugProtocol.Variable {
    const displayName = v.name === RETURN_VARIABLE ? '(return value)' : v.name;
    const tableMatch = /^TABLE(?:\((\d+)\))?$/.exec(v.typeName);
    if (!tableMatch) {
      const variable: DebugProtocol.Variable = new Variable(displayName, v.value ?? 'NULL');
      variable.type = v.typeName;
      variable.evaluateName = v.name;
      return variable;
    }

    // Sidecaren serialiserar TABLE-variabler som en JSON-array av de första
    // raderna (FOR JSON AUTO); NULL betyder tom tabell; TABLE(n) bär totalen.
    const rows = this.parseTableRows(v.value);
    const total = tableMatch[1] !== undefined ? Number(tableMatch[1]) : rows?.length ?? 0;
    if (rows === null) {
      return new Variable(displayName, v.value ?? 'TABLE (0 rows)');
    }
    const ref = rows.length > 0 ? this.variableHandles.create({ kind: 'rows', rows }) : 0;
    const label = rows.length < total
      ? `TABLE (${total} rows, first ${rows.length} shown)`
      : `TABLE (${total} rows)`;
    const variable: DebugProtocol.Variable = new Variable(displayName, label, ref);
    variable.type = 'TABLE';
    variable.evaluateName = v.name;
    return variable;
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

  /** Hover: bara variabelnamn (allt annat ignoreras). Watch/REPL: godtyckligt T-SQL-uttryck. */
  protected async evaluateRequest(
    response: DebugProtocol.EvaluateResponse,
    args: DebugProtocol.EvaluateArguments
  ): Promise<void> {
    const expression = args.expression.trim();
    const isVariable = /^[@#][\w$#@]*$/.test(expression);
    if (args.context === 'hover' && !isVariable) {
      this.sendErrorResponse(response, 2001, 'not a variable');
      return;
    }

    if (isVariable) {
      const local = (await this.sidecar.getLocals()).find(l => l.name.toLowerCase() === expression.toLowerCase());
      if (!local) {
        this.sendErrorResponse(response, 2002, `${expression} is not in scope`);
        return;
      }
      const v = this.toVariable(local);
      response.body = { result: v.value, type: v.type, variablesReference: v.variablesReference };
      this.sendResponse(response);
      return;
    }

    const { value, error } = await this.sidecar.evaluate(expression);
    if (error) {
      this.sendErrorResponse(response, 2003, error);
      return;
    }
    response.body = { result: value ?? 'NULL', variablesReference: 0 };
    this.sendResponse(response);
  }

  /** Nytt värde tar effekt när batchen fortsätter; NULL (skiftlägesokänsligt) sätter NULL. */
  protected async setVariableRequest(
    response: DebugProtocol.SetVariableResponse,
    args: DebugProtocol.SetVariableArguments
  ): Promise<void> {
    const container = this.variableHandles.get(args.variablesReference);
    if (container?.kind !== 'locals') {
      this.sendErrorResponse(response, 2004, 'Only scalar local variables can be changed');
      return;
    }
    const name = args.name === '(return value)' ? RETURN_VARIABLE : args.name;
    const value = args.value.trim().toUpperCase() === 'NULL' ? null : args.value;
    try {
      await this.sidecar.setVariable(name, value);
      response.body = { value: value ?? 'NULL' };
      this.sendResponse(response);
    } catch (err) {
      this.sendErrorResponse(response, 2005, (err as Error).message);
    }
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

  /** Pause-knappen: batchen stannar vid nästa statement. */
  protected async pauseRequest(response: DebugProtocol.PauseResponse): Promise<void> {
    await this.sidecar.signal('stepOver');
    this.sendResponse(response);
  }

  protected async disconnectRequest(response: DebugProtocol.DisconnectResponse): Promise<void> {
    await this.sidecar?.stopSession().catch(() => {});
    this.sendResponse(response);
  }
}
