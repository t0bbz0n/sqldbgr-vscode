using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlDebugger.Sidecar.Parsing;

public class InstrumentedScript
{
    /// <summary>The instrumented batches, GO-separated in the source, in the
    /// order they run. Each runs on its own, because CREATE FUNCTION, CREATE PROC
    /// and others have to be alone in a batch.</summary>
    public required IReadOnlyList<InstrumentedBatch> Batches { get; init; }
    /// <summary>The original file that was instrumented; it travels with paused events.</summary>
    public required string SourcePath { get; init; }
    /// <summary>Line, 1-based, in the original file -> statementId.</summary>
    public required Dictionary<int, int> LineMap { get; init; }
    /// <summary>stmtId -> position in the original file, for paused events back to the client.</summary>
    public required Dictionary<int, StatementSpan> StmtToSpan { get; init; }
    /// <summary>stmtId -> the variables in scope, declared before, at that statement.</summary>
    public required Dictionary<int, IReadOnlyList<DeclaredVariable>> ScopeMap { get; init; }
    /// <summary>Pauses that show the final state: a virtual "end of batch", and
    /// RETURN in module mode. The runner forces a stop on these in module mode.</summary>
    public required IReadOnlyList<int> FinalStmtIds { get; init; }
    /// <summary>Variables to report at the end in module mode: the return value and OUTPUT parameters.</summary>
    public required IReadOnlyList<string> ResultVariables { get; init; }
    public required List<string> Errors { get; init; }
}

/// <summary>A runnable batch, plus the line map back to the original file, for SQL errors.</summary>
public record InstrumentedBatch(string Sql, IReadOnlyList<LineSegment> LineSegments)
{
    /// <summary>A line in the instrumented batch, 1-based, to the original line; 0 when unknown.</summary>
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

/// <summary>A text segment in the instrumented batch: it starts at line OutStart
/// and corresponds to original line OrigLine. Injected text points at the line it
/// was inserted at.</summary>
public record LineSegment(int OutStart, int OrigLine, bool Injected);

/// <summary>1-based line and column positions for a statement in the original file.</summary>
public record StatementSpan(int Line, int Column, int EndLine, int EndColumn);

public record DeclaredVariable(
    string Name, string TypeName, bool IsTable, bool IsTempTable = false,
    /// <summary>The type is an alias type (CREATE TYPE ... FROM), sysname or a
    /// CLR type. CONVERT accepts only system types, so one of these must never end
    /// up as the target type of a CONVERT or TRY_CONVERT.</summary>
    bool IsUserDefined = false);

/// <summary>A parse error with its position, for the client's Problems panel.</summary>
public record ParseIssue(int Line, int Column, string Message);

public record ModuleParameter(string Name, string TypeName, string? DefaultValue, bool IsOutput);

/// <summary>A CREATE or ALTER FUNCTION/PROCEDURE found in a script.</summary>
public record ModuleInfo(
    string Kind, string Name, IReadOnlyList<ModuleParameter> Parameters,
    bool CanScriptify, string? Reason);

public class ScriptDomAnalyzer
{
    private const string Header = "-- Instrumented by sqldbgr. NEVER deploy permanently.";
    private const string SidDeclaration =
        "DECLARE @__dbg_sid UNIQUEIDENTIFIER = CONVERT(UNIQUEIDENTIFIER, SESSION_CONTEXT(N'__dbg_session'));";

    /// <summary>The name the runner binds a module parameter under. The prelude
    /// redeclares it with the right type: DECLARE @a INT = @__p_a.</summary>
    public static string BoundParameterName(string parameterName)
        => "@__p_" + parameterName.TrimStart('@');

    public IReadOnlyList<ParseIssue> GetParseErrors(string sql)
    {
        new TSql160Parser(initialQuotedIdentifiers: true).Parse(new StringReader(sql), out var errors);
        return errors.Select(e => new ParseIssue(e.Line, e.Column, e.Message)).ToList();
    }

    /// <summary>Finds the first function or procedure definition in the script,
    /// for the extension's "debug the body?" question and for collecting
    /// parameters.</summary>
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

    /// <summary>Instruments the body of the script's first function or procedure
    /// as a standalone script. The runner binds the parameters as query
    /// parameters, and RETURN is rewritten to SET @__dbg_return plus a pause so
    /// the return value shows up in Locals. Other batches, GRANT and so on, do not
    /// run in this mode.</summary>
    /// <param name="debugSchema">For example "[MyDb].__dbg" - qualified, so a USE in the script cannot break the pauses.</param>
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
        prelude.AppendLine("SET DATEFORMAT ymd;"); // ISO dates in parameters are unambiguous in any language

