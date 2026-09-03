using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlDebugger.Sidecar.Parsing;

public class InstrumentedScript
{
    /// <summary>Instrumenterade batchar (GO-separerade i källan), i körordning.
    /// Körs var för sig - CREATE FUNCTION/PROC m.fl. kräver egen batch.</summary>
    public required IReadOnlyList<InstrumentedBatch> Batches { get; init; }
    /// <summary>Originalfilen som instrumenterades; följer med i paused-events.</summary>
    public required string SourcePath { get; init; }
    /// <summary>Rad (1-baserad) i originalfilen -> statementId.</summary>
    public required Dictionary<int, int> LineMap { get; init; }
    /// <summary>stmtId -> position i originalfilen, för paused-events tillbaka till klienten.</summary>
    public required Dictionary<int, StatementSpan> StmtToSpan { get; init; }
    /// <summary>stmtId -> variabler som är i scope (deklarerade före) vid det statementet.</summary>
    public required Dictionary<int, IReadOnlyList<DeclaredVariable>> ScopeMap { get; init; }
    /// <summary>Pauser som visar slutläget: virtuellt "slut på batch" samt RETURN
    /// i modulläge. Runnern tvingar stopp på dessa i modulläge.</summary>
    public required IReadOnlyList<int> FinalStmtIds { get; init; }
    /// <summary>Variabler att rapportera vid avslut i modulläge (returvärde, OUTPUT).</summary>
    public required IReadOnlyList<string> ResultVariables { get; init; }
    public required List<string> Errors { get; init; }
}

/// <summary>En körbar batch plus radkarta tillbaka till originalfilen (för SQL-fel).</summary>
public record InstrumentedBatch(string Sql, IReadOnlyList<LineSegment> LineSegments)
{
    /// <summary>Rad i den instrumenterade batchen (1-baserad) -> originalrad, 0 om okänd.</summary>
    public int MapLine(int instrumentedLine)
    {
        LineSegment? hit = null;
        foreach (var seg in LineSegments)
        {
            if (seg.OutStart > instrumentedLine) break;
            hit = seg;
        }
        if (hit is null) return 0;
        return hit.Injected ? hit.OrigLine : hit.OrigLine + (instrumentedLine - hit.OutStart);
    }
}

/// <summary>Ett textsegment i den instrumenterade batchen: börjar på rad OutStart
/// och motsvarar originalrad OrigLine (injicerad text pekar på raden den sattes in vid).</summary>
public record LineSegment(int OutStart, int OrigLine, bool Injected);

/// <summary>1-baserade rad/kolumn-positioner för ett statement i originalfilen.</summary>
public record StatementSpan(int Line, int Column, int EndLine, int EndColumn);

public record DeclaredVariable(string Name, string TypeName, bool IsTable, bool IsTempTable = false);

/// <summary>Parse-fel med position, för Problems-panelen i klienten.</summary>
public record ParseIssue(int Line, int Column, string Message);

public record ModuleParameter(string Name, string TypeName, string? DefaultValue, bool IsOutput);

/// <summary>En CREATE/ALTER FUNCTION/PROCEDURE hittad i ett script.</summary>
public record ModuleInfo(
    string Kind, string Name, IReadOnlyList<ModuleParameter> Parameters,
    bool CanScriptify, string? Reason);

public class ScriptDomAnalyzer
{
    private const string Header = "-- Instrumented by sqldbgr. NEVER deploy permanently.";
    private const string SidDeclaration =
        "DECLARE @__dbg_sid UNIQUEIDENTIFIER = CONVERT(UNIQUEIDENTIFIER, SESSION_CONTEXT(N'__dbg_session'));";

    /// <summary>Namnet runnern binder en modulparameter under; preludet deklarerar
    /// om den med rätt typ: DECLARE @a INT = @__p_a.</summary>
    public static string BoundParameterName(string parameterName)
        => "@__p_" + parameterName.TrimStart('@');

