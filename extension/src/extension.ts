import * as vscode from 'vscode';
import { TsqlDebugSession } from './debugAdapter';
import { SidecarManager } from './sidecarManager';

const sidecarManager = new SidecarManager();

export function activate(context: vscode.ExtensionContext) {
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

        if (!config.connectionString) {
          vscode.window.showErrorMessage('tsql-debugger: connectionString saknas i launch-konfigurationen.');
          return undefined;
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

export function deactivate() {}
