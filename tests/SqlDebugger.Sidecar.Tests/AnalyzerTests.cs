using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlDebugger.Sidecar.Parsing;
using Xunit;

namespace SqlDebugger.Sidecar.Tests;

/// <summary>Enhetstester för instrumenteringen - kräver ingen databas.</summary>
public class AnalyzerTests
{
    private readonly ScriptDomAnalyzer _analyzer = new();

    private static void AssertReparses(string sql)
    {
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors);
        Assert.True(errors.Count == 0,
            "Instrumenterad SQL parsar inte: " + string.Join("; ", errors.Select(e => $"rad {e.Line}: {e.Message}")));
    }

    [Fact]
    public void NestedControlFlow_IsInstrumentedAndReparses()
    {
        var sql = """
            DECLARE @i INT = 0;
            IF @i = 0 SET @i = 1 ELSE SET @i = -1;
            WHILE @i < 3
            BEGIN
                SET @i = @i + 1;
                IF @i = 2 PRINT 'two';
            END
            BEGIN TRY
                IF @i > 1 PRINT 'big' ELSE PRINT 'small';
            END TRY
            BEGIN CATCH
                SET @i = NULL;
            END CATCH
            """;
        var r = _analyzer.Instrument(sql, "/t.sql");
        Assert.Empty(r.Errors);
        var batch = Assert.Single(r.Batches);
        AssertReparses(batch.Sql);
        // grenar utan BEGIN/END wrappas så pausen bara körs när grenen körs
        Assert.Contains("BEGIN\n", batch.Sql);
        Assert.True(r.LineMap.ContainsKey(5), "statement inne i WHILE-blocket ska ha breakpoint-mappning");
        Assert.True(r.LineMap.ContainsKey(6), "IF-gren inne i loopen ska ha breakpoint-mappning");
    }

    [Fact]
    public void PauseBefore_ScopeExcludesTheStatementsOwnDeclaration()
    {
        var sql = "DECLARE @i INT = 0;\nDECLARE @j INT = 1;\nSET @i = @j;";
        var r = _analyzer.Instrument(sql, "/t.sql", "[Db].__dbg");
        var text = r.Batches[0].Sql;
        Assert.True(text.IndexOf("Pause @stmt_id = 0;") < text.IndexOf("DECLARE @i INT = 0;"), "pausen ska ligga före statementet");
        Assert.Empty(r.ScopeMap[0]);
        Assert.Equal(["@i"], r.ScopeMap[1].Select(v => v.Name));
        Assert.Equal(["@i", "@j"], r.ScopeMap[2].Select(v => v.Name));
        Assert.Equal("INT", r.ScopeMap[1][0].TypeName);
    }

    [Fact]
    public void GoBatches_AreSeparateAndScopeResets()
    {
        var sql = "DECLARE @x INT = 1;\nSELECT @x;\nGO\nDECLARE @y INT = 2;\nSELECT @y;";
        var r = _analyzer.Instrument(sql, "/t.sql");
        Assert.Equal(2, r.Batches.Count);
        foreach (var b in r.Batches) AssertReparses(b.Sql);
        Assert.Equal(["@y"], r.ScopeMap[r.LineMap[5]].Select(v => v.Name));
    }

    [Fact]
    public void ModuleDefinitionBatch_GetsNoPreludeOrPause()
    {
        var sql = "CREATE PROCEDURE dbo.P AS SELECT 1;\nGO\nGRANT EXECUTE ON dbo.P TO public;";
        var r = _analyzer.Instrument(sql, "/t.sql");
        Assert.Equal(2, r.Batches.Count);
        Assert.DoesNotContain("__dbg", r.Batches[0].Sql);
        Assert.Contains("__dbg.Pause", r.Batches[1].Sql);
        AssertReparses(r.Batches[0].Sql);
        AssertReparses(r.Batches[1].Sql);
    }

    [Fact]
    public void DebugSchema_IsQualifiedEverywhere()
    {
        var r = _analyzer.Instrument("DECLARE @t TABLE (a INT);\nSELECT 1;", "/t.sql", "[My Db].__dbg");
        var text = r.Batches[0].Sql;
        Assert.Contains("[My Db].__dbg.Pause", text);
        Assert.Contains("[My Db].__dbg.Locals", text);
        Assert.Contains("[My Db].__dbg.ShouldPause", text);
        Assert.DoesNotContain(" __dbg.", text);
    }

    [Fact]
    public void CaptureIsGated_TableVariablesOnlyInsideShouldPauseBlock()
    {
        var r = _analyzer.Instrument("DECLARE @t TABLE (a INT);\nDECLARE @d DATETIME;\nSELECT 1;", "/t.sql");
        var text = r.Batches[0].Sql;
        var stmt = r.LineMap[3];
        var ifIdx = text.IndexOf($"ShouldPause(@__dbg_sid, {stmt})");
        var tblIdx = text.IndexOf("FOR JSON AUTO", ifIdx);
        var pauseIdx = text.IndexOf($"Pause @stmt_id = {stmt}", ifIdx);
        Assert.True(ifIdx > 0 && tblIdx > ifIdx && pauseIdx > tblIdx, "tabellcapture ska ligga inuti IF-blocket före Pause");
        Assert.Contains("CONVERT(NVARCHAR(MAX), @d, 126)", text);
    }

    [Fact]
    public void LineMap_MapsInstrumentedLinesBackToOriginal()
    {
        var sql = "DECLARE @x INT = 0;\nSELECT 1,\n       2 / @x;\nSELECT 3;";
        var r = _analyzer.Instrument(sql, "/t.sql");
        var batch = r.Batches[0];
        var lines = batch.Sql.Split('\n');
        var divLine = Array.FindIndex(lines, l => l.Contains("2 / @x")) + 1;
        Assert.Equal(3, batch.MapLine(divLine));
        Assert.Equal(1, batch.MapLine(1)); // prefixet mappas till batchens första rad
        var pauseLine = Array.FindIndex(lines, l => l.Contains("Pause @stmt_id = 2")) + 1;
        Assert.Equal(4, batch.MapLine(pauseLine));
    }

    [Fact]
    public void ModuleBody_TypedParametersAndReturnRewrite()
    {
        var sql = """
            CREATE PROCEDURE dbo.Calc @a INT, @b INT = 10, @result INT OUTPUT
            AS
            BEGIN
                IF @a IS NULL RETURN 1;
                SET @result = @a + @b;
                RETURN 0;
            END
            """;
        var info = _analyzer.InspectModule(sql);
        Assert.NotNull(info);
        Assert.Equal("dbo.Calc", info!.Name);
        Assert.True(info.Parameters.Single(p => p.Name == "@result").IsOutput);
        Assert.Equal("10", info.Parameters.Single(p => p.Name == "@b").DefaultValue);

        var r = _analyzer.InstrumentModuleBody(sql, "/p.sql", "[Db].__dbg");
        Assert.Empty(r.Errors);
        var text = r.Batches[0].Sql;
        AssertReparses(text);
        Assert.Contains("DECLARE @a INT = @__p_a;", text);
        Assert.Contains("SET @__dbg_return = (1);", text);
        Assert.Equal(["@__dbg_return", "@result"], r.ResultVariables);
        Assert.Equal(3, r.FinalStmtIds.Count); // två RETURN + slut på kroppen
    }

    [Fact]
    public void ModuleBody_MultiStatementTvf()
    {
        var sql = """
            CREATE FUNCTION dbo.Numbers(@n INT)
            RETURNS @t TABLE (N INT NOT NULL)
            AS
            BEGIN
                INSERT INTO @t (N) VALUES (@n);
                RETURN;
            END
            """;
        var r = _analyzer.InstrumentModuleBody(sql, "/f.sql");
        Assert.Empty(r.Errors);
        AssertReparses(r.Batches[0].Sql);
        Assert.Contains("DECLARE @t TABLE (N INT NOT NULL);", r.Batches[0].Sql);
        Assert.Equal(["@t"], r.ResultVariables);
    }

    [Fact]
    public void InlineTvf_IsReportedAsNotScriptifiable()
    {
        var info = _analyzer.InspectModule("CREATE FUNCTION dbo.F() RETURNS TABLE AS RETURN (SELECT 1 AS x);");
        Assert.NotNull(info);
        Assert.False(info!.CanScriptify);
    }

    [Fact]
    public void TempTables_AreCapturedBehindObjectIdGuard()
    {
        var r = _analyzer.Instrument("CREATE TABLE #t (a INT);\nSELECT 1 AS x INTO #s;\nSELECT 2;", "/t.sql");
        var text = r.Batches[0].Sql;
        Assert.Contains("IF OBJECT_ID('tempdb..#t') IS NOT NULL", text);
        Assert.Contains("IF OBJECT_ID('tempdb..#s') IS NOT NULL", text);
        Assert.Contains("EXEC sp_executesql", text);
        AssertReparses(text);
        Assert.Equal(["#t", "#s"], r.ScopeMap[r.LineMap[3]].Select(v => v.Name));
    }

    [Fact]
    public void Overrides_AreAppliedAfterPause()
    {
        var r = _analyzer.Instrument("DECLARE @x INT = 1;\nSELECT @x;", "/t.sql", "[Db].__dbg");
        var text = r.Batches[0].Sql;
        Assert.Contains("SELECT @x = TRY_CONVERT(INT, Value) FROM [Db].__dbg.Overrides", text);
        Assert.True(text.IndexOf("Overrides") > text.IndexOf("Pause @stmt_id = 1"), "overrides läses efter Pause");
        AssertReparses(text);
    }

    [Fact]
    public void InstrumentInPlace_KeepsTheModuleAndTurnsCreateIntoAlter()
    {
        var sql = """
            CREATE PROCEDURE dbo.Calc @a INT, @b INT
            AS
            BEGIN
                DECLARE @sum INT;
                SET @sum = @a + @b;
                SELECT @sum;
            END
            """;
        var r = _analyzer.InstrumentModuleInPlace(sql, "/p.sql", "[Db].__dbgpro");
        Assert.Empty(r.Errors);
        var text = Assert.Single(r.Batches).Sql;
        AssertReparses(text);

        // ALTER preserves permissions; drop/create would not.
        Assert.Contains("ALTER PROCEDURE dbo.Calc", text);
        Assert.DoesNotContain("CREATE PROCEDURE", text);
        // The body is instrumented in place - no scriptified prelude.
        Assert.Contains("[Db].__dbgpro.Pause", text);
        Assert.DoesNotContain("@__p_a", text);
        // The module's own parameters are in scope without being redeclared.
        Assert.DoesNotContain("DECLARE @a INT =", text);
        Assert.Equal(["@a", "@b"], r.ScopeMap[r.LineMap[4]].Select(v => v.Name));
    }

    [Fact]
    public void InstrumentInPlace_ReadsSessionContextInline()
    {
        // Pause sets SESSION_CONTEXT mid-run when it catches a foreign session,
        // so a value cached in a variable would stay NULL for the rest of the
        // module and every later statement would fall through.
        var sql = "CREATE PROCEDURE dbo.P AS BEGIN SELECT 1; SELECT 2; END";
        var text = _analyzer.InstrumentModuleInPlace(sql, "/p.sql", "[Db].__dbgpro").Batches[0].Sql;
        AssertReparses(text);
        Assert.DoesNotContain("@__dbg_sid UNIQUEIDENTIFIER =", text);
        Assert.Contains("SESSION_CONTEXT(N'__dbg_session')", text);
    }

    [Fact]
    public void InstrumentInPlace_HandlesCreateOrAlter()
    {
        var sql = "CREATE OR ALTER PROCEDURE dbo.P AS BEGIN SELECT 1; END";
        var text = _analyzer.InstrumentModuleInPlace(sql, "/p.sql").Batches[0].Sql;
        AssertReparses(text);
        Assert.Contains("ALTER PROCEDURE dbo.P", text);
        Assert.DoesNotContain("ALTER OR ALTER", text);
    }

    [Fact]
    public void InstrumentInPlace_RejectsAScriptWithoutAModule()
    {
        var r = _analyzer.InstrumentModuleInPlace("SELECT 1;", "/p.sql");
        Assert.NotEmpty(r.Errors);
    }

    [Fact]
    public void ParseErrors_HavePositions()
    {
        var errors = _analyzer.GetParseErrors("SELECT 1;\nSELECT FROM WHERE;");
        Assert.NotEmpty(errors);
        Assert.Equal(2, errors[0].Line);
        Assert.True(errors[0].Column > 0);
    }

    [Fact]
    public void ParseErrors_AreReportedWithLine()
    {
        var r = _analyzer.Instrument("SELECT FROM WHERE;", "/t.sql");
        Assert.NotEmpty(r.Errors);
        Assert.StartsWith("Line 1:", r.Errors[0]);
    }
}