    public IReadOnlyList<ParseIssue> GetParseErrors(string sql)
    {
        new TSql160Parser(initialQuotedIdentifiers: true).Parse(new StringReader(sql), out var errors);
        return errors.Select(e => new ParseIssue(e.Line, e.Column, e.Message)).ToList();
    }

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
                            ? "Inline table-valued functions have no statement body to debug."
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
    /// <param name="debugSchema">T.ex. "[MyDb].__dbg" - kvalificerat så USE i scriptet inte bryter pauserna.</param>
    public InstrumentedScript InstrumentModuleBody(string sql, string sourcePath, string debugSchema = "__dbg")
    {
        if (ParseScript(sql, out var script, out _) is { } parseFailure)
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
            return Empty(sourcePath, ["No function or procedure body found to debug."]);

        var prelude = new StringBuilder();
        prelude.AppendLine(Header);
        prelude.AppendLine(SidDeclaration);
        prelude.AppendLine("SET DATEFORMAT ymd;"); // ISO-datum i parametrar är entydiga oavsett språk

        // Parametrarna binds av runnern som @__p_<namn> (NVARCHAR) och deklareras
        // här om med signaturens typ - SQL Server gör konverteringen, så INT
        // förblir INT (ingen strängkonkatenering) och fel syns som SQL-fel.
        IList<ProcedureParameter> parameters = module switch
        {
            FunctionStatementBody fn => fn.Parameters,
            ProcedureStatementBody proc => proc.Parameters,
            _ => throw new InvalidOperationException()
        };
        var declared = new List<DeclaredVariable>();
        foreach (var p in parameters)
        {
            var typeText = GetText(sql, p.DataType);
            prelude.AppendLine($"DECLARE {p.VariableName.Value} {typeText} = {BoundParameterName(p.VariableName.Value)};");
            declared.Add(new DeclaredVariable(p.VariableName.Value, typeText, IsTable: false));
        }

        // Returvärde: skalär funktion/proc -> @__dbg_return; multi-statement TVF ->
        // dess RETURNS @t TABLE (...) deklareras och rapporteras som resultat.
        string? returnVariable = "@__dbg_return";
        var resultVariables = new List<string>();
        switch (module)
        {
            case FunctionStatementBody { ReturnType: ScalarFunctionReturnType scalar }:
                var returnType = GetText(sql, scalar.DataType);
                prelude.AppendLine($"DECLARE @__dbg_return {returnType};");
                declared.Add(new DeclaredVariable("@__dbg_return", returnType, IsTable: false));
                break;
            case FunctionStatementBody { ReturnType: TableValuedFunctionReturnType tvf }:
                returnVariable = null;
                prelude.AppendLine($"DECLARE {GetText(sql, tvf.DeclareTableVariableBody)};");
                declared.Add(new DeclaredVariable(tvf.DeclareTableVariableBody.VariableName.Value, "TABLE", IsTable: true));
                resultVariables.Add(tvf.DeclareTableVariableBody.VariableName.Value);
                break;
            case FunctionStatementBody:
                return Empty(sourcePath, ["The function's return type is not supported in module mode."]);
            default: // procedure
                prelude.AppendLine("DECLARE @__dbg_return INT;");
                declared.Add(new DeclaredVariable("@__dbg_return", "INT", IsTable: false));
                break;
        }
        if (returnVariable is not null) resultVariables.Insert(0, returnVariable);
        resultVariables.AddRange(parameters
            .Where(p => p.Modifier == ParameterModifier.Output)
            .Select(p => p.VariableName.Value));

        var ctx = new Context(sql, debugSchema) { IsModule = true, ReturnVariable = returnVariable };
        ctx.Declared.AddRange(declared);

        var injections = new List<Injection>();
        foreach (var stmt in statementList.Statements)
            InstrumentStatement(stmt, injections, ctx);
        AddEndOfBatchPause(statementList, injections, ctx);

        var batch = Splice(ctx, statementList.StartOffset, EndOffset(statementList), injections, prelude.ToString());

        return new InstrumentedScript
        {
            Batches = [batch],
            SourcePath = sourcePath,
            LineMap = ctx.LineMap,
            StmtToSpan = ctx.StmtToSpan,
            ScopeMap = ctx.ScopeMap,
            FinalStmtIds = ctx.FinalStmtIds,
            ResultVariables = resultVariables,
            Errors = []
        };
    }

