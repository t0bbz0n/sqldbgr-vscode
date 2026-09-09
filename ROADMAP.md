# Roadmap: sqldbgr

The second pass over the codebase, after phase 1. Ordered in phases; S/M/L is
the estimated effort (hours / a day / several days). Items marked *Bug* are
wrong behaviour today; the rest are improvements.

## Phase 1: reliability ✅

Done (commits `1b9a242`, `26e35df`): the breakpoint handshake and
`stopOnEntry`, an abort that kills the batch, pause-before semantics,
PRINT and results in the Debug Console, SQL errors mapped back to the original
line with a stop on exception, the final state in module mode, USE-safe schema
qualification, and a version check on a sidecar left behind.

## Phase 2: bugs found in the second pass ✅

*Done. The items are kept as a description of what was changed.*

1. **Repeated stops on the same statement were missed.** `[S]` *Bug.* The
   monitor recognised a new pause by `PausedAtStmt` changing. A `WHILE` whose
   body is a single statement pauses on the same id every turn, and once the
   gap between resume and the next pause was shorter than the polling interval
   (50 ms) the monitor never saw the `NULL` in between and sent no new paused
   event. VS Code showed "running" while SQL stood still. Fixed with a counter,
   `PauseSeq` in `Control`, that `__dbg.Pause` steps up and the monitor
   compares instead of the statement id.
2. **Module parameters were bound as NVARCHAR.** `[S]` *Bug.* The panel's
   values are sent as strings, and `AddWithValue` made them `NVARCHAR`. In a
   procedure with `@a INT, @b INT`, `@a + @b` became string concatenation
   (`'5' + '10' = '510'`), and a `@d DATETIME` compared as text. Fixed without
   type mapping in C#: bind as `@__p_<name>` and let the prelude declare
   `DECLARE @a INT = @__p_a;` using the type text from the signature. SQL
   Server does the conversion, and a conversion failure shows up as an ordinary
   SQL error on the right line. `SET DATEFORMAT ymd` in the prelude keeps ISO
   dates unambiguous.
3. **Multi-statement table functions crashed in module mode.** `[S]` *Bug.*
   `RETURNS @t TABLE (...)` gives a `ReturnType` of table type; we declared
   `@__dbg_return SQL_VARIANT` and the body's `INSERT INTO @t` failed to
   compile. Fixed by declaring `@t` from the return definition, registering it
   as a table variable in Locals and reporting it as the result.
4. **Character encoding.** `[S]` *Bug.* The file was read as UTF-8. An older
   `.sql` in Windows-1252 produced mojibake in string literals and PRINT, and
   that is what was executed against the database. Fixed with strict UTF-8 and
   a fallback to 1252 (`System.Text.Encoding.CodePages`); alternatively the
   extension could send `files.encoding`.
5. **One sidecar was shared by every VS Code window.** `[M]` On a fixed port
   5199, window B with a newer extension replaced the sidecar window A was
   debugging against, thanks to the version check, and closing A made `dispose`
   kill B's sidecar. Fixed: each window starts its own sidecar on `--port 0`, a
   random port; the sidecar writes the chosen port to stdout and the manager
   reads it. `sidecarUrl` becomes a plain override for a sidecar you started
   yourself. This removes the whole class of stale-sidecar problems.
6. **Capture per statement cost something even without a pause.** `[M]` Every
   statement ran a `DELETE`, an `INSERT` per variable, a `FOR JSON` per table
   variable and an `EXEC Pause`, even on Continue with no breakpoints. A loop
   with ten thousand turns became very slow. Fixed with one `INSERT ...
   VALUES (…),(…)` for all scalars, which is cheap and needed for the exception
   stop, and table variables only when there is actually a pause:
   `IF [Db].__dbg.ShouldPause(@stmt_id) = 1 BEGIN <table capture>; EXEC Pause
   END`, where `ShouldPause` is a scalar function reading `Control`.

## Phase 3: everyday experience ✅

*Done: restart without being asked again plus `moduleFiles`, span mapping,
hover and Watch, the Pause button, CodeLens with commands and snippets, parse
errors in Problems, Locals polish (ordering, ISO dates, hex, TABLE(n) with TOP
100), results at full width, mssql profiles with SecretStorage, temp tables,
and the sidecar log.*

- **Restart without being asked again.** `[S]` Ctrl+Shift+F5 went through
  `resolveDebugConfiguration` again, so the QuickPick and parameter panel
  reappeared. Implement `supportsRestartRequest` in the adapter, with the same
  arguments, and a setting `sqldbgr.moduleFiles: ask | debug | run`.
