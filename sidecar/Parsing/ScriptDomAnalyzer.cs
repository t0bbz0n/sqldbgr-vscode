using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlDebugger.Sidecar.Parsing;

public class InstrumentedScript
{
    /// <summary>Instrumenterade batchar (GO-separerade i källan), i körordning.
    /// Körs var för sig - CREATE FUNCTION/PROC m.fl. kräver egen batch.</summary>
    public required IReadOnlyList<string> Batches { get; init; }
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

public record ModuleParameter(string Name, string TypeName, string? DefaultValue);

/// <summary>En CREATE/ALTER FUNCTION/PROCEDURE hittad i ett script.</summary>
public record ModuleInfo(
    string Kind, string Name, IReadOnlyList<ModuleParameter> Parameters,
    bool CanScriptify, string? Reason);

public class ScriptDomAnalyzer
{
    /// <summary>Hittar första funktions-/procedurdefinitionen i scriptet, för
    /// extensionens "debugga kroppen?"-fråga och parameterinsamling.</summary>
    public ModuleInfo? InspectModule(string sql)
    {
        if (ParseScript(sql, out var script, out _) is not null || script is null)
            return null;

        foreach (var batch in script.Batches)
        foreach (var stmt in batch.Statements)
        {
            switch (stmt)
            {
                case FunctionStatementBody fn:
                    return new ModuleInfo("function", FullName(fn.Name),
                        MapParameters(sql, fn.Parameters),
                        CanScriptify: fn.StatementList is not null,
                        Reason: fn.StatementList is null
                            ? "Inline table-valued functions har ingen statementkropp att debugga."
                            : null);
                case ProcedureStatementBody proc:
                    return new ModuleInfo("procedure", FullName(proc.ProcedureReference.Name),
                        MapParameters(sql, proc.Parameters),
                        CanScriptify: proc.StatementList is not null, Reason: null);
            }
        }
        return null;
    }

    /// <summary>Instrumenterar kroppen på scriptets första funktion/procedur som
    /// ett fristående script: parametrarna binds som query-parametrar av runnern,
    /// och RETURN skrivs om till SET @__dbg_return + paus så returvärdet syns i
    /// Locals. Övriga batchar (GRANT m.m.) körs inte i detta läge.</summary>
    public InstrumentedScript InstrumentModuleBody(string sql, string sourcePath)
    {
        if (ParseScript(sql, out var script, out var errors) is { } parseFailure)
            return Empty(sourcePath, parseFailure);

        var module = script!.Batches
            .SelectMany(b => b.Statements)
            .FirstOrDefault(s => s is FunctionStatementBody or ProcedureStatementBody);
        var statementList = module switch
        {
            FunctionStatementBody fn => fn.StatementList,
            ProcedureStatementBody proc => proc.StatementList,
            _ => null
        };
        if (module is null || statementList is null)
            return Empty(sourcePath, ["Hittade ingen funktions-/procedurkropp att debugga."]);

        var ctx = new Context { Sql = sql, ReturnVariable = "@__dbg_return" };

        var (parameters, returnType) = module switch
        {
            FunctionStatementBody fn => (fn.Parameters,
                fn.ReturnType is ScalarFunctionReturnType scalar
                    ? GetText(sql, scalar.DataType) : "SQL_VARIANT"),
            ProcedureStatementBody proc => (proc.Parameters, "INT"),
            _ => throw new InvalidOperationException()
        };
        foreach (var p in parameters)
            ctx.Declared.Add(new DeclaredVariable(
                p.VariableName.Value, GetText(sql, p.DataType), IsTable: false));
        ctx.Declared.Add(new DeclaredVariable(ctx.ReturnVariable, returnType, IsTable: false));

        var injections = new List<Injection>();
        foreach (var stmt in statementList.Statements)
            InstrumentStatement(stmt, injections, ctx);

        var body = new StringBuilder();
        body.AppendLine("-- Instrumenterad av tsql-debugger. Deployas ALDRIG permanent.");
        body.AppendLine($"DECLARE {ctx.ReturnVariable} {returnType};");
        body.AppendLine(Splice(sql, statementList.StartOffset, EndOffset(statementList), injections));

        return new InstrumentedScript
        {
            Batches = [body.ToString()],
            SourcePath = sourcePath,
            LineMap = ctx.LineMap,
            StmtToSpan = ctx.StmtToSpan,
            ScopeMap = ctx.ScopeMap,
            Errors = []
        };
    }