    /// <summary>Instrumenterar en modul PÅ PLATS: hela CREATE/ALTER-texten behålls
    /// och pauser sprutas bara in i kroppen, så resultatet kan deployas med ALTER
    /// (som bevarar rättigheter, till skillnad från drop/create). Används av
    /// attach-läget, där modulen körs av någon annans session.</summary>
    public InstrumentedScript InstrumentModuleInPlace(string sql, string sourcePath, string debugSchema = "__dbg")
    {
        if (ParseScript(sql, out var script, out _) is { } parseFailure)
            return Empty(sourcePath, parseFailure);

        var module = script!.Batches.SelectMany(b => b.Statements)
            .FirstOrDefault(s => s is ProcedureStatementBody or TriggerStatementBody);
        var statementList = module switch
        {
            ProcedureStatementBody p => p.StatementList,
            TriggerStatementBody t => t.StatementList,
            _ => null
        };
        if (module is null || statementList is null)
            return Empty(sourcePath, ["No procedure or trigger body found to instrument."]);

        var ctx = new Context(sql, debugSchema)
        {
            IsModule = true,
            // Inget prelude kan deklarera en variabel här, och SESSION_CONTEXT
            // sätts mitt i körningen när sessionen fångas - läs den varje gång.
            Sid = "CONVERT(UNIQUEIDENTIFIER, SESSION_CONTEXT(N'__dbg_session'))"
        };

        // Modulens egna parametrar är i scope från början och deklareras inte om.
        if (module is ProcedureStatementBody proc)
            foreach (var p in proc.Parameters)
                ctx.Declared.Add(new DeclaredVariable(
                    p.VariableName.Value, GetText(sql, p.DataType), IsTable: false));

        var injections = new List<Injection>();
        foreach (var stmt in statementList.Statements)
            InstrumentStatement(stmt, injections, ctx);

        // CREATE [OR ALTER] -> ALTER, så deployen behåller modulens rättigheter.
        var tokens = module.ScriptTokenStream;
        var keyword = module.FirstTokenIndex;
        while (keyword <= module.LastTokenIndex
               && tokens[keyword].TokenType is not (TSqlTokenType.Procedure or TSqlTokenType.Proc or TSqlTokenType.Trigger))
            keyword++;
        if (keyword <= module.LastTokenIndex)
        {
            var start = tokens[module.FirstTokenIndex].Offset;
            injections.Add(new Injection(start, tokens[keyword].Offset - start, ctx.NextSeq(), "ALTER "));
        }

        var batch = Splice(ctx, module.StartOffset, EndOffset(module), injections, Header + "\n");

        return new InstrumentedScript
        {
            Batches = [batch],
            SourcePath = sourcePath,
            LineMap = ctx.LineMap,
            StmtToSpan = ctx.StmtToSpan,
            ScopeMap = ctx.ScopeMap,
            FinalStmtIds = ctx.FinalStmtIds,
            ResultVariables = [],
            Errors = []
        };
    }

    public InstrumentedScript Instrument(string sql, string sourcePath, string debugSchema = "__dbg")
    {
        if (ParseScript(sql, out var script, out _) is { } parseFailure)
            return Empty(sourcePath, parseFailure);

        var ctx = new Context(sql, debugSchema);
        var batches = new List<InstrumentedBatch>();

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
            if (injections.Count > 0)
                AddEndOfBatchPause(batch, injections, ctx);

            if (string.IsNullOrWhiteSpace(GetText(sql, batch))) continue;

            // SESSION_CONTEXT (satt av runnern) bär sessionId genom alla batchar; den
            // läses in i en variabel per batch. Inte i batchar utan instrumentering
            // (CREATE PROC måste vara första statementet i sin batch).
            var prefix = injections.Count > 0 ? $"{Header}\n{SidDeclaration}\n" : $"{Header}\n";
            batches.Add(Splice(ctx, batch.StartOffset, EndOffset(batch), injections, prefix));
        }

