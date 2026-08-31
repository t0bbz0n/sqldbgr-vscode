import * as vscode from 'vscode';
import { TsqlDebugSession } from './debugAdapter';
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

        // --- LICENSED FEATURE BOUNDARY ---
        // Remote/attach-gating läggs här senare. Lokal debugging går ALDRIG genom licenskod.
        return config;
      }
    })
  );
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
