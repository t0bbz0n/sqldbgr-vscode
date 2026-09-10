# sqldbgr

[![build](https://github.com/t0bbz0n/sqldbgr-vscode/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/t0bbz0n/sqldbgr-vscode/actions/workflows/build.yml)
[![license: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![VS Code](https://img.shields.io/badge/VS%20Code-%5E1.85-007ACC?logo=visualstudiocode&logoColor=white)](https://code.visualstudio.com/)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)

Breakpoint debugging for T-SQL in VS Code. Set breakpoints in `.sql` files,
step through statements, inspect variables. No Visual Studio, no SSDT.

**Local debugging is free and needs no licence.** See [NOTICE.md](NOTICE.md)
for where the paid boundary is: the attach mechanism lives in a separate,
licensed extension, and this repository only holds the extension point that
loads it.

## Architecture

```
VS Code (extension, TS)  ──DAP──►  debugAdapter.ts
                                        │ HTTP + SSE
                                        ▼
                              sidecar (C#, ASP.NET minimal API)
                                        │ 1. ScriptDom parse + instrumentation
                                        │ 2. runs the batch (connection A)
                                        │ 3. watches __dbg.Control (connection B)
                                        ▼
                                   SQL Server
                              __dbg.Pause blocks the batch
                              until the client signals
```

## Getting started (development)

Prerequisites: .NET 8 SDK, Node 20+, and a local SQL Server ((localdb) is
enough).

```bash
cd extension
npm install
npm run compile
# Open extension/ in VS Code, F5 -> Extension Development Host
```

In the development host: open a `.sql` file, set breakpoints, press F5. The
run stops on breakpoints, or on the first statement with `stopOnEntry: true`.
The highlighted statement is the one about to run, and Locals shows the state
before it runs. `PRINT`, result sets and SQL errors go to the Debug Console; a
SQL error stops on the failing line so Locals can be inspected. After the last
statement there is a virtual stop, when stepping, that shows the final state.

No launch.json is needed. With no `connectionString` the extension reads the
`sqldbgr.connectionString` setting, and if that is missing too it asks for one
at startup and offers to save it. For more control, create a launch
configuration of type `tsql`; a snippet is provided.

### The sidecar starts itself

No backend has to be started by hand, and no .NET has to be installed. On F5
the extension probes `sidecarUrl` (`/health`) and otherwise starts the sidecar
itself. The start command is chosen in this order:

1. `sidecarCommand` from the launch configuration (a development override, see
   below).
2. The **sidecar bundled in the VSIX** (`extension/sidecar-dist/`), run against
   an ASP.NET Core 8 runtime that the [.NET Install Tool
   extension](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.vscode-dotnet-runtime)
   downloads on first use. That extension is a declared dependency. If the
   runtime is already present it answers immediately. Without the Install Tool,
   for example in the development host, the system `dotnet` is tried.
3. `npx -y sqldbgr-sidecar`, the npm package in `sidecar-npm/`, as a fallback
   when no bundled sidecar is present. Run `npm run bundle-sidecar` in
   `extension/` to avoid it in the development host.

The process is owned by the extension and cleaned up when it deactivates. A
sidecar you started yourself is never touched. Its log goes to the **sqldbgr
Sidecar** output channel.

During development, against an unpublished package or local changes, point the
start command somewhere else in the launch configuration:

```jsonc
{
  "type": "tsql",
  // ...
  "sidecarCommand": ["dotnet", "run", "--project", "${workspaceFolder}/sidecar", "--"],
  // or turn it off entirely and run `dotnet run` yourself:
  "autoStartSidecar": false
}
```

To publish the sidecar package: `cd sidecar-npm && npm publish` (prepack runs
`dotnet publish` into `dist/`).

## Tests

```bash
cd tests/SqlDebugger.Sidecar.Tests
dotnet test                                    # unit tests (the analyzer)
SQLDBGR_TEST_CONNECTION="Server=localhost;User Id=sa;Password=...;TrustServerCertificate=true" \
  dotnet test                                  # + integration tests against SQL Server
```

The integration tests create the `sqldbgr_test` database and drive the whole
pause mechanism against a real server: breakpoints, stepping, loops, abort,
stopping on an exception, module mode and the HTTP surface the extension uses.
CI runs them against `mcr.microsoft.com/mssql/server` on every push, and fails
the build if any test skips itself, because a skipped integration test proves
nothing.

## Installing in VS Code

CI (`.github/workflows/build.yml`) builds the VSIX on every push. Download it
under **Actions → the run → Artifacts**. Pushing a `v*` tag creates a GitHub
release with the VSIX and the sidecar npm tarball attached.

Versioning: every CI build takes `major.minor` from `extension/package.json`
and the run number as the patch, for example `0.1.7`, stamped into the VSIX,
the npm package and the sidecar DLL. `GET /health` answers with that version,
so it is always clear which build is running. On a `v*` tag the tag's version
is used as is.

To build and install locally:

```bash
cd extension
npm install
npm run compile
npm run package                                     # bundles the sidecar + vsce package
code --install-extension sqldbgr-0.1.0.vsix
```

Or use the Extensions panel → `⋯` → *Install from VSIX…*

### Publishing to the Marketplace

```bash
az login --allow-no-subscriptions    # once, with the account that owns the publisher
cd extension
npm run release                      # 0.1.0 -> 0.1.1, builds, publishes, tags
```

Use `npm run release -- minor`, `-- major` or `-- 1.0.0` for other jumps, and
`--no-push` to inspect the commit first.

The script refuses to run on a dirty tree, checks the login *before* bumping
the version, and rolls the bump back if publishing fails. The Marketplace never
accepts the same version twice, so a bump left behind after a failure makes the
next attempt look like a duplicate of something that was never published.

No Personal Access Token is involved, and that is deliberate: Azure DevOps
retires global PATs on **1 December 2026**. `--azure-credential` uses Microsoft
Entra ID, the mechanism Microsoft is moving everything to, but with *you* as
the identity rather than a service principal. That is why no app registration,
federated credential or Azure DevOps user is needed.

#### Publishing from CI

Not available yet, and that is measured rather than assumed. `vsce publish
--oidc` (*trusted publishing*) exchanges GitHub's OIDC token directly for a
Marketplace credential: no PAT, no app registration, no Azure DevOps. The
client side exists and works. The server side does not:

```
POST https://marketplace.visualstudio.com/_apis/gallery/token
HTTP 404 - The controller for path '/_apis/gallery/token' was not found
```

To check whether that has changed, try the exchange yourself against the URL
above with a workflow-issued OIDC token; nothing is published and no version
number is spent, which matters because the Marketplace never accepts the same
version twice. Never print the token itself, only the status code.

The publish job in `build.yml` is already written, and switched off behind the
repository variable `TRUSTED_PUBLISHING`. Once the exchange answers 200, set it
to `true` under Settings → Secrets and variables → Actions → Variables and tags
will publish automatically. Until then a tag builds the VSIX and creates the
GitHub release as usual, without a red cross for something that cannot succeed
anyway.

Incidentally, `--oidc` is deliberately hidden from `vsce publish --help`
([PR #1297](https://github.com/microsoft/vscode-vsce/pull/1297)) and missing
entirely from `latest` (3.9.2), so the workflow pins `@vscode/vsce@3.9.3-12`.

The service principal route, an Entra app with a federated credential, was
abandoned on purpose. It requires the app to be added as a user in the Azure
DevOps organisation behind the publisher, and for a publisher owned by a
personal Microsoft account that does not appear to be possible. See
[vsce#1023](https://github.com/microsoft/vscode-vsce/issues/1023), reported and
closed without an answer.

#### Worth knowing

The version must always be higher than the last published one. The Marketplace
does not accept the same version twice, not even after you remove it. The first
publish takes a few minutes before the extension appears; after that updates go
through in under a minute.

## Features at a glance

- Breakpoints, including conditional ones with hit counts and logpoints;
  stepping; Pause; Restart without being asked again.
- Locals: scalars, table variables and temp tables as expandable trees; hover
  and Watch; changing a variable's value while paused (`setVariable`).
- Module mode: F5 on a `CREATE PROCEDURE/FUNCTION` debugs the body with a
  parameter panel; the return value and OUTPUT parameters are reported; a
  CodeLens sits above the definition.
- Transaction mode `transaction: rollback|commit`, a dry run.
- `PRINT`, result sets and SQL errors in the Debug Console; the *Open last
  result set* command shows results at full width; parse errors go to the
  Problems panel.
- Connections from the mssql extension's profiles or from an input box, stored
  safely in SecretStorage; `debugDatabase` for environments without DDL rights
  in the target database.
- The sidecar is bundled in the VSIX, one per window on a random port,
  token-authenticated; the .NET runtime is fetched automatically.
- English UI with a Swedish translation (`l10n/`).

## Status and roadmap

The detailed plan is in [ROADMAP.md](ROADMAP.md). Still to come: step-into for
stored procedures, the attach mechanism in its own repository, and extension
tests with `@vscode/test-electron`.

## Known limitations

- Attach mode, pausing inside a deployed module that other traffic is calling,
  lives in a separate licensed extension. See
  [docs/ATTACH-PROTOCOL.md](docs/ATTACH-PROTOCOL.md). Without it everything
  else works unchanged.
- Pausing inside a *deployed* module is not possible without attach mode. Use
  module mode, F5 on the file, to debug the body. Scalar functions can never be
  paused, because a UDF allows no side effects.
- Module mode runs the body as a batch: `RETURN` is rewritten and ends the
  batch. If the body calls itself recursively, the module must already exist.
- sqlcmd mode (`:r`, `:setvar`, `GO n`) is not supported. "(n rows affected)"
  is not shown, because the counters are polluted by the instrumentation's own
  INSERT/DELETE.

## The website

[t0bbz0n.github.io/sqldbgr-vscode](https://t0bbz0n.github.io/sqldbgr-vscode/)
is a single static page in [site/](site/), published by
`.github/workflows/pages.yml` on every push that touches it. It lives here
rather than in the Pro repository because GitHub Pages cannot serve a public
site from a private one, and because the page should be readable by people who
have not bought anything.

Turn it on once under Settings → Pages → Source → **GitHub Actions**.

`claim.html` is the page Stripe redirects to after a purchase. It reads the
checkout session from the URL, asks the licence service for the key and shows
it. Point `LICENSE_API` in it at the Function App, and allow this origin under
that app's CORS settings - or move the page onto the same host as the API and
set it to `/api` instead.

## Contributing

Bug reports and pull requests are welcome. See
[CONTRIBUTING.md](.github/CONTRIBUTING.md) for how to build, test and submit
changes, and [SECURITY.md](.github/SECURITY.md) for reporting a vulnerability
privately.

## Licence

MIT. See [LICENSE](LICENSE) and [NOTICE.md](NOTICE.md).