        return new InstrumentedScript
        {
            Batches = batches,
            SourcePath = sourcePath,
            LineMap = ctx.LineMap,
            StmtToSpan = ctx.StmtToSpan,
            ScopeMap = ctx.ScopeMap,
            FinalStmtIds = ctx.FinalStmtIds,
            ResultVariables = [],
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
            return parseErrors.Select(e => $"Line {e.Line}: {e.Message}").ToList();
        if (script is null)
            return ["The file could not be parsed as a T-SQL script."];
        return null;
    }

    private static string FullName(SchemaObjectName name)
        => string.Join(".", name.Identifiers.Select(i => i.Value));

    private static IReadOnlyList<ModuleParameter> MapParameters(
        string sql, IList<ProcedureParameter> parameters)
        => parameters.Select(p => new ModuleParameter(
            p.VariableName.Value,
            p.DataType is null ? "?" : GetText(sql, p.DataType),
            p.Value is null ? null : GetText(sql, p.Value),
            p.Modifier == ParameterModifier.Output)).ToList();

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

    /// <summary>Paus FÖRE statementet: det highlightade statementet är det som
    /// körs härnäst och Locals visar läget innan det körs (som andra debuggers).
    /// Deklarationer registreras efter, så variabeln syns från nästa paus.</summary>
    private void InstrumentLeaf(TSqlStatement stmt, List<Injection> injections, Context ctx)
    {
        // Modul-definitioner (CREATE PROC/VIEW/TRIGGER...) måste vara ensamma i sin
        // batch - text runt dem hamnar annars i modulkroppen. Instrumenteras inte.
        if (stmt is ProcedureStatementBodyBase or ViewStatementBody or TriggerStatementBody)
            return;

        var id = ctx.RegisterStatement(stmt.StartLine, ComputeSpan(stmt));
        injections.Add(new Injection(stmt.StartOffset, 0, ctx.NextSeq(), PauseText(ctx, id)));
        TrackDeclarations(stmt, ctx);
    }

    /// <summary>RETURN pausas före som andra statements. I modulläge ersätts hela
    /// satsen: RETURN expr -> SET @__dbg_return = expr + paus + RETURN, så
    /// returvärdet syns i Locals och batchen förblir giltig (RETURN med värde
    /// är bara tillåtet inuti moduler). Pausen räknas som slutläge.</summary>
    private void InstrumentReturn(ReturnStatement ret, List<Injection> injections, Context ctx)
    {
        var id = ctx.RegisterStatement(ret.StartLine, ComputeSpan(ret));
        if (ctx.IsModule) ctx.FinalStmtIds.Add(id);

        var text = new StringBuilder();
        text.AppendLine();
        if (ret.Expression is not null && ctx.ReturnVariable is not null)
            text.AppendLine($"SET {ctx.ReturnVariable} = ({GetText(ctx.Sql, ret.Expression)});");
        text.Append(PauseText(ctx, id));
        text.AppendLine("RETURN;");
        injections.Add(new Injection(ret.StartOffset, ret.FragmentLength, ctx.NextSeq(), text.ToString()));
    }

