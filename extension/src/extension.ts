import * as vscode from 'vscode';
import { TsqlDebugSession } from './debugAdapter';
import { collectParameters } from './parameterPanel';
import { catchSession, resolveAttachProvider } from './attachProvider';
import { getResults, renderMarkdown } from './resultStore';
import { ModuleInfo, SidecarClient } from './sidecarClient';
import { SidecarManager } from './sidecarManager';

const t = vscode.l10n.t;
const SECRET_CONNECTION_KEY = 'sqldbgr.connectionString';
const DEFAULT_SIDECAR_URL = 'http://localhost:5199';

export function activate(context: vscode.ExtensionContext) {
  const sidecarManager = new SidecarManager(
    context.extensionPath, context.extension.id,
    String((context.extension.packageJSON as { version: string }).version));
  context.subscriptions.push(sidecarManager);

  const diagnostics = vscode.languages.createDiagnosticCollection('sqldbgr');
  context.subscriptions.push(diagnostics);

  // An inline debug adapter, running in the extension host process. It is the
  // simplest thing during development, and can be split into its own process
  // later without any API change.
  context.subscriptions.push(
    vscode.debug.registerDebugAdapterDescriptorFactory('tsql', {
      createDebugAdapterDescriptor: () =>
        new vscode.DebugAdapterInlineImplementation(new TsqlDebugSession(
          // The session is over by the time this fires, so a notification is
          // the only place the reason is still visible. The message comes from
          // the sidecar in English, so there is nothing here to localise.
          message => vscode.window.showErrorMessage(`sqldbgr: ${message}`)))
    })
  );

  // Without this, VS Code picks the word under the pointer using its own word
  // boundary, and that does not count '@' as part of the word: hovering over
  // @sMotnr asked for "sMotnr", which is never in scope. The same goes for the
  // '#' on temp tables.
  context.subscriptions.push(
    vscode.languages.registerEvaluatableExpressionProvider('sql', {
      provideEvaluatableExpression(document, position) {
        const line = document.lineAt(position.line).text;
        // Variable names: @x, @@ROWCOUNT, #tmp, ##global. Search outwards from
        // the pointer rather than trusting the word boundary.
        let start = position.character;
        while (start > 0 && /[\w$#@]/.test(line[start - 1])) start--;
        let end = position.character;
        while (end < line.length && /[\w$#@]/.test(line[end])) end++;
        if (start === end) return undefined;

        const word = line.slice(start, end);
        // The prefix has to come first: "a@b" is not a variable.
        if (!/^[@#][\w$#@]*$/.test(word)) return undefined;
        return new vscode.EvaluatableExpression(
          new vscode.Range(position.line, start, position.line, end), word);
      }
    })
  );

  context.subscriptions.push(
    vscode.debug.registerDebugConfigurationProvider('tsql', {
      async resolveDebugConfiguration(_folder, config) {
        // The quick path: F5 in an open .sql file with no launch.json.
        if (!config.type && !config.request && !config.name) {
          const editor = vscode.window.activeTextEditor;
          if (editor?.document.languageId === 'sql') {
            config.type = 'tsql';
            config.request = 'launch';
            config.name = 'Debug T-SQL';
            config.program = editor.document.fileName;
          }
        }
        // Attach: a separate licensed extension owns the mechanism, see
        // docs/ATTACH-PROTOCOL.md. It catches a running session and we connect
        // to it. No sidecar of our own, and nothing of our own is run.
        const isAttach = config.request === 'attach' || config.mode === 'attach';
        if (isAttach) return resolveAttachConfiguration(config);

        if (!config.program) {
          vscode.window.showErrorMessage(t('sqldbgr: open a .sql file to debug, or set "program" in launch.json.'));
          return undefined;
        }

        // connectionString: launch config -> SecretStorage -> settings -> ask.
        config.connectionString ||= await resolveConnectionString(context);
        if (!config.connectionString) return undefined;

        // The sidecar: one per window on a random port, or the sidecarUrl given.
        // The adapter gets the actual address and the auth token through config.
        if (config.autoStartSidecar !== false) {
          try {
            const sidecar = await vscode.window.withProgress(
              { location: vscode.ProgressLocation.Notification, title: t('Starting sqldbgr sidecar…') },
              () => sidecarManager.ensureRunning(config.sidecarUrl, config.sidecarCommand));
            config.sidecarUrl = sidecar.url;
            config.sidecarToken = sidecar.token;
          } catch (err) {
            vscode.window.showErrorMessage(`sqldbgr: ${(err as Error).message}`);
            return undefined;
          }
        }
        config.sidecarUrl ??= DEFAULT_SIDECAR_URL; // autoStartSidecar: false, with no URL of its own

        const proceed = await inspectAndPrepare(context, config, diagnostics);
        if (!proceed) return undefined;

        return config;
      }
    })
  );

  registerCommands(context);
  context.subscriptions.push(
    vscode.languages.registerCodeLensProvider({ language: 'sql' }, new ModuleCodeLensProvider()));
}

/**
 * --- LICENSED FEATURE BOUNDARY ---
 * Everything attach-specific sits behind this call, in a separate extension
 * that owns the licence check, choosing the module, deploying the instrumented
 * definition and restoring it. Local debugging never comes this way, and works
 * unchanged when that extension is not installed.
 */
async function resolveAttachConfiguration(
  config: vscode.DebugConfiguration
): Promise<vscode.DebugConfiguration | undefined> {
  if (!config.connectionString) {
    vscode.window.showErrorMessage(
      t('sqldbgr: attach mode needs a "connectionString" in the launch configuration.'));
    return undefined;
  }
  const provider = await resolveAttachProvider();
  if (!provider) return undefined;

  const caught = await catchSession(provider, {
    connectionString: config.connectionString,
    program: config.program,
    debugDatabase: config.debugDatabase
  });
  if (!caught) return undefined; // cancelled, or nothing caught - start no session

  config.request = 'attach';
  config.mode = 'attach';
  config.program = caught.program;
  config.sidecarUrl = caught.sidecarUrl;
  config.sidecarToken = caught.sidecarToken;
  config.attachSessionId = caught.sessionId;
  config.autoStartSidecar = false; // the provider owns its own sidecar
  return config;
}

/**
 * Parse errors go to the Problems panel, and the launch is cancelled. If the
 * file holds a CREATE FUNCTION/PROCEDURE, offer to debug the body with
 * parameters - module mode - according to the sqldbgr.moduleFiles setting.
 */
async function inspectAndPrepare(
  context: vscode.ExtensionContext,
  config: vscode.DebugConfiguration,
  diagnostics: vscode.DiagnosticCollection
): Promise<boolean> {
  const uri = vscode.Uri.file(config.program);
  let module: ModuleInfo | null;
  try {
    const result = await new SidecarClient(config.sidecarUrl, config.sidecarToken).inspect(config.program);
    diagnostics.set(uri, result.parseErrors.map(e => new vscode.Diagnostic(
      new vscode.Range(Math.max(e.line - 1, 0), Math.max(e.column - 1, 0), Math.max(e.line - 1, 0), Number.MAX_SAFE_INTEGER),
      e.message, vscode.DiagnosticSeverity.Error)));
    if (result.parseErrors.length > 0) {
      vscode.window.showErrorMessage(
        t('sqldbgr: {0} could not be parsed ({1} error(s)) - see the Problems panel.', vscode.workspace.asRelativePath(uri), result.parseErrors.length));
      return false;
    }
    module = result.module;
  } catch {
    return true; // sidecar down or old - carry on as an ordinary script
  }
  if (!module) return true;

  if (config.mode !== 'module') {
    const preference = vscode.workspace.getConfiguration('sqldbgr').get<'ask' | 'debug' | 'run'>('moduleFiles', 'ask');
    if (preference === 'run') return true;
    if (preference === 'debug' && module.canScriptify) {
      config.mode = 'module';
    } else {
      const kindLabel = module.kind === 'function' ? t('function') : t('procedure');
      const choice = await vscode.window.showQuickPick(
        [
          ...(module.canScriptify ? [{
            label: `$(debug-alt) ${t('Debug the {0} {1}', kindLabel, module.name)}`,
            description: t('the body runs as a script with your parameter values'),
            value: 'module' as const
          }] : []),
          {
            label: `$(run) ${t('Run the script as is')}`,
            description: module.canScriptify
              ? t('CREATE/GRANT run; no pauses inside the module body')
              : module.reason ?? undefined,
            value: 'script' as const
          },
          ...(module.canScriptify ? [{
            label: `$(check) ${t('Always debug the body')}`,
            description: t('stop asking; change it under sqldbgr.moduleFiles'),
            value: 'always' as const
          }] : [])
        ],
        { title: t('{0}: how do you want to run?', module.name), ignoreFocusOut: true });
      if (!choice) return false;
      if (choice.value === 'always') {
        // Global rather than per-file: the question is about a habit, and a
        // per-file memory would go stale as soon as the file is renamed.
        await vscode.workspace.getConfiguration('sqldbgr')
          .update('moduleFiles', 'debug', vscode.ConfigurationTarget.Global);
      } else if (choice.value !== 'module') {
        return true;
      }
      config.mode = 'module';
    }
  }

  // The parameter panel shows every parameter in a form, prefilled from the
  // launch config, the last values used and the declared defaults.
  if (module.parameters.length > 0) {
    const values = await collectParameters(context, module, config.params ?? {});
    if (values === undefined) return false; // closed or cancelled - abandon the launch
    config.params = values;
  }
  return true;
}

/** SecretStorage -> settings -> the mssql extension's profiles, or an input box. */
async function resolveConnectionString(context: vscode.ExtensionContext): Promise<string | undefined> {
  const stored = await context.secrets.get(SECRET_CONNECTION_KEY)
    || vscode.workspace.getConfiguration('sqldbgr').get<string>('connectionString');
  if (stored) return stored;

  const mssql = vscode.extensions.getExtension('ms-mssql.mssql');
  const pickFromMssql = { label: `$(database) ${t('Pick a connection from the SQL Server (mssql) extension')}`, value: 'mssql' as const };
  const enterManually = { label: `$(edit) ${t('Enter a connection string')}`, value: 'manual' as const };
  const choice = mssql
    ? await vscode.window.showQuickPick([pickFromMssql, enterManually], { title: t('sqldbgr: choose a SQL Server connection'), ignoreFocusOut: true })
    : enterManually;
  if (!choice) return undefined;

  let connectionString: string | undefined;
  if (choice.value === 'mssql') {
    connectionString = await connectionStringFromMssql(mssql!);
    if (!connectionString) return undefined;
  } else {
    connectionString = await vscode.window.showInputBox({
      title: t('sqldbgr: connection string'),
      prompt: t('SQL Server connection string for the debug session'),
      value: 'Server=(localdb)\\MSSQLLocalDB;Database=MyDb;Integrated Security=true;TrustServerCertificate=true',
      ignoreFocusOut: true
    });
    if (!connectionString) return undefined;
  }
  offerToSaveConnectionString(context, connectionString);
  return connectionString;
}

/**
 * ms-mssql.mssql exponerar promptForConnection/createConnectionDetails/
 * getConnectionString. That API is not formally stable, hence the caution.
 */
async function connectionStringFromMssql(mssql: vscode.Extension<unknown>): Promise<string | undefined> {
  try {
    const api = (mssql.isActive ? mssql.exports : await mssql.activate()) as {
      promptForConnection(ignoreFocusOut?: boolean): Promise<unknown>;
      createConnectionDetails(info: unknown): unknown;
      getConnectionString(details: unknown, includePassword?: boolean, includeApplicationName?: boolean): Promise<string>;
    };
    const info = await api.promptForConnection(true);
    if (!info) return undefined;
    return await api.getConnectionString(api.createConnectionDetails(info), true, false);
  } catch (err) {
    vscode.window.showErrorMessage(t('sqldbgr: could not get a connection from the mssql extension: {0}', (err as Error).message));
    return undefined;
  }
}

// Fire and forget, so the launch is not blocked by the question. Passwords
// belong in SecretStorage, not in settings.json.
function offerToSaveConnectionString(context: vscode.ExtensionContext, connectionString: string): void {
  const secure = t('Save securely');
  const workspace = t('Save in workspace settings');
  void vscode.window.showInformationMessage(
    t('Save the connection string so you are not asked next time?'), secure, workspace
  ).then(choice => {
    if (choice === secure) {
      void context.secrets.store(SECRET_CONNECTION_KEY, connectionString);
    } else if (choice === workspace) {
      void vscode.workspace.getConfiguration('sqldbgr')
        .update('connectionString', connectionString, vscode.ConfigurationTarget.Workspace);
    }
  });
}

function registerCommands(context: vscode.ExtensionContext): void {
  const startDebugging = (program: string, extra: Record<string, unknown> = {}) =>
    vscode.debug.startDebugging(
      vscode.workspace.getWorkspaceFolder(vscode.Uri.file(program)),
      { type: 'tsql', request: 'launch', name: 'Debug T-SQL', program, ...extra });

  context.subscriptions.push(
    // Attach needed a hand-written launch.json with mode: "attach" and a
    // connection string. Everything it needs is resolvable from here, so the
    // command asks for nothing the launch path would not have asked for.
    vscode.commands.registerCommand('sqldbgr.attachToRunningModule', async () => {
      const connectionString = await resolveConnectionString(context);
      if (!connectionString) return;
      await vscode.debug.startDebugging(undefined, {
        type: 'tsql',
        request: 'attach',
        name: t('Attach to a running module'),
        mode: 'attach',
        connectionString
      });
    }),
    vscode.commands.registerCommand('sqldbgr.debugCurrentFile', () => {
      const editor = vscode.window.activeTextEditor;
      if (!editor || editor.document.isUntitled) {
        vscode.window.showErrorMessage(t('sqldbgr: open a saved .sql file first.'));
        return;
      }
      return startDebugging(editor.document.fileName);
    }),
    vscode.commands.registerCommand('sqldbgr.debugModule', (uri?: vscode.Uri) => {
      const program = uri?.fsPath ?? vscode.window.activeTextEditor?.document.fileName;
      if (!program) return;
      return startDebugging(program, { mode: 'module' });
    }),
    vscode.commands.registerCommand('sqldbgr.openLastResults', async () => {
      const results = getResults();
      if (results.length === 0) {
        vscode.window.showInformationMessage(t('sqldbgr: no result sets from the last debug session.'));
        return;
      }
      const doc = await vscode.workspace.openTextDocument({ language: 'markdown', content: renderMarkdown(results) });
      await vscode.window.showTextDocument(doc, { preview: false });
    }),
    vscode.commands.registerCommand('sqldbgr.forgetConnection', async () => {
      await context.secrets.delete(SECRET_CONNECTION_KEY);
      await vscode.workspace.getConfiguration('sqldbgr').update('connectionString', undefined, vscode.ConfigurationTarget.Workspace);
      vscode.window.showInformationMessage(t('sqldbgr: saved connection string removed.'));
    })
  );
}

/** "Debug with parameters", above every CREATE PROCEDURE/FUNCTION. */
class ModuleCodeLensProvider implements vscode.CodeLensProvider {
  private static readonly pattern = /^\s*CREATE\s+(?:OR\s+ALTER\s+)?(?:PROCEDURE|PROC|FUNCTION)\s+([\w\[\]."]+)/gim;

  provideCodeLenses(document: vscode.TextDocument): vscode.CodeLens[] {
    if (document.isUntitled) return [];
    const lenses: vscode.CodeLens[] = [];
    const text = document.getText();
    for (const m of text.matchAll(ModuleCodeLensProvider.pattern)) {
      const line = document.positionAt(m.index!).line;
      lenses.push(new vscode.CodeLens(new vscode.Range(line, 0, line, 0), {
        title: `$(debug-alt) ${t('Debug {0} with parameters', m[1])}`,
        command: 'sqldbgr.debugModule',
        arguments: [document.uri]
      }));
    }
    return lenses;
  }
}

export function deactivate() {}
