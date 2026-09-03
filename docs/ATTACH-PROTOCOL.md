# Attach provider protocol

Attach mode — pausing inside a deployed procedure that *someone else's*
session is executing — lives in a **separate extension**, not in this
repository. This repo is MIT and contains only local debugging; it exposes
the extension point described here and nothing more.

This document is the contract that separate extension implements.

## Why the split

The attach mechanism modifies deployed database objects and blocks foreign
sessions. It is licensed separately, so it ships as its own extension with
its own sidecar. The boundary is deliberately narrow: the provider does all
the attach-specific work and hands back a **session that is already caught
and paused**. From that point this extension drives it with exactly the same
debug adapter used for local sessions — breakpoints, stepping, locals,
conditions and logpoints all work unchanged.

The single entry point on this side is `resolveAttachConfiguration()` in
`extension/src/extension.ts`, marked `--- LICENSED FEATURE BOUNDARY ---`.
Local debugging never passes through it.

## 1. Extension API

The provider extension is discovered by id (`sqldbgr.attachExtension`,
default `tobias-trunehag.sqldbgr-attach`), activated, and its `exports` must
satisfy `AttachProviderApi` from `extension/src/attachProvider.ts`:

```ts
interface AttachProviderApi {
  readonly protocolVersion: number;   // must equal ATTACH_PROTOCOL_VERSION (1)
  attach(request: AttachRequest, token: vscode.CancellationToken):
    Promise<AttachSession | undefined>;
}

interface AttachRequest {
  connectionString: string;   // the connection the user picked
  program?: string;           // the open file, if any
  debugDatabase?: string;     // where the __dbg schema should live
}

interface AttachSession {
  sidecarUrl: string;         // provider-owned sidecar speaking section 2
  sidecarToken?: string;      // bearer token, if it requires one
  sessionId: string;          // already created and paused
  program: string;            // file or virtual document for breakpoints
}
```

`attach` resolves when a session has been caught, or `undefined` if the user
cancelled or the watch expired without a catch. Everything in between —
license check, picking the module, filters, deploying the instrumented
definition, arming, catching — is the provider's business and invisible here.

The provider owns all of its UI. It should honour the cancellation token,
since this extension shows a cancellable progress notification while waiting.

## 2. Sidecar HTTP protocol

The provider's sidecar must implement the endpoints this extension calls
after attaching. All requests carry `Authorization: Bearer <sidecarToken>`
when a token was supplied. The reference implementation is `sidecar/` in
this repo; endpoints not listed here are never called on an attached session.

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/session/{id}` | `{ sessionId, program, lineMap, statements }` for the caught session |
| GET | `/session/{id}/events` | SSE stream: `paused`, `output`, `resultset`, `terminated`, `error` |
| POST | `/session/{id}/breakpoints` | `{ breakpoints: BreakpointSpec[] }` |
| POST | `/session/{id}/signal` | `{ command: "continue" \| "stepOver" \| "stepIn" }` |
| GET | `/session/{id}/locals` | `LocalVar[]`, in declaration order |
| POST | `/session/{id}/variables` | `{ name, value }` — setVariable |
| POST | `/session/{id}/evaluate` | `{ expression }` → `{ value, error }` |
| POST | `/session/{id}/stop` | detach: release the caught session |

`lineMap` maps source lines to statement ids; `statements` carries
`{ stmtId, line, endLine }` spans so breakpoints inside multi-line statements
resolve to the containing statement. Payload shapes are the TypeScript
interfaces in `extension/src/sidecarClient.ts` — that file is the normative
definition.

`POST /session/{id}/run` is **not** called on an attached session: the caught
session is already running and paused. Restart is refused by the adapter for
the same reason — detach and arm again.

## 3. Requirements the provider must meet

These are not optional. The mechanism blocks foreign sessions and rewrites
deployed objects, so a provider that skips them can hang application traffic
or leave a shared database instrumented.

- **Restore always.** Keep the original `sys.sql_modules.definition` in a
  registry table and restore it on detach, on session end, and from a sweeper
  when the provider's own process died mid-session. Deploy with
  `ALTER PROCEDURE`, never drop/create — dropping loses permissions.
- **Verify before mapping.** If the deployed definition differs from the file
  the user has open, breakpoints point at the wrong lines. Either refuse, or
  (preferred) fetch the definition from the server and present it as a virtual
  read-only document, which makes line mapping correct by construction.
- **Auto-disarm.** Arm for a single catch or a short time window. An armed
  watch left running pauses every caller.
- **Heartbeat.** A caught foreign session must release itself if the debugger
  goes away. This repo's schema aborts after 60s without a heartbeat
  (error 50098); a provider using its own schema needs the equivalent.
- **Claim atomically.** Exactly one session may be caught per armed watch —
  a conditional update checking the affected row count. All other callers
  must pass through untouched.

## 4. What cannot be done

Scalar and table-valued functions can never pause: UDFs allow no side
effects, so the pause procedure cannot be called from them. Encrypted
(`WITH ENCRYPTION`) and natively compiled modules cannot be instrumented.
Attaching requires `ALTER` permission on the target module.

Note also that `ALTER PROCEDURE` invalidates the cached plan, and that while
a watch is armed every call pays for one row lookup per statement. Keep armed
windows short.
