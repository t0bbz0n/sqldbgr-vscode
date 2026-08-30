using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlDebugger.Sidecar.Parsing;

public class InstrumentedScript
{
    public required string Sql { get; init; }
    /// <summary>Originalfilen som instrumenterades; följer med i paused-events.</summary>
    public required string SourcePath { get; init; }
    /// <summary>Rad (1-baserad) i originalfilen -> statementId.</summary>
    public required Dictionary<int, int> LineMap { get; init; }
    /// <summary>stmtId -> position i originalfilen, för paused-events tillbaka till klienten.</summary>
    public required Dictionary<int, StatementSpan> StmtToSpan { get; init; }
    /// <summary>stmtId -> variabelnamn som är i scope vid det statementet.</summary>
    public required Dictionary<int, IReadOnlyList<DeclaredVariable>> ScopeMap { get; init; }
    public required List<string> Errors { get; init; }
}

/// <summary>1-baserade rad/kolumn-positioner för ett statement i originalfilen.</summary>
public record StatementSpan(int Line, int Column, int EndLine, int EndColumn);

public record DeclaredVariable(string Name, string TypeName, bool IsTable);

public class ScriptDomAnalyzer
{
    public InstrumentedScript Instrument(string sql, string sourcePath)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out var parseErrors);

        var errors = parseErrors.Select(e => $"Rad {e.Line}: {e.Message}").ToList();
        if (errors.Count > 0)
            return Empty(sourcePath, errors);

        if (fragment is not TSqlScript script)
            return Empty(sourcePath, ["Kunde inte tolka innehållet som ett T-SQL-script."]);

        var ctx = new Context();
        var sb = new StringBuilder();

        // Sätts av runnern per session; SESSION_CONTEXT bär sessionId genom batchen.
        sb.AppendLine("-- Instrumenterad av tsql-debugger. Deployas ALDRIG permanent.");

        foreach (var batch in script.Batches)
        {
            // Injektioner sprängs in i batchens originaltext (istället för att
            // statements skrivs ut platt) så att BEGIN/END-, IF/ELSE- och
            // WHILE-strukturer bevaras och pauser hamnar inuti blocken.
            var injections = new List<Injection>();
            foreach (var stmt in batch.Statements)
                InstrumentStatement(stmt, injections, ctx);
            sb.AppendLine(Splice(sql, batch, injections));
        }

        return new InstrumentedScript
        {
            Sql = sb.ToString(),
            SourcePath = sourcePath,
            LineMap = ctx.LineMap,
            StmtToSpan = ctx.StmtToSpan,
            ScopeMap = ctx.ScopeMap,
            Errors = []
        };
    }

    private void InstrumentStatement(TSqlStatement stmt, List<Injection> injections, Context ctx)
    {
        switch (stmt)
        {
            case BeginEndBlockStatement block:
                foreach (var inner in block.StatementList.Statements)
                    InstrumentStatement(inner, injections, ctx);
                break;

            case IfStatement ifStmt:
                InstrumentBranch(ifStmt.ThenStatement, injections, ctx);
                if (ifStmt.ElseStatement is not null)
                    InstrumentBranch(ifStmt.ElseStatement, injections, ctx);
                break;

            case WhileStatement whileStmt:
                InstrumentBranch(whileStmt.Statement, injections, ctx);
                break;

            case TryCatchStatement tryCatch:
                foreach (var inner in tryCatch.TryStatements.Statements)
                    InstrumentStatement(inner, injections, ctx);
                foreach (var inner in tryCatch.CatchStatements.Statements)
                    InstrumentStatement(inner, injections, ctx);
                break;

            default:
                InstrumentLeaf(stmt, injections, ctx);
                break;
        }
    }

    private void InstrumentBranch(TSqlStatement branch, List<Injection> injections, Context ctx)
    {
        if (branch is BeginEndBlockStatement or IfStatement or WhileStatement or TryCatchStatement)
        {
            InstrumentStatement(branch, injections, ctx);
            return;
        }

        // En gren utan BEGIN/END (IF @x = 1 SELECT 1 ELSE ...) får ett syntetiskt
        // block runt sig, annars hamnar capture/pause utanför grenen och körs
        // ovillkorligt - och ELSE skulle inte längre parsa.
        injections.Add(new Injection(branch.StartOffset, ctx.NextSeq(), "BEGIN\n"));
        InstrumentLeaf(branch, injections, ctx);
        injections.Add(new Injection(EndOffset(branch), ctx.NextSeq(), "\nEND\n"));
    }

    private void InstrumentLeaf(TSqlStatement stmt, List<Injection> injections, Context ctx)
    {
        TrackDeclarations(stmt, ctx.Declared);

        // Modul-definitioner (CREATE PROC/VIEW/TRIGGER...) måste vara ensamma i sin
        // batch - text efter dem hamnar annars i modulkroppen. Instrumenteras inte.
        if (stmt is ProcedureStatementBodyBase or ViewStatementBody or TriggerStatementBody)
            return;

        var id = ctx.NextStmtId();
        ctx.LineMap.TryAdd(stmt.StartLine, id);
        ctx.StmtToSpan[id] = ComputeSpan(stmt);
        ctx.ScopeMap[id] = ctx.Declared.ToList();

        var text = new StringBuilder();
        text.AppendLine();
        text.Append(BuildLocalsCapture(ctx.Declared));
        text.AppendLine($"EXEC __dbg.Pause @stmt_id = {id};");
        injections.Add(new Injection(EndOffset(stmt), ctx.NextSeq(), text.ToString()));
    }

    private static int EndOffset(TSqlFragment fragment)
        => fragment.StartOffset + fragment.FragmentLength;

    private static StatementSpan ComputeSpan(TSqlStatement stmt)
    {
        var lastToken = stmt.ScriptTokenStream[stmt.LastTokenIndex];
        var text = lastToken.Text ?? string.Empty;
        var endLine = lastToken.Line;
        var endColumn = lastToken.Column + text.Length;

        // Sista tokenet kan spänna över flera rader (t.ex. blockkommentar).
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline >= 0)
        {
            endLine += text.Count(c => c == '\n');
            endColumn = text.Length - lastNewline;
        }

        return new StatementSpan(stmt.StartLine, stmt.StartColumn, endLine, endColumn);
    }

    private static string Splice(string sql, TSqlBatch batch, List<Injection> injections)
    {
        var sb = new StringBuilder();
        var pos = batch.StartOffset;
        foreach (var inj in injections.OrderBy(i => i.Offset).ThenBy(i => i.Seq))
        {
            sb.Append(sql, pos, inj.Offset - pos);
            sb.Append(inj.Text);
            pos = inj.Offset;
        }
        sb.Append(sql, pos, EndOffset(batch) - pos);
        return sb.ToString();
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

    private static InstrumentedScript Empty(string sourcePath, List<string> errors) => new()
    {
        Sql = string.Empty,
        SourcePath = sourcePath,
        LineMap = [],
        StmtToSpan = [],
        ScopeMap = [],
        Errors = errors
    };

    private sealed record Injection(int Offset, int Seq, string Text);

    private sealed class Context
    {
        public Dictionary<int, int> LineMap { get; } = [];
        public Dictionary<int, StatementSpan> StmtToSpan { get; } = [];
        public Dictionary<int, IReadOnlyList<DeclaredVariable>> ScopeMap { get; } = [];
        public List<DeclaredVariable> Declared { get; } = [];

        private int _stmtId;
        private int _seq;
        public int NextStmtId() => _stmtId++;
        public int NextSeq() => _seq++;
    }
}