    /// <summary>Virtuellt stopp efter sista statementet så slutläget går att
    /// inspektera (annars försvinner Locals med sessionen). Träffas bara vid
    /// stegning - eller alltid i modulläge, där runnern tvingar stoppet.</summary>
    private void AddEndOfBatchPause(TSqlFragment scope, List<Injection> injections, Context ctx)
    {
        var lastToken = scope.ScriptTokenStream[scope.LastTokenIndex];
        var endLine = lastToken.Line;
        var endColumn = lastToken.Column + (lastToken.Text?.Length ?? 0);
        var id = ctx.RegisterStatement(line: null, new StatementSpan(endLine, 1, endLine, endColumn));
        ctx.FinalStmtIds.Add(id);
        injections.Add(new Injection(EndOffset(scope), 0, ctx.NextSeq(), PauseText(ctx, id)));
    }

    private static string PauseText(Context ctx, int stmtId)
    {
        var text = new StringBuilder();
        text.AppendLine();
        text.Append(BuildScalarCapture(ctx));
        // Den dyra delen (tabellvariabler som JSON + proc-anropet) bara när det
        // faktiskt blir en paus - annars kostar varje statement i en loop.
        text.AppendLine($"IF {ctx.Dbg}.ShouldPause({ctx.Sid}, {stmtId}) = 1");
        text.AppendLine("BEGIN");
        text.Append(BuildTableCapture(ctx));
        text.AppendLine($"    EXEC {ctx.Dbg}.Pause @stmt_id = {stmtId};");
        text.Append(BuildOverridesApply(ctx));
        text.AppendLine("END");
        return text.ToString();
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

    /// <summary>Spränger in injektionerna i originaltexten [start, end) och bygger
    /// samtidigt radkartan tillbaka till originalfilen.</summary>
    private static InstrumentedBatch Splice(
        Context ctx, int start, int end, List<Injection> injections, string prefix)
    {
        var sb = new StringBuilder(prefix);
        // Prefixet (header, DECLARE @__dbg_return) mappas till batchens första originalrad.
        var segments = new List<LineSegment> { new(1, ctx.LineAt(start), Injected: true) };
        var outLine = 1 + prefix.Count(c => c == '\n');
        var pos = start;

        void AppendOriginal(int from, int to)
        {
            if (to <= from) return;
            segments.Add(new LineSegment(outLine, ctx.LineAt(from), Injected: false));
            sb.Append(ctx.Sql, from, to - from);
            outLine += CountNewlines(ctx.Sql, from, to);
        }

        foreach (var inj in injections.OrderBy(i => i.Offset).ThenBy(i => i.Seq))
        {
            AppendOriginal(pos, inj.Offset);
            segments.Add(new LineSegment(outLine, ctx.LineAt(inj.Offset), Injected: true));
            sb.Append(inj.Text);
            outLine += CountNewlines(inj.Text, 0, inj.Text.Length);
            pos = Math.Max(pos, inj.Offset + inj.Length); // Length > 0 = ersättning
        }
        AppendOriginal(pos, end);

        return new InstrumentedBatch(sb.ToString(), segments);
    }

    private static int CountNewlines(string s, int from, int to)
    {
        var n = 0;
        for (var i = from; i < to; i++) if (s[i] == '\n') n++;
        return n;
    }

    private static void TrackDeclarations(TSqlStatement stmt, Context ctx)
    {
        if (stmt is DeclareVariableStatement decl)
        {
            foreach (var d in decl.Declarations)
            {
                var typeName = d.DataType is null ? "TABLE" : GetText(ctx.Sql, d.DataType);
                ctx.Declared.Add(new DeclaredVariable(d.VariableName.Value, typeName, IsTable: d.DataType is null));
            }
        }
        else if (stmt is DeclareTableVariableStatement tableDecl)
        {
            ctx.Declared.Add(new DeclaredVariable(
                tableDecl.Body.VariableName.Value, "TABLE", IsTable: true));
        }
        // Temp-tabeller fångas som tabellvariabler (guardade med OBJECT_ID vid
        // capture, eftersom de till skillnad från variabler kanske inte finns).
        else if (stmt is CreateTableStatement { SchemaObjectName.BaseIdentifier.Value: var tmp } && tmp.StartsWith('#'))
        {
            AddTempTable(ctx, tmp);
        }
        else if (stmt is SelectStatement { Into.BaseIdentifier.Value: var into } && into.StartsWith('#'))
        {
            AddTempTable(ctx, into);
        }
    }

    private static void AddTempTable(Context ctx, string name)
    {
        if (ctx.Declared.Any(v => v.Name == name)) return;
        ctx.Declared.Add(new DeclaredVariable(name, "TABLE", IsTable: true, IsTempTable: true));
    }

    private const int TableCaptureRows = 100;

    /// <summary>Skalära variabler: en DELETE + en INSERT ... VALUES per statement.</summary>
    private static string BuildScalarCapture(Context ctx)
    {
        if (ctx.Declared.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine($"DELETE FROM {ctx.Dbg}.Locals WHERE SessionId = {ctx.Sid};");

        var scalars = ctx.Declared.Select((v, i) => (v, i)).Where(x => !x.v.IsTable).ToList();
        if (scalars.Count == 0) return sb.ToString();

        sb.AppendLine($"INSERT INTO {ctx.Dbg}.Locals (SessionId, Ordinal, Name, TypeName, Value) VALUES");
        sb.AppendLine(string.Join(",\n", scalars.Select(x =>
            $"    ({ctx.Sid}, {x.i}, '{x.v.Name}', '{x.v.TypeName.Replace("'", "''")}', {ValueExpression(x.v)})")) + ";");
        return sb.ToString();
    }

    /// <summary>Tabellvariabler och temp-tabeller: de första raderna som JSON,
    /// antal rader i typnamnet (TABLE(n)). Temp-tabeller via dynamisk SQL bakom en
    /// OBJECT_ID-guard - en referens till en temp-tabell som inte finns skulle
    /// annars fälla hela statementet vid kompilering.</summary>
    private static string BuildTableCapture(Context ctx)
    {
        // Inuti sp_executesql är sid alltid parametern; värdet kommer utifrån.
        const string sidInDynamicSql = "@__dbg_sid";
        var sb = new StringBuilder();
        foreach (var (v, i) in ctx.Declared.Select((v, i) => (v, i)).Where(x => x.v.IsTable))
        {
            var insert = $"""
                INSERT INTO {ctx.Dbg}.Locals (SessionId, Ordinal, Name, TypeName, Value)
                SELECT {sidInDynamicSql}, {i}, '{v.Name}',
                       'TABLE(' + CAST((SELECT COUNT(*) FROM {v.Name}) AS NVARCHAR(20)) + ')',
                       (SELECT TOP ({TableCaptureRows}) * FROM {v.Name} FOR JSON AUTO, INCLUDE_NULL_VALUES);
                """;
            if (v.IsTempTable)
            {
                sb.AppendLine($"    IF OBJECT_ID('tempdb..{v.Name}') IS NOT NULL");
                sb.AppendLine($"        EXEC sp_executesql N'{insert.Replace("'", "''")}', N'@__dbg_sid UNIQUEIDENTIFIER', {ctx.Sid};");
            }
            else
            {
                sb.AppendLine(insert);
            }
        }
        return sb.ToString();
    }

    /// <summary>Efter en paus: läs in värden som klienten satt (setVariable) och töm.
    /// SELECT @x = ... utan träff lämnar @x orörd; en rad med NULL sätter NULL.</summary>
    private static string BuildOverridesApply(Context ctx)
    {
        var scalars = ctx.Declared.Where(v => !v.IsTable).ToList();
        if (scalars.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine($"    IF EXISTS (SELECT 1 FROM {ctx.Dbg}.Overrides WITH (NOLOCK) WHERE SessionId = {ctx.Sid})");
        sb.AppendLine("    BEGIN");
        foreach (var v in scalars)
        {
            var t = v.TypeName.ToLowerInvariant();
            var convert = t.StartsWith("binary") || t.StartsWith("varbinary")
                ? $"CONVERT({v.TypeName}, Value, 1)"
                : $"TRY_CONVERT({v.TypeName}, Value)";
            sb.AppendLine($"        SELECT {v.Name} = {convert} FROM {ctx.Dbg}.Overrides WITH (NOLOCK) WHERE SessionId = {ctx.Sid} AND Name = '{v.Name}';");
        }
        sb.AppendLine($"        DELETE FROM {ctx.Dbg}.Overrides WHERE SessionId = {ctx.Sid};");
        sb.AppendLine("    END");
        return sb.ToString();
    }

    /// <summary>Textrepresentation per typ: datum som ISO 8601 (stil 126, annars
    /// språkberoende "Jan 31 2024"), binärt som hex (stil 1), övrigt TRY_CONVERT.</summary>
    private static string ValueExpression(DeclaredVariable v)
    {
        var t = v.TypeName.ToLowerInvariant();
        if (t.StartsWith("date") || t.StartsWith("time") || t.StartsWith("smalldatetime"))
            return $"CONVERT(NVARCHAR(MAX), {v.Name}, 126)";
        if (t.StartsWith("binary") || t.StartsWith("varbinary") || t is "timestamp" or "rowversion")
            return $"CONVERT(NVARCHAR(MAX), {v.Name}, 1)";
        return $"TRY_CONVERT(NVARCHAR(MAX), {v.Name})";
    }

    private static InstrumentedScript Empty(string sourcePath, List<string> errors) => new()
    {
        Batches = [],
        SourcePath = sourcePath,
        LineMap = [],
        StmtToSpan = [],
        ScopeMap = [],
        FinalStmtIds = [],
        ResultVariables = [],
        Errors = errors
    };

    private sealed record Injection(int Offset, int Length, int Seq, string Text);

    private sealed class Context
    {
        public Context(string sql, string debugSchema)
        {
            Sql = sql;
            Dbg = debugSchema;
            var starts = new List<int> { 0 };
            for (var i = 0; i < sql.Length; i++)
                if (sql[i] == '\n') starts.Add(i + 1);
            _lineStarts = starts.ToArray();
        }

        public string Sql { get; }
        /// <summary>Kvalificerat schemanamn för __dbg-objekten, t.ex. "[MyDb].__dbg".</summary>
        public string Dbg { get; }
        public bool IsModule { get; init; }
        /// <summary>Uttryck som ger sessionens id i genererad SQL. Normalt variabeln
        /// som preludet deklarerar; vid instrumentering på plats finns inget prelude,
        /// och SESSION_CONTEXT måste läsas om vid varje statement eftersom den sätts
        /// mitt i körningen (när en främmande session fångas).</summary>
        public string Sid { get; init; } = "@__dbg_sid";
        /// <summary>Modulläge: variabeln som RETURN-uttryck fångas i (null för TVF).</summary>
        public string? ReturnVariable { get; init; }
        public Dictionary<int, int> LineMap { get; } = [];
        public Dictionary<int, StatementSpan> StmtToSpan { get; } = [];
        public Dictionary<int, IReadOnlyList<DeclaredVariable>> ScopeMap { get; } = [];
        public List<DeclaredVariable> Declared { get; } = [];
        public List<int> FinalStmtIds { get; } = [];

        private readonly int[] _lineStarts;
        private int _stmtId;
        private int _seq;

        public int NextSeq() => _seq++;

        /// <summary>Nytt stmtId med span och scope = variabler deklarerade före.</summary>
        public int RegisterStatement(int? line, StatementSpan span)
        {
            var id = _stmtId++;
            if (line is int l) LineMap.TryAdd(l, id);
            StmtToSpan[id] = span;
            ScopeMap[id] = Declared.ToList();
            return id;
        }

        /// <summary>1-baserad originalrad för ett offset.</summary>
        public int LineAt(int offset)
        {
            var idx = Array.BinarySearch(_lineStarts, offset);
            return (idx >= 0 ? idx : ~idx - 1) + 1;
        }
    }
}
