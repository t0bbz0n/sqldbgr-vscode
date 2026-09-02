# sqldbgr – T-SQL debugger for VS Code

Breakpoint debugging for T-SQL scripts, stored procedures and functions in
SQL Server – straight from VS Code. No Visual Studio, no SSDT.

- **Set breakpoints in `.sql` files and press F5.** Step statement by statement,
  inspect variables (including table variables and temp tables as expandable
  trees), hover to see values, add watch expressions.
- **Debug a procedure or function body** directly from its `CREATE` script:
  a parameter form asks for values, `RETURN` and `OUTPUT` values are shown when
  it finishes.
- **Conditional breakpoints, hit counts and logpoints**, evaluated against the
  live variable values.
- **Change a variable while paused** – the new value is used when execution
  continues.
- **Dry run:** `"transaction": "rollback"` wraps the whole run in a transaction
  that is rolled back at the end, so stepping through data-modifying scripts is
  safe.
- `PRINT` output, result sets and SQL errors go to the Debug Console; an error
  stops on the failing line with the variables still inspectable.
  *sqldbgr: Open last result set* shows result sets in full.

## Requirements

- SQL Server 2016+ (any edition, including LocalDB and Docker). The debugger
  creates a small `__dbg` schema in the database you connect to (or in the
  database given by `debugDatabase`).
- Nothing else. The .NET runtime the debugger needs is downloaded on first use
  by the *.NET Install Tool* extension, which is installed automatically.

## Getting started

1. Open a `.sql` file, set a breakpoint, press **F5**.
2. Choose a connection – from the SQL Server (mssql) extension if you have it,
   or enter a connection string. You can save it securely for next time.
3. If the file defines a procedure or function you are asked whether to debug
   its body (with parameters) or run the script as is.

No `launch.json` is needed. For more control, add a `tsql` configuration:

```jsonc
{
  "type": "tsql",
  "request": "launch",
  "name": "Debug T-SQL (dry run)",
  "program": "${file}",
  "connectionString": "Server=(localdb)\\MSSQLLocalDB;Database=MyDb;Integrated Security=true;TrustServerCertificate=true",
  "transaction": "rollback",     // none | rollback | commit
  "stopOnEntry": false,
  "mode": "invoke",              // invoke | module
  "params": { "@customerId": "42", "@from": "2024-01-01" }
}
```

## How it works

The script is parsed with Microsoft's ScriptDom, and a pause point is injected
before every statement (recursively inside `IF`/`WHILE`/`TRY` blocks). The
instrumented script runs on one connection and blocks in `__dbg.Pause` when a
breakpoint is hit; a second connection watches the pause state and reports it
to VS Code. Nothing is deployed permanently – the instrumented text lives only
for the duration of the session.

Limitations: pausing *inside* an already deployed procedure is not possible
(debug its body from the script instead); scalar functions cannot pause at all
(UDFs allow no side effects); sqlcmd mode (`:r`, `:setvar`, `GO n`) is not
supported.

## Settings and commands

- `sqldbgr.connectionString` – fallback connection string (prefer *Save securely*).
- `sqldbgr.moduleFiles` – `ask` | `debug` | `run` for files that define a module.
- Commands: *sqldbgr: Debug current file*, *Debug procedure/function with
  parameters* (also as a CodeLens above `CREATE PROCEDURE/FUNCTION`), *Open last
  result set*, *Forget saved connection string*.

## License

MIT. Local debugging is free and needs no license. See the repository for the
roadmap and the boundary to future remote/attach features.
