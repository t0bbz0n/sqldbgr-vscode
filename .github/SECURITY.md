# Security policy

## Reporting a vulnerability

Do not open a public issue. Use GitHub's private reporting:
[Report a vulnerability](https://github.com/t0bbz0n/sqldbgr-vscode/security/advisories/new).

Tell us what an attacker can do and how to reproduce it. You will get a first
response within a week. If a fix is needed, we will agree a disclosure date
with you before publishing.

## Supported versions

The latest release. This is a young project; there are no maintained branches
behind it.

## What this extension does to your database

Worth knowing when you assess risk:

- The debugger creates a `__dbg` schema in the database it debugs, holding the
  session's control rows, captured locals and temp-table snapshots. Point it
  elsewhere with the `debugDatabase` setting if you may not create objects in
  the target database.
- The instrumented script is rewritten before it runs: calls to `__dbg.Pause`
  are spliced in between statements. What executes is not byte-for-byte what
  you wrote.
- A paused statement holds whatever locks it has taken until you continue.
  Debugging against a shared server can block other traffic.
- The sidecar listens on a random loopback port and requires a bearer token
  that is generated per window. It is not intended to be reachable from
  another machine.
- Connection strings are stored in VS Code's SecretStorage, never in
  `settings.json`, unless you explicitly choose to save one there.