    public InstrumentedScript Instrument(string sql, string sourcePath)
    {
        if (ParseScript(sql, out var script, out _) is { } parseFailure)
            return Empty(sourcePath, parseFailure);

        var ctx = new Context { Sql = sql };
        var batches = new List<string>();

        foreach (var batch in script!.Batches)
        {
            // Variabler lever inte över batchgränser - scopet börjar om per batch.
            ctx.Declared.Clear();

            // Injektioner sprängs in i batchens originaltext (istället för att
            // statements skrivs ut platt) så att BEGIN/END-, IF/ELSE- och
            // WHILE-strukturer bevaras och pauser hamnar inuti blocken.
            var injections = new List<Injection>();
            foreach (var stmt in batch.Statements)
                InstrumentStatement(stmt, injections, ctx);

            var text = Splice(sql, batch.StartOffset, EndOffset(batch), injections);
            if (string.IsNullOrWhiteSpace(text)) continue;

            // SESSION_CONTEXT (satt av runnern) bär sessionId genom alla batchar.
            batches.Add("-- Instrumenterad av tsql-debugger. Deployas ALDRIG permanent.\n" + text);
        }

        return new InstrumentedScript
        {
            Batches = batches,
            SourcePath = sourcePath,
            LineMap = ctx.LineMap,
            StmtToSpan = ctx.StmtToSpan,
            ScopeMap = ctx.ScopeMap,
            Errors = []
        };
    }

    /// <returns>Fellista vid parse-fel, annars null (och script satt).</returns>
    private static List<string>? ParseScript(string sql, out TSqlScript? script, out IList<ParseError> parseErrors)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out parseErrors);

        script = fragment as TSqlScript;
        if (parseErrors.Count > 0)
            return parseErrors.Select(e => $"Rad {e.Line}: {e.Message}").ToList();
        if (script is null)
            return ["Kunde inte tolka innehållet som ett T-SQL-script."];
        return null;
    }

    private static string FullName(SchemaObjectName name)
        => string.Join(".", name.Identifiers.Select(i => i.Value));

    private static IReadOnlyList<ModuleParameter> MapParameters(
        string sql, IList<ProcedureParameter> parameters)
        => parameters.Select(p => new ModuleParameter(
            p.VariableName.Value,
            p.DataType is null ? "?" : GetText(sql, p.DataType),
            p.Value is null ? null : GetText(sql, p.Value))).ToList();

    private static string GetText(string sql, TSqlFragment fragment)
        => sql.Substring(fragment.StartOffset, fragment.FragmentLength);

    private void InstrumentStatement(TSqlStatement stmt, List<Injection> injections, Context ctx)
    {
        switch (stmt)
        {
            case ReturnStatement ret:
                InstrumentReturn(ret, injections, ctx);
                break;

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
        injections.Add(new Injection(branch.StartOffset, 0, ctx.NextSeq(), "BEGIN\n"));
        InstrumentStatement(branch, injections, ctx);
        injections.Add(new Injection(EndOffset(branch), 0, ctx.NextSeq(), "\nEND\n"));
    }

    /// <summary>RETURN pausas FÖRE (paus efter skulle aldrig köras). I modulläge
    /// ersätts hela satsen: RETURN expr -> SET @__dbg_return = expr + paus +
    /// RETURN, så returvärdet syns i Locals och batchen förblir giltig
    /// (RETURN med värde är bara tillåtet inuti moduler).</summary>
    private void InstrumentReturn(ReturnStatement ret, List<Injection> injections, Context ctx)
    {
        var id = ctx.NextStmtId();
        ctx.LineMap.TryAdd(ret.StartLine, id);
        ctx.StmtToSpan[id] = ComputeSpan(ret);
        ctx.ScopeMap[id] = ctx.Declared.ToList();

        var text = new StringBuilder();
        text.AppendLine();
        if (ret.Expression is not null && ctx.ReturnVariable is not null)
            text.AppendLine($"SET {ctx.ReturnVariable} = ({GetText(ctx.Sql, ret.Expression)});");
        text.Append(BuildLocalsCapture(ctx.Declared));
        text.AppendLine($"EXEC __dbg.Pause @stmt_id = {id};");
        text.AppendLine("RETURN;");
        injections.Add(new Injection(ret.StartOffset, ret.FragmentLength, ctx.NextSeq(), text.ToString()));
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
        injections.Add(new Injection(EndOffset(stmt), 0, ctx.NextSeq(), text.ToString()));
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

    private static string Splice(string sql, int start, int end, List<Injection> injections)
    {
        var sb = new StringBuilder();
        var pos = start;
        foreach (var inj in injections.OrderBy(i => i.Offset).ThenBy(i => i.Seq))
        {
            sb.Append(sql, pos, inj.Offset - pos);
            sb.Append(inj.Text);
            pos = inj.Offset + inj.Length; // Length > 0 = ersättning, originalet hoppas över
        }
        sb.Append(sql, pos, end - pos);
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
        Batches = [],
        SourcePath = sourcePath,
        LineMap = [],
        StmtToSpan = [],
        ScopeMap = [],
        Errors = errors
    };

    private sealed record Injection(int Offset, int Length, int Seq, string Text);

    private sealed class Context
    {
        public required string Sql { get; init; }
        /// <summary>Satt i modulläge: variabeln som RETURN-uttryck fångas i.</summary>
        public string? ReturnVariable { get; init; }
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
