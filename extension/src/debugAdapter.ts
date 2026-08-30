import {
  LoggingDebugSession, InitializedEvent, StoppedEvent, TerminatedEvent,
  OutputEvent, Thread, StackFrame, Source, Scope, Variable, Breakpoint
} from '@vscode/debugadapter';
import { DebugProtocol } from '@vscode/debugprotocol';
import * as path from 'path';
import { SidecarClient, PausedEvent, LocalVar } from './sidecarClient';
import { BreakpointMapper } from './breakpointMapper';

const THREAD_ID = 1;

interface TsqlLaunchArgs extends DebugProtocol.LaunchRequestArguments {
  program: string;
  connectionString: string;
  mode?: 'invoke' | 'attach';
  params?: Record<string, unknown>;
  sidecarUrl?: string;
}

export class TsqlDebugSession extends LoggingDebugSession {
  private sidecar!: SidecarClient;
  private mapper = new BreakpointMapper();
  private programPath = '';
  private currentStack: PausedEvent['stack'] = [];

  protected initializeRequest(response: DebugProtocol.InitializeResponse): void {
    response.body = {
      supportsConfigurationDoneRequest: true,
      supportsConditionalBreakpoints: false, // TODO: fas 2
      supportsEvaluateForHovers: false
    };
    this.sendResponse(response);
    this.sendEvent(new InitializedEvent());
  }

  protected async launchRequest(
    response: DebugProtocol.LaunchResponse, args: TsqlLaunchArgs
  ): Promise<void> {
    this.programPath = args.program;
    this.sidecar = new SidecarClient(args.sidecarUrl ?? 'http://localhost:5199');

    this.sidecar.on('paused', (e: PausedEvent) => {
      this.currentStack = e.stack;
      this.sendEvent(new StoppedEvent(e.reason, THREAD_ID));
    });
    this.sidecar.on('output', (text: string) => {
      this.sendEvent(new OutputEvent(text + '\n', 'stdout'));
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
      this.sendResponse(response);
    } catch (err) {
      this.sendErrorResponse(response, 1001,
        `Kunde inte starta debug-session: ${(err as Error).message}`);
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

    // Sessionen kan vara ostartad när VS Code skickar breakpoints - mappern
    // cachar dem och launchRequest pushar efter parse.
    if (this.sidecar) {
      await this.sidecar.setBreakpoints(stmtIds).catch(() => { /* pushas vid launch */ });
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
      stackFrames: this.currentStack.map((f, i) =>
        new StackFrame(
          i,
          f.frameName,
          new Source(path.basename(f.sourcePath ?? this.programPath),
                     f.sourcePath ?? this.programPath),
          f.line
        )),
      totalFrames: this.currentStack.length
    };
    this.sendResponse(response);
  }

  protected scopesRequest(response: DebugProtocol.ScopesResponse): void {
    response.body = { scopes: [new Scope('Locals', 1, false)] };
    this.sendResponse(response);
  }

  protected async variablesRequest(
    response: DebugProtocol.VariablesResponse
  ): Promise<void> {
    const locals: LocalVar[] = await this.sidecar.getLocals();
    response.body = {
      variables: locals.map(v =>
        new Variable(v.name, v.value ?? 'NULL', 0))
    };
    this.sendResponse(response);
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
