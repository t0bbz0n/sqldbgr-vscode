import * as vscode from 'vscode';

const t = vscode.l10n.t;

/** The protocol version this extension speaks. The provider must match on major. */
export const ATTACH_PROTOCOL_VERSION = 1;
const DEFAULT_PROVIDER_ID = 'tobias-trunehag.sqldbgr-pro';

/** What the client asks for when a running session is to be caught. */
export interface AttachRequest {
  /** The connection the user chose, the same one local debugging uses. */
  connectionString: string;
  /** The file in the editor, if any. Otherwise the provider picks the module. */
  program?: string;
  /** The database the __dbg schema should live in, if the user chose one. */
  debugDatabase?: string;
}

/**
 * A session the provider has already caught, paused inside a sidecar. The
 * client's debug adapter connects to it and carries on with the same
 * breakpoints, locals and stepping a local session has.
 */
export interface AttachSession {
  /** The sidecar owning the session; it speaks the sidecar HTTP protocol (docs/ATTACH-PROTOCOL.md). */
  sidecarUrl: string;
  sidecarToken?: string;
  /** The session, already created and paused. */
  sessionId: string;
  /** The file, or virtual document, breakpoints are mapped against. */
  program: string;
}

/**
 * The API the attach extension exports. It owns the licence check, choosing
 * the module, filters, deploying the instrumented definition and restoring it
 * afterwards - everything that separates attach from local debugging. This
 * extension cannot pause inside a deployed module on its own, and does not
 * try to.
 */
export interface AttachProviderApi {
  readonly protocolVersion: number;
  /**
   * Arms a watch and resolves once a session is caught, or with undefined if
   * the user cancelled or nothing was caught before the watch expired.
   */
  attach(request: AttachRequest, token: vscode.CancellationToken): Promise<AttachSession | undefined>;
}

/**
 * Finds and activates the attach provider. When it is missing, which is the
 * common case because it is a separate licensed extension, that is explained
 * once. Local debugging is never affected by its absence.
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

/** Runs the provider's attach flow behind a cancellable progress notification. */
export async function catchSession(
  api: AttachProviderApi,
  request: AttachRequest
): Promise<AttachSession | undefined> {
  // No progress notification here: the provider owns the whole flow, and it
  // starts by asking which module to watch. A notification saying "waiting for
  // a call" on top of that picker describes something that has not begun.
  // The token is still ours, so cancelling the debug session cancels the watch.
  const cancellation = new vscode.CancellationTokenSource();
  try {
    return await api.attach(request, cancellation.token);
  } finally {
    cancellation.dispose();
  }
}