        // The runner binds the parameters as @__p_<name> (NVARCHAR); they are
        // redeclared here with the signature's type. SQL Server does the
        // conversion, so an INT stays an INT rather than becoming string
        // concatenation, and a bad value surfaces as a SQL error.
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
            declared.Add(new DeclaredVariable(p.VariableName.Value, typeText, IsTable: false,
                IsUserDefined: IsUserDefinedType(p.DataType)));
        }

        // The return value: a scalar function or procedure goes to @__dbg_return.
        // For a multi-statement table function, its RETURNS @t TABLE (...) is
        // declared and reported as the result.
        string? returnVariable = "@__dbg_return";
        var resultVariables = new List<string>();
        switch (module)
        {
            case FunctionStatementBody { ReturnType: ScalarFunctionReturnType scalar }:
                var returnType = GetText(sql, scalar.DataType);
                prelude.AppendLine($"DECLARE @__dbg_return {returnType};");
                declared.Add(new DeclaredVariable("@__dbg_return", returnType, IsTable: false,
                    IsUserDefined: IsUserDefinedType(scalar.DataType)));
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

    /// <summary>Instruments a module IN PLACE: the whole CREATE or ALTER text is
    /// kept and pauses are spliced into the body only, so the result can be
    /// deployed with ALTER, which preserves permissions where drop and create
    /// would not. Used by attach mode, where the module is run by somebody else's
    /// session.</summary>
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
            // No prelude can declare a variable here, and SESSION_CONTEXT is set
            // mid-run when the session is caught, so read it every time.
            Sid = "CONVERT(UNIQUEIDENTIFIER, SESSION_CONTEXT(N'__dbg_session'))",
            // Foreign sessions run this same code with a NULL session id, so
            // nothing may be written until the gate says to pause.
            CaptureBeforeGate = false
        };

        // The module's own parameters are in scope from the start and are not redeclared.
        if (module is ProcedureStatementBody proc)
            foreach (var p in proc.Parameters)
                ctx.Declared.Add(new DeclaredVariable(
                    p.VariableName.Value, GetText(sql, p.DataType), IsTable: false,
                    IsUserDefined: IsUserDefinedType(p.DataType)));

        var injections = new List<Injection>();
        foreach (var stmt in statementList.Statements)
            InstrumentStatement(stmt, injections, ctx);

        // CREATE [OR ALTER] becomes ALTER, so the deploy keeps the module's permissions.
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
            // Variables do not survive a batch boundary: the scope restarts per batch.
            ctx.Declared.Clear();

            // Injections are spliced into the batch's original text, rather than
            // printing statements out flat, so that BEGIN/END, IF/ELSE and
            // WHILE structures are preserved and pauses land inside the blocks.
            var injections = new List<Injection>();
            foreach (var stmt in batch.Statements)
                InstrumentStatement(stmt, injections, ctx);
            if (injections.Count > 0)
                AddEndOfBatchPause(batch, injections, ctx);

            if (string.IsNullOrWhiteSpace(GetText(sql, batch))) continue;

            // SESSION_CONTEXT, set by the runner, carries the session id through
            // every batch, and is read into one variable per batch. Not in batches
            // without instrumentation, because CREATE PROC has to be the first
            // statement in its batch.
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

    /// <returns>The list of errors on a parse failure, otherwise null, with script set.</returns>
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

