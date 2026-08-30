import * as vscode from 'vscode';
import { TsqlDebugSession } from './debugAdapter';

export function activate(context: vscode.ExtensionContext) {
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
      resolveDebugConfiguration(_folder, config) {
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

        // --- LICENSED FEATURE BOUNDARY ---
        // Remote/attach-gating läggs här senare. Lokal debugging går ALDRIG genom licenskod.
        return config;
      }
    })
  );
}

export function deactivate() {}
