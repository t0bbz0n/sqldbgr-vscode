# Changelog

## 0.1.x (unreleased)

Initial public version.

- Breakpoints, stepping, Locals (scalars, table variables, temp tables),
  hover and watch, change variables while paused.
- Conditional breakpoints, hit counts and logpoints.
- Module mode: debug the body of a `CREATE PROCEDURE`/`FUNCTION` script with a
  parameter form; return value and OUTPUT parameters reported.
- Transaction mode (`rollback` / `commit`) for safe dry runs.
- `PRINT`, result sets and SQL errors in the Debug Console; errors stop on the
  failing line. *Open last result set* for full result sets.
- Sidecar bundled in the VSIX; the .NET runtime is acquired automatically.
- Connection picker via the mssql extension, secure storage of connection strings.
- Swedish UI translation.
