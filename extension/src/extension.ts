import * as vscode from 'vscode';
import { TsqlDebugSession } from './debugAdapter';
import { collectParameters } from './parameterPanel';
import { ModuleInfo, SidecarClient } from './sidecarClient';
import { SidecarManager } from './sidecarManager';

export function activate(context: vscode.ExtensionContext) {
  const sidecarManager = new SidecarManager(context.extensionPath, context.extension.id);
  context.subscriptions.push(sidecarManager);

  // Inline debug adapter - körs i extension host-processen. Enklast under utveckling;
  // kan brytas ut till separat process senare utan API-ändringar.
  context.subscriptions.push(
    vscode.debug.registerDebugAdapterDescriptorFactory('tsql', {
      createDebugAdapterDescriptor: () =>
        new vscode.DebugAdapterInlineImplementation(new TsqlDebugSession())
    })
  );

  context.subscriptions.push(
    vscode.debug.registerDebugConfigurationProvider('tsql', {
      async resolveDebugConfiguration(_folder, config) {
        // Snabbstart: F5 i en öppen .sql-fil utan launch.json
        if (!config.type && !config.request && !config.name) {
          const editor = vscode.window.activeTextEditor;
          if (editor?.document.languageId === 'sql') {
            config.type = 'tsql';
            config.request = 'launch';
            config.name = 'Debug T-SQL';
            config.program = editor.document.fileName;
          }
        }

        // connectionString: launch-konfig -> settings -> fråga användaren
        if (!config.connectionString) {
          config.connectionString = vscode.workspace
            .getConfiguration('tsql-debugger').get<string>('connectionString') || undefined;
        }
        if (!config.connectionString) {
          const entered = await vscode.window.showInputBox({
            title: 'T-SQL Debugger: connection string',
            prompt: 'SQL Server-anslutningssträng för debug-sessionen',
            value: 'Server=(localdb)\\MSSQLLocalDB;Database=MyDb;Integrated Security=true;TrustServerCertificate=true',
            ignoreFocusOut: true
          });
          if (!entered) return undefined; // avbrutet - starta inte sessionen
          config.connectionString = entered;
          offerToSaveConnectionString(entered);
        }

        if (config.autoStartSidecar !== false) {
          const sidecarUrl: string = config.sidecarUrl ?? 'http://localhost:5199';
          try {
            await vscode.window.withProgress(
              { location: vscode.ProgressLocation.Notification, title: 'Startar T-SQL debugger-sidecar…' },
              () => sidecarManager.ensureRunning(sidecarUrl, config.sidecarCommand));
          } catch (err) {
            vscode.window.showErrorMessage(
              `tsql-debugger: ${(err as Error).message}`);
            return undefined;
          }
        }

        // Innehåller filen en CREATE FUNCTION/PROCEDURE? Erbjud att debugga
        // kroppen som script med parametervärden (modulläge).
        if (config.mode !== 'attach' && config.program) {
          const proceed = await resolveModuleDebugging(context, config);
          if (!proceed) return undefined;
        }

        // --- LICENSED FEATURE BOUNDARY ---
        // Remote/attach-gating läggs här senare. Lokal debugging går ALDRIG genom licenskod.
        return config;
      }
    })
  );
}

/**
 * F5 på ett CREATE FUNCTION/PROCEDURE-script: fråga om kroppen ska debuggas
 * (scriptifierad, med parametervärden från parameterpanelen) eller om scriptet
 * ska köras som det är. Returnerar false om användaren avbröt launchen.
 */
async function resolveModuleDebugging(
  context: vscode.ExtensionContext,
  config: vscode.DebugConfiguration
): Promise<boolean> {
  let module: ModuleInfo | null;
  try {
    ({ module } = await new SidecarClient(config.sidecarUrl ?? 'http://localhost:5199')
      .inspect(config.program));
  } catch {
    return true; // sidecaren nere/gammal - kör vidare som vanligt script
  }
  if (!module) return true;

  if (config.mode !== 'module') {
    const kindLabel = module.kind === 'function' ? 'funktionen' : 'proceduren';
    const choice = await vscode.window.showQuickPick(
      [
        ...(module.canScriptify ? [{
          label: `$(debug-alt) Debugga ${kindLabel} ${module.name}`,
          description: 'kroppen körs som script med dina parametervärden',
          value: 'module' as const
        }] : []),
        {
          label: '$(run) Kör scriptet som det är',
          description: module.canScriptify
            ? 'CREATE/GRANT körs; inga pauser i modulkroppen'
            : module.reason ?? undefined,
          value: 'script' as const
        }
      ],
      { title: `${module.name}: hur vill du köra?`, ignoreFocusOut: true });
    if (!choice) return false;
    if (choice.value !== 'module') return true;
    config.mode = 'module';
  }

  // Parameterpanelen visar alla parametrar i ett formulär, förifyllt från
  // launch-konfigen, senast använda värden och deklarerade defaults.
  if (module.parameters.length > 0) {
    const values = await collectParameters(context, module, config.params ?? {});
    if (values === undefined) return false; // stängd/avbruten - avbryt launchen
    config.params = values;
  }
  return true;
}

// Fire-and-forget så launchen inte blockeras av frågan.
function offerToSaveConnectionString(connectionString: string): void {
  void vscode.window.showInformationMessage(
    'Spara anslutningssträngen så du slipper frågan nästa gång?',
    'Spara i workspace', 'Spara globalt'
  ).then(choice => {
    if (!choice) return;
    const target = choice === 'Spara globalt'
      ? vscode.ConfigurationTarget.Global
      : vscode.ConfigurationTarget.Workspace;
    void vscode.workspace.getConfiguration('tsql-debugger')
      .update('connectionString', connectionString, target);
  });
}

export function deactivate() {}
