import * as vscode from 'vscode';

const t = vscode.l10n.t;

/** Protokollversion som den här extensionen talar. Providern måste matcha major. */
export const ATTACH_PROTOCOL_VERSION = 1;
const DEFAULT_PROVIDER_ID = 'tobias-trunehag.sqldbgr-pro';

/** Vad klienten ber om när en körande session ska fångas. */
export interface AttachRequest {
  /** Anslutningen användaren valt (samma som för lokal debugging). */
  connectionString: string;
  /** Filen i editorn, om någon - providern får välja modul själv annars. */
  program?: string;
  /** Databas där __dbg-schemat ska ligga, om användaren styrt det. */
  debugDatabase?: string;
}

/**
 * En session som providern redan har fångat och som står pausad i en sidecar.
 * Klientens debug-adapter kopplar upp sig mot den och kör vidare med samma
 * breakpoints, locals och stegning som för lokala sessioner.
 */
export interface AttachSession {
  /** Sidecar som äger sessionen; talar sidecar-HTTP-protokollet (se docs/ATTACH-PROTOCOL.md). */
  sidecarUrl: string;
  sidecarToken?: string;
  /** Sessionen som redan är skapad och pausad. */
  sessionId: string;
  /** Fil eller virtuellt dokument som breakpoints mappas mot. */
  program: string;
}

/**
 * API:t som attach-extensionen exporterar. Den äger licenskontroll, val av
 * modul, filter, deploy av instrumenterad definition och återställning - allt
 * som skiljer attach från lokal debugging. Den här extensionen kan inte
 * pausa i deployade moduler på egen hand och gör inga försök att göra det.
 */
export interface AttachProviderApi {
  readonly protocolVersion: number;
  /**
   * Armerar en bevakning och resolvar när en session fångats, eller undefined
   * om användaren avbröt eller inget fångades innan bevakningen slog av.
   */
  attach(request: AttachRequest, token: vscode.CancellationToken): Promise<AttachSession | undefined>;
}

/**
 * Hittar och aktiverar attach-providern. Saknas den (vanligaste fallet - den
 * är en separat, licensierad extension) förklaras det en gång; lokal debugging
 * påverkas aldrig av att den inte finns.
 */
export async function resolveAttachProvider(): Promise<AttachProviderApi | undefined> {
  const id = vscode.workspace.getConfiguration('sqldbgr').get<string>('attachExtension') || DEFAULT_PROVIDER_ID;
  const extension = vscode.extensions.getExtension<AttachProviderApi>(id);
  if (!extension) {
    vscode.window.showErrorMessage(
      t('sqldbgr: attach mode needs the separate extension {0}, which is not installed.', id));
    return undefined;
  }

  let api: AttachProviderApi;
  try {
    api = extension.isActive ? extension.exports : await extension.activate();
  } catch (err) {
    vscode.window.showErrorMessage(
      t('sqldbgr: the attach extension could not be activated: {0}', (err as Error).message));
    return undefined;
  }

  if (typeof api?.attach !== 'function') {
    vscode.window.showErrorMessage(t('sqldbgr: {0} does not expose an attach provider.', id));
    return undefined;
  }
  if (Math.trunc(api.protocolVersion) !== ATTACH_PROTOCOL_VERSION) {
    vscode.window.showErrorMessage(t(
      'sqldbgr: the attach extension speaks protocol {0}, this version needs {1} - update one of them.',
      String(api.protocolVersion), String(ATTACH_PROTOCOL_VERSION)));
    return undefined;
  }
  return api;
}

/** Kör providerns attach-flöde med en avbrytbar progress-notis. */
export function catchSession(
  api: AttachProviderApi,
  request: AttachRequest
): Thenable<AttachSession | undefined> {
  return vscode.window.withProgress({
    location: vscode.ProgressLocation.Notification,
    title: t('sqldbgr: waiting for a call to the watched module…'),
    cancellable: true
  }, (_progress, token) => api.attach(request, token));
}