- **A breakpoint in the middle of a multi-line statement.** `[S]` Snap-down
  jumped to the *next* statement; map through spans so a line inside hits it.
- **Hover and Watch.** `[S]` An `evaluate` that looks a variable name up in the
  captured locals, and `type` on the DAP variables.
- **The Pause button.** `[S]` `pauseRequest` signals `stepOver`.
- **Discoverability.** `[M]` A CodeLens "▷ Debug with parameters" above
  `CREATE PROCEDURE/FUNCTION`, which needs `onLanguage:sql` activation,
  commands in the palette, and `configurationSnippets`.
- **Parse errors in the Problems panel.** `[S]` Diagnostics rather than a dialog.
- **Locals polish.** `[S]` Declaration order; dates through `CONVERT(…, 126)`,
  which today gives the language-dependent "Jan 31 2024"; `varbinary` as hex
  (style 1); float at full precision (style 3); `@__dbg_return` shown as
  `(return value)`; `TOP n` plus a count for table variables.
- **Result sets at full width.** `[M]` The Debug Console clips at 40 characters
  and 100 rows. An "Open last result set" command, as a virtual document with a
  CSV or markdown table, or a simple grid webview.
- **Connections through the mssql extension, with SecretStorage.** `[M]`
- **Temp tables (`#t`) in Locals.** `[M]`
- **The sidecar log.** `[S]` Warning as the default level.

## Phase 4: new use cases ✅ (except step-into)

*Done: transaction mode, conditional breakpoints with hit counts and logpoints,
setVariable through `__dbg.Overrides`, the heartbeat and cleanup of orphaned
sessions, and `debugDatabase`. Also a redesign of the locking: `Control` is
written only by the sidecar and `PauseState` only by `Pause`, everything read
with NOLOCK, which fixed a latent deadlock when pausing inside a user
transaction. Still to come: step-into for stored procedures `[L]`.*

- **Transaction mode, a dry run.** `[S/M]` `transaction: rollback | commit |
  none`, rolling back at the end or on stop.
- **Conditional breakpoints and logpoints.** `[M]` The sidecar evaluates the
  condition on its own connection against the captured locals, and continues
  automatically.
- **Changing variable values.** `[M]` `setVariable` through `__dbg.Overrides`.
- **Step-into for stored procedures.** `[L]` Virtual source files and a real
  call stack.
- **The heartbeat and orphaned sessions.** `[S]` `LastHeartbeatUtc` was unused;
  `Pause` now gives up after, say, 60 seconds without one.
- **The debug schema in another database.** `[S]` For environments without DDL
  rights.

## Phase 5: security, publishing, quality (partly ✅)

*Done: token auth between the extension and the sidecar, `extension/README.md`
and `CHANGELOG.md`, an English UI with a Swedish translation, unit and
integration tests in CI, and the sidecar log level. Still to come: screenshots
and a GIF, a real publisher id, `@vscode/test-electron`, and attach mode.*

- **Auth between the extension and the sidecar.** `[M]` The sidecar listened on
  127.0.0.1 but without authentication, so any local process could read
  arbitrary files through `/inspect` and start sessions under the user's login.
  The extension now generates a token per start, passes it as an environment
  variable, and every call requires `Authorization: Bearer`.
- **The publishing package.** `[M]` `extension/README.md`, which is what the
  Marketplace shows, a `CHANGELOG.md`, screenshots and a GIF of F5 → pause →
  Locals, and a real `publisher` id.
- **Tests.** ✅ An xunit project with analyzer tests and integration tests
  against `mcr.microsoft.com/mssql/server` in CI: the pause mechanism, loops,
  abort, stopping on an exception, module mode and table functions. Still to
  come: `@vscode/test-electron` for the mapping and the panel. `[S]`
- **Attach mode, the paid part.** `[L]` The mechanism lives in a separate
  repository by decision. This repository holds the extension point
  (`attachProvider.ts`, `attachRequest`, `GET /session/{id}`) and the contract
  in docs/ATTACH-PROTOCOL.md. Left in the other repository: the watch schema
  with its inverted gate and atomic claim, deploying and restoring instrumented
  definitions, the arming UI, and the licence check.

## Deliberate limits

- sqlcmd mode (`:r`, `:setvar`, `GO n`) is not supported.
- "(n rows affected)" is not shown: the counters are polluted by the
  instrumentation's own INSERT and DELETE.
- Pausing inside a *deployed* module is not possible without attach mode, and
  never for a scalar UDF.
- Ad hoc SQL against the paused session's temp tables is not possible, because
  it would be a different session.
