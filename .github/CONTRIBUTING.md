# Contributing to sqldbgr

Thanks for taking the time. This is a small project, so the process is short.

## Before you start

For anything larger than a bug fix, open an issue first and say what you want
to change. It costs you five minutes and can save you an afternoon of work
that goes in a direction the project is not taking.

Check [ROADMAP.md](../ROADMAP.md) first. Some of what looks missing is
deliberate, and some of it is already planned.

## Getting set up

You need the .NET 8 SDK, Node 20 or newer, and a SQL Server to test against.
`(localdb)` is enough for the extension; the integration tests want a real
instance, and the easiest one is a container:

```bash
docker run -d --name mssql -e ACCEPT_EULA=Y -e 'MSSQL_SA_PASSWORD=Your!Password1' \
  -e MSSQL_PID=Developer -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
```

Then:

```bash
cd extension && npm install && npm run compile
```

Open `extension/` in VS Code and press F5 to get an Extension Development Host
with the extension loaded.

## Running the tests

```bash
# The sidecar: analyzer unit tests, plus integration and HTTP tests
SQLDBGR_TEST_CONNECTION="Server=localhost,1433;User Id=sa;Password=Your!Password1;TrustServerCertificate=true" \
  dotnet test tests/SqlDebugger.Sidecar.Tests

# The extension's own unit tests
cd extension && npm test
```

Without `SQLDBGR_TEST_CONNECTION` the integration tests skip themselves, which
is convenient locally and dangerous in CI, so CI fails the build if any test
skips. Run them against a real server before you open a pull request.

## What a change should look like

**Every behaviour change needs a test.** The pause mechanism runs inside SQL
Server, where a mistake compiles fine, goes green and only fails on someone
else's machine. If you cannot see how to test it, say so in the pull request
and we will work it out.

**Test against a real server, not a mock.** Every integration test here drives
an actual SQL Server. That is the whole reason they catch anything.

**Keep the diff to the change.** Reformatting, renaming and drive-by cleanups
in the same commit make the actual change impossible to review.

**Write the commit message for the reader.** Say what was wrong and why the
change fixes it, not which files you touched. The diff already says that.

Comments and documentation are in English.

## Pull requests

- Branch from `main`.
- Make sure CI is green. It builds the sidecar and the extension, runs the
  whole test suite against SQL Server, and packages the VSIX.
- Fill in the pull request template. The "how did you test this" part is the
  one that matters.

## Reporting bugs

Use the bug report template. The three things that decide whether a bug can be
fixed are the SQL that reproduces it, the SQL Server version, and what the
Debug Console and the **sqldbgr Sidecar** output channel said.

## Security

Do not open a public issue for a vulnerability. See [SECURITY.md](SECURITY.md).

## Licence

By contributing you agree that your contribution is MIT licensed, like the rest
of this repository. See [NOTICE.md](../NOTICE.md) for the boundary between this
repository and the licensed attach extension.
