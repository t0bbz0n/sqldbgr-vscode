using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlDebugger.Sidecar.Parsing;

public class InstrumentedScript
{
    public required string Sql { get; init; }
    /// <summary>Rad (1-baserad) i originalfilen -> statementId.</summary>
    public required Dictionary<int, int> LineMap { get; init; }
    /// <summary>stmtId -> rad, för paused-events tillbaka till klienten.</summary>
    public required Dictionary<int, int> StmtToLine { get; init; }
    /// <summary>stmtId -> variabelnamn som är i scope vid det statementet.</summary>
    public required Dictionary<int, IReadOnlyList<DeclaredVariable>> ScopeMap { get; init; }
    public required List<string> Errors { get; init; }
}

public record DeclaredVariable(string Name, string TypeName, bool IsTable);

public class ScriptDomAnalyzer
{
    public InstrumentedScript Instrument(string sql, string sourcePath)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out var parseErrors);

        var errors = parseErrors.Select(e => $"Rad {e.Line}: {e.Message}").ToList();
        if (errors.Count > 0)
            return Empty(errors);

        if (fragment is not TSqlScript script)
            return Empty(["Kunde inte tolka innehållet som ett T-SQL-script."]);

        var sb = new StringBuilder();
        var lineMap = new Dictionary<int, int>();
        var stmtToLine = new Dictionary<int, int>();
        var scopeMap = new Dictionary<int, IReadOnlyList<DeclaredVariable>>();
        var declaredSoFar = new List<DeclaredVariable>();
        int stmtId = 0;

        // Sätts av runnern per session; SESSION_CONTEXT bär sessionId genom batchen.
        sb.AppendLine("-- Instrumenterad av tsql-debugger. Deployas ALDRIG permanent.");

        foreach (var batch in script.Batches)
        {
            foreach (var stmt in batch.Statements)
            {
                TrackDeclarations(stmt, declaredSoFar);

                lineMap[stmt.StartLine] = stmtId;
                stmtToLine[stmtId] = stmt.StartLine;
                scopeMap[stmtId] = declaredSoFar.ToList();

                sb.AppendLine(GetSourceText(sql, stmt));
                sb.AppendLine(BuildLocalsCapture(declaredSoFar));
                sb.AppendLine($"EXEC __dbg.Pause @stmt_id = {stmtId};");
                stmtId++;
            }
        }

        return new InstrumentedScript
        {
            Sql = sb.ToString(),
            LineMap = lineMap,
            StmtToLine = stmtToLine,
            ScopeMap = scopeMap,
            Errors = []
        };
    }

    private static void TrackDeclarations(TSqlStatement stmt, List<DeclaredVariable> declared)
    {
        if (stmt is DeclareVariableStatement decl)
        {
            foreach (var d in decl.Declarations)
            {
                var isTable = d.DataType is null; // TABLE-variabler har separat AST-form
                var typeName = d.DataType is SqlDataTypeReference sqlType
                    ? sqlType.SqlDataTypeOption.ToString()
                    : d.DataType?.GetType().Name ?? "TABLE";
                declared.Add(new DeclaredVariable(d.VariableName.Value, typeName, isTable));
            }
        }
        else if (stmt is DeclareTableVariableStatement tableDecl)
        {
            declared.Add(new DeclaredVariable(
                tableDecl.Body.VariableName.Value, "TABLE", IsTable: true));
        }
    }

    private static string BuildLocalsCapture(IReadOnlyList<DeclaredVariable> vars)
    {
        if (vars.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("DELETE FROM __dbg.Locals WHERE SessionId = CONVERT(UNIQUEIDENTIFIER, SESSION_CONTEXT(N'__dbg_session'));");

        foreach (var v in vars)
        {
            if (v.IsTable)
            {
                sb.AppendLine($"""
                    INSERT INTO __dbg.Locals (SessionId, Name, TypeName, Value)
                    SELECT CONVERT(UNIQUEIDENTIFIER, SESSION_CONTEXT(N'__dbg_session')),
                           '{v.Name}', 'TABLE',
                           (SELECT * FROM {v.Name} FOR JSON AUTO, INCLUDE_NULL_VALUES);
                    """);
            }
            else
            {
                sb.AppendLine($"""
                    INSERT INTO __dbg.Locals (SessionId, Name, TypeName, Value)
                    VALUES (CONVERT(UNIQUEIDENTIFIER, SESSION_CONTEXT(N'__dbg_session')),
                            '{v.Name}', '{v.TypeName}',
                            TRY_CONVERT(NVARCHAR(MAX), {v.Name}));
                    """);
            }
        }
        return sb.ToString();
    }

    private static string GetSourceText(string sql, TSqlStatement stmt)
    {
        // ScriptDom ger token-offsets; plocka exakt originaltext för statementet.
        var start = stmt.StartOffset;
        var length = stmt.FragmentLength;
        return sql.Substring(start, length);
    }

    private static InstrumentedScript Empty(List<string> errors) => new()
    {
        Sql = string.Empty,
        LineMap = [],
        StmtToLine = [],
        ScopeMap = [],
        Errors = errors
    };
}