        // A branch without BEGIN/END, as in IF @x = 1 SELECT 1 ELSE ..., gets a
        // synthetic block around it. Otherwise the capture and pause land outside
        // the branch and run unconditionally, and the ELSE would no longer parse.
        injections.Add(new Injection(branch.StartOffset, 0, ctx.NextSeq(), "BEGIN\n"));
        InstrumentStatement(branch, injections, ctx);
        injections.Add(new Injection(EndOffset(branch), 0, ctx.NextSeq(), "\nEND\n"));
    }

    /// <summary>Pause BEFORE the statement: the highlighted statement is the one
    /// about to run, and Locals shows the state before it runs, which is what other
    /// debuggers do. Declarations are registered afterwards, so a variable appears
    /// from the next pause onwards.</summary>
    private void InstrumentLeaf(TSqlStatement stmt, List<Injection> injections, Context ctx)
    {
        // Module definitions - CREATE PROC, VIEW, TRIGGER and so on - have to be alone in their
        // batch, or text around them ends up in the module body. Not instrumented.
        if (stmt is ProcedureStatementBodyBase or ViewStatementBody or TriggerStatementBody)
            return;

        var id = ctx.RegisterStatement(stmt.StartLine, ComputeSpan(stmt));
        injections.Add(new Injection(stmt.StartOffset, 0, ctx.NextSeq(), PauseText(ctx, id)));
        TrackDeclarations(stmt, ctx);
    }

    /// <summary>RETURN pauses before, like any other statement. In module mode the
    /// whole statement is replaced: RETURN expr becomes SET @__dbg_return = expr,
    /// a pause, then RETURN, so the return value shows in Locals and the batch stays
    /// valid - RETURN with a value is only allowed inside a module. That pause
    /// counts as a final state.</summary>
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

    /// <summary>A virtual stop after the last statement, so the final state can be
    /// inspected; otherwise Locals disappears with the session. It is only reached
    /// when stepping, or always in module mode, where the runner forces it.</summary>
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
        // The expensive part - table variables as JSON, plus the procedure call -
        // only when there will actually be a pause. Otherwise every statement in a
        // loop pays for it.
        if (ctx.CaptureBeforeGate)
        {
            text.Append(BuildScalarCapture(ctx));
            text.AppendLine($"IF {ctx.Dbg}.ShouldPause({ctx.Sid}, {stmtId}) = 1");
            text.AppendLine("BEGIN");
            text.Append(BuildTableCapture(ctx));
            text.AppendLine($"    EXEC {ctx.Dbg}.Pause @stmt_id = {stmtId};");
            text.Append(BuildOverridesApply(ctx));
            text.AppendLine("END");
            return text.ToString();
        }

        // Instrumented in place: the session may be somebody else's and not yet
        // marked. BeginPause makes the claim and sets the session id; only then is
        // there anything to write locals under. If it loses that race, or this is
        // just ordinary traffic, the id stays NULL and we touch nothing.
        text.AppendLine($"IF {ctx.Dbg}.ShouldPause({ctx.Sid}, {stmtId}) = 1");
        text.AppendLine("BEGIN");
        text.AppendLine($"    EXEC {ctx.Dbg}.BeginPause;");
        text.AppendLine($"    IF {ctx.Sid} IS NOT NULL");
        text.AppendLine("    BEGIN");
        text.Append(BuildScalarCapture(ctx));
        text.Append(BuildTableCapture(ctx));
        text.AppendLine($"        EXEC {ctx.Dbg}.Pause @stmt_id = {stmtId};");
        text.Append(BuildOverridesApply(ctx));
        text.AppendLine("    END");
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

        // The last token can span several lines, a block comment for instance.
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline >= 0)
        {
            endLine += text.Count(c => c == '\n');
            endColumn = text.Length - lastNewline;
        }

        return new StatementSpan(stmt.StartLine, stmt.StartColumn, endLine, endColumn);
    }

    /// <summary>Splices the injections into the original text [start, end) and
    /// builds the line map back to the original file as it goes.</summary>
    private static InstrumentedBatch Splice(
        Context ctx, int start, int end, List<Injection> injections, string prefix)
    {
        var sb = new StringBuilder(prefix);
        // The prefix - header, DECLARE @__dbg_return - maps to the batch's first original line.
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
            pos = Math.Max(pos, inj.Offset + inj.Length); // Length > 0 means a replacement
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
                ctx.Declared.Add(new DeclaredVariable(d.VariableName.Value, typeName, IsTable: d.DataType is null,
                    IsUserDefined: IsUserDefinedType(d.DataType)));
            }
        }
        else if (stmt is DeclareTableVariableStatement tableDecl)
        {
            ctx.Declared.Add(new DeclaredVariable(
                tableDecl.Body.VariableName.Value, "TABLE", IsTable: true));
        }
        // Temp tables are captured as table variables, guarded with OBJECT_ID at
        // capture time because, unlike variables, they may not exist.
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

    /// <summary>Scalar variables: one DELETE and one INSERT ... VALUES per statement.</summary>
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

    /// <summary>Table variables and temp tables: the first rows as JSON, with the
    /// row count in the type name, TABLE(n). Temp tables go through dynamic SQL
    /// behind an OBJECT_ID guard, because a reference to a temp table that does not
    /// exist would otherwise fail the whole statement at compile time.</summary>
    private static string BuildTableCapture(Context ctx)
    {
        // Inside sp_executesql the sid is always the parameter; the value comes from outside.
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

    /// <summary>After a pause: read the values the client set with setVariable, and
    /// empty the table. SELECT @x = ... with no matching row leaves @x untouched; a
    /// row holding NULL sets NULL.</summary>
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
            // CONVERT accepts only system types: TRY_CONVERT(my_alias_type, ...)
            // is a compile error that fails the whole batch. Assignment converts
            // implicitly to the variable's own type anyway, so alias types go that
            // way - and a value that does not fit becomes a clear error rather
            // than a NULL.
            var convert = v.IsUserDefined
                ? "Value"
                : t.StartsWith("binary") || t.StartsWith("varbinary")
                    ? $"CONVERT({v.TypeName}, Value, 1)"
                    : $"TRY_CONVERT({v.TypeName}, Value)";
            sb.AppendLine($"        SELECT {v.Name} = {convert} FROM {ctx.Dbg}.Overrides WITH (NOLOCK) WHERE SessionId = {ctx.Sid} AND Name = '{v.Name}';");
        }
        sb.AppendLine($"        DELETE FROM {ctx.Dbg}.Overrides WHERE SessionId = {ctx.Sid};");
        sb.AppendLine("    END");
        return sb.ToString();
    }

    /// <summary>The text representation per type: dates as ISO 8601 (style 126;
    /// otherwise you get the language-dependent "Jan 31 2024"), binary as hex
    /// (style 1), everything else through TRY_CONVERT.</summary>
    private static string ValueExpression(DeclaredVariable v)
    {
        // An alias type can be called anything, and the name says nothing about
        // the base type, so do not guess from it. The target is NVARCHAR(MAX), a
        // system type, so TRY_CONVERT is valid whatever the source is.
        if (v.IsUserDefined) return $"TRY_CONVERT(NVARCHAR(MAX), {v.Name})";
        var t = v.TypeName.ToLowerInvariant();
        if (t.StartsWith("date") || t.StartsWith("time") || t.StartsWith("smalldatetime"))
            return $"CONVERT(NVARCHAR(MAX), {v.Name}, 126)";
        if (t.StartsWith("binary") || t.StartsWith("varbinary") || t is "timestamp" or "rowversion")
            return $"CONVERT(NVARCHAR(MAX), {v.Name}, 1)";
        return $"TRY_CONVERT(NVARCHAR(MAX), {v.Name})";
    }

    /// <summary>Anything that is not a built-in system type: alias types (CREATE
    /// TYPE ... FROM), sysname - which is itself an alias type - and CLR types.
    /// ScriptDom hands them all back as UserDataTypeReference.</summary>
    private static bool IsUserDefinedType(DataTypeReference? dataType) =>
        dataType is UserDataTypeReference;

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
        /// <summary>The qualified schema name for the __dbg objects, for example "[MyDb].__dbg".</summary>
        public string Dbg { get; }
        public bool IsModule { get; init; }
        /// <summary>The expression that yields the session id in generated SQL.
        /// Normally the variable the prelude declares. When instrumenting in place
        /// there is no prelude, and SESSION_CONTEXT has to be re-read at every
        /// statement because it is set mid-run, when a foreign session is caught.</summary>
        public string Sid { get; init; } = "@__dbg_sid";
        /// <summary>Whether scalar locals may be captured before the gate. True
        /// when the session is always ours, in which case the values are available
        /// even if a statement throws. False when instrumenting in place: there,
        /// foreign traffic runs the same code with a NULL session id, and an
        /// unconditional INSERT would blow up their call.</summary>
        public bool CaptureBeforeGate { get; init; } = true;
        /// <summary>Module mode: the variable a RETURN expression is captured in; null for a table function.</summary>
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

        /// <summary>A new stmtId, with its span, and a scope of the variables declared before it.</summary>
        public int RegisterStatement(int? line, StatementSpan span)
        {
            var id = _stmtId++;
            if (line is int l) LineMap.TryAdd(l, id);
            StmtToSpan[id] = span;
            ScopeMap[id] = Declared.ToList();
            return id;
        }

        /// <summary>The 1-based original line for an offset.</summary>
        public int LineAt(int offset)
        {
            var idx = Array.BinarySearch(_lineStarts, offset);
            return (idx >= 0 ? idx : ~idx - 1) + 1;
        }
    }
}
