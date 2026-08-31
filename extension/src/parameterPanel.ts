import * as vscode from 'vscode';
import { ModuleInfo } from './sidecarClient';

type ParamValues = Record<string, string | null>;

/**
 * Visar en webview-panel med alla modulparametrar i ett formulär och
 * resolvar med värdena (null = NULL) när användaren startar, eller
 * undefined om panelen stängs/avbryts.
 *
 * Förifyllnad per parameter: launch-konfigens params -> senast använda
 * värden (workspaceState) -> deklarerat default i signaturen.
 */
export function collectParameters(
  context: vscode.ExtensionContext,
  module: ModuleInfo,
  existing: Record<string, unknown>
): Promise<ParamValues | undefined> {
  const stateKey = `tsql-debugger.lastParams:${module.name}`;
  const lastUsed = context.workspaceState.get<ParamValues>(stateKey, {});

  const panel = vscode.window.createWebviewPanel(
    'tsqlParameterPanel',
    `Parametrar: ${module.name}`,
    vscode.ViewColumn.Active,
    { enableScripts: true, localResourceRoots: [] }
  );

  return new Promise<ParamValues | undefined>(resolve => {
    let settled = false;
    const settle = (values: ParamValues | undefined) => {
      if (settled) return;
      settled = true;
      resolve(values);
    };

    panel.webview.onDidReceiveMessage(
      (msg: { command: string; values?: Record<string, { value: string; isNull: boolean }> }) => {
        if (msg.command === 'submit' && msg.values) {
          const values: ParamValues = {};
          for (const [name, v] of Object.entries(msg.values)) {
            values[name] = v.isNull ? null : v.value;
          }
          void context.workspaceState.update(stateKey, values);
          settle(values);
          panel.dispose();
        } else if (msg.command === 'cancel') {
          panel.dispose();
        }
      },
      undefined, context.subscriptions);

    panel.onDidDispose(() => settle(undefined), undefined, context.subscriptions);

    panel.webview.html = renderHtml(module, prefill(module, existing, lastUsed));
  });
}

function prefill(
  module: ModuleInfo,
  existing: Record<string, unknown>,
  lastUsed: ParamValues
): Map<string, { value: string; isNull: boolean }> {
  const result = new Map<string, { value: string; isNull: boolean }>();
  for (const p of module.parameters) {
    if (p.name in existing) {
      const v = existing[p.name];
      result.set(p.name, { value: v === null ? '' : String(v), isNull: v === null });
    } else if (p.name in lastUsed) {
      const v = lastUsed[p.name];
      result.set(p.name, { value: v ?? '', isNull: v === null });
    } else {
      result.set(p.name, { value: stripLiteralQuotes(p.defaultValue), isNull: false });
    }
  }
  return result;
}

/** Deklarerade defaults är literal-text: N'x' / 'x' -> x, annars som den är. */
function stripLiteralQuotes(literal: string | null): string {
  if (literal === null) return '';
  const m = /^N?'(.*)'$/s.exec(literal.trim());
  return m ? m[1].replace(/''/g, "'") : literal.trim();
}

function renderHtml(
  module: ModuleInfo,
  values: Map<string, { value: string; isNull: boolean }>
): string {
  const nonce = getNonce();
  const kindLabel = module.kind === 'function' ? 'Funktion' : 'Procedur';

  const rows = module.parameters.map((p, i) => {
    const v = values.get(p.name) ?? { value: '', isNull: false };
    return `
      <div class="param">
        <label for="p${i}">${escapeHtml(p.name)}<span class="type">${escapeHtml(p.typeName)}</span></label>
        <input type="text" id="p${i}" data-name="${escapeHtml(p.name)}"
               value="${escapeHtml(v.value)}" ${v.isNull ? 'disabled' : ''}
               ${i === 0 ? 'autofocus' : ''} />
        <div class="null-row">
          <input type="checkbox" id="n${i}" data-for="p${i}" ${v.isNull ? 'checked' : ''} />
          <label for="n${i}">NULL</label>
          ${p.defaultValue !== null ? `<span class="default">default: ${escapeHtml(p.defaultValue)}</span>` : ''}
        </div>
      </div>`;
  }).join('');

  return `<!DOCTYPE html>
<html lang="sv">
<head>
  <meta charset="UTF-8">
  <meta http-equiv="Content-Security-Policy"
        content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}';">
  <style>
    body {
      font-family: var(--vscode-font-family);
      color: var(--vscode-foreground);
      padding: 20px;
      max-width: 560px;
    }
    h2 { margin: 0 0 2px; font-size: 1.2em; }
    .subtitle { opacity: 0.7; margin-bottom: 18px; }
    .param { margin-bottom: 14px; }
    .param > label { display: block; margin-bottom: 4px; font-weight: 600; }
    .type { opacity: 0.7; font-weight: normal; margin-left: 8px; font-size: 0.9em; }
    input[type="text"] {
      width: 100%; box-sizing: border-box; padding: 5px 7px;
      background: var(--vscode-input-background);
      color: var(--vscode-input-foreground);
      border: 1px solid var(--vscode-input-border, transparent);
      border-radius: 2px;
    }
    input[type="text"]:focus { outline: 1px solid var(--vscode-focusBorder); }
    input[type="text"]:disabled { opacity: 0.45; }
    .null-row {
      display: flex; align-items: center; gap: 5px;
      margin-top: 4px; font-size: 0.9em; opacity: 0.9;
    }
    .default { margin-left: auto; opacity: 0.6; }
    .buttons { display: flex; gap: 8px; margin-top: 22px; }
    button {
      padding: 6px 16px; border: none; border-radius: 2px; cursor: pointer;
      background: var(--vscode-button-background);
      color: var(--vscode-button-foreground);
    }
    button:hover { background: var(--vscode-button-hoverBackground); }
    button.secondary {
      background: var(--vscode-button-secondaryBackground);
      color: var(--vscode-button-secondaryForeground);
    }
    button.secondary:hover { background: var(--vscode-button-secondaryHoverBackground); }
    .hint { margin-top: 14px; font-size: 0.85em; opacity: 0.6; }
  </style>
</head>
<body>
  <h2>${escapeHtml(module.name)}</h2>
  <div class="subtitle">${kindLabel} · ${module.parameters.length} parametrar</div>
  <form id="form">
    ${rows}
    <div class="buttons">
      <button type="submit">Starta debug</button>
      <button type="button" class="secondary" id="cancel">Avbryt</button>
    </div>
  </form>
  <div class="hint">Enter startar · datum anges säkrast som ISO (2024-01-31)</div>
  <script nonce="${nonce}">
    const vscode = acquireVsCodeApi();

    for (const cb of document.querySelectorAll('input[type="checkbox"]')) {
      cb.addEventListener('change', () => {
        const input = document.getElementById(cb.dataset.for);
        input.disabled = cb.checked;
        if (!cb.checked) input.focus();
      });
    }

    document.getElementById('form').addEventListener('submit', e => {
      e.preventDefault();
      const values = {};
      for (const input of document.querySelectorAll('input[type="text"]')) {
        const cb = document.querySelector('input[data-for="' + input.id + '"]');
        values[input.dataset.name] = { value: input.value, isNull: cb.checked };
      }
      vscode.postMessage({ command: 'submit', values });
    });

    document.getElementById('cancel').addEventListener('click',
      () => vscode.postMessage({ command: 'cancel' }));
  </script>
</body>
</html>`;
}

function escapeHtml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

function getNonce(): string {
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  return Array.from({ length: 32 }, () => chars[Math.floor(Math.random() * chars.length)]).join('');
}
