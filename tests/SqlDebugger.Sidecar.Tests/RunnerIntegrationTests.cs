using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace SqlDebugger.Sidecar.Tests;

/// <summary>Kör mot en riktig SQL Server (SQLDBGR_TEST_CONNECTION). Verifierar
/// pausmekaniken, abort, exception-stopp, output och modulläge end-to-end.</summary>
public class RunnerIntegrationTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private string Cs => fixture.ConnectionString ?? throw new InvalidOperationException();

    private void RequireSqlServer() => Skip.If(fixture.ConnectionString is null, "SQLDBGR_TEST_CONNECTION är inte satt");

    [SkippableFact]
    public async Task Breakpoint_PausesBeforeStatement_AndLocalsShowPriorState()
    {
        RequireSqlServer();
        var run = await DebugRun.StartAsync(Cs, "DECLARE @x INT = 1;\nSET @x = @x + 1;\nSET @x = @x + 1;", [2]);

        var (line, reason) = await run.ExpectPausedAsync();
        Assert.Equal(2, line);
        Assert.Equal("breakpoint", reason);
        Assert.Equal("1", (await run.LocalsAsync())["@x"]); // före rad 2

        await run.Runner.SignalAsync("continue");
        await run.ExpectAsync("terminated");
    }

    [SkippableFact]
    public async Task StopOnEntry_PausesOnFirstStatement()
    {
        RequireSqlServer();
        var run = await DebugRun.StartAsync(Cs, "DECLARE @x INT = 1;\nSELECT @x;", [], stopOnEntry: true);
        var (line, reason) = await run.ExpectPausedAsync();
        Assert.Equal(1, line);
        Assert.Equal("entry", reason);
        await run.Runner.SignalAsync("continue");
        await run.ExpectAsync("terminated");
    }

    [SkippableFact]
    public async Task RepeatedPause_OnSameStatementInLoop_IsReportedEveryIteration()
    {
        RequireSqlServer();
        var run = await DebugRun.StartAsync(Cs, "DECLARE @i INT = 0;\nWHILE @i < 3 SET @i = @i + 1;\nSELECT @i AS Final;", [2]);

        for (var expected = 0; expected < 3; expected++)
        {
            var (line, _) = await run.ExpectPausedAsync();
            Assert.Equal(2, line);
            Assert.Equal(expected.ToString(), (await run.LocalsAsync())["@i"]);
            await run.Runner.SignalAsync("continue");
        }
        await run.ExpectAsync("terminated");
        Assert.Contains(run.Outputs, o => o.Contains("Final") && o.Contains("3"));
    }

    [SkippableFact]
    public async Task Step_StopsOnNextStatement_AndAtEndOfBatch()
    {
        RequireSqlServer();
        var run = await DebugRun.StartAsync(Cs, "DECLARE @x INT = 1;\nSET @x = 2;\nSET @x = 3;", [], stopOnEntry: true);
        Assert.Equal(1, (await run.ExpectPausedAsync()).line);
        await run.Runner.SignalAsync("stepOver");
        Assert.Equal((2, "step"), await run.ExpectPausedAsync());
        await run.Runner.SignalAsync("stepOver");
        Assert.Equal((3, "step"), await run.ExpectPausedAsync());
        await run.Runner.SignalAsync("stepOver");
        // virtuellt slutstopp: sista raden, med slutläget i Locals
        var (endLine, _) = await run.ExpectPausedAsync();
        Assert.Equal(3, endLine);
        Assert.Equal("3", (await run.LocalsAsync())["@x"]);
        await run.Runner.SignalAsync("continue");
        await run.ExpectAsync("terminated");
    }

    [SkippableFact]
    public async Task PrintAndResultSets_ReachOutput()
    {
        RequireSqlServer();
        var run = await DebugRun.StartAsync(Cs, "PRINT 'hej från print';\nSELECT 42 AS Answer, N'åäö' AS Text;", []);
        await run.ExpectAsync("terminated");
        Assert.Contains(run.Outputs, o => o.Contains("hej från print"));
        Assert.Contains(run.Outputs, o => o.Contains("Answer") && o.Contains("42") && o.Contains("åäö"));
    }

    [SkippableFact]
    public async Task Abort_DoesNotRunRemainingStatements()
    {
        RequireSqlServer();
        await using (var conn = new SqlConnection(Cs)) await conn.ExecuteAsync("TRUNCATE TABLE dbo.AbortProbe");
        var run = await DebugRun.StartAsync(Cs,
            "INSERT INTO dbo.AbortProbe VALUES (1);\nINSERT INTO dbo.AbortProbe VALUES (2);\nINSERT INTO dbo.AbortProbe VALUES (3);", [2]);

        Assert.Equal(2, (await run.ExpectPausedAsync()).line);
        await run.Runner.StopAsync();
        await run.ExpectAsync("terminated");

        await using var check = new SqlConnection(Cs);
        Assert.Equal(1, await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.AbortProbe"));
    }

    [SkippableFact]
    public async Task SqlError_StopsOnOriginalLine_WithLocalsFromBefore()
    {
        RequireSqlServer();
        var run = await DebugRun.StartAsync(Cs, "DECLARE @x INT = 0;\nSELECT 1 / @x AS Boom;\nSELECT 2 AS NeverRuns;", []);

        var e = await run.ExpectAsync("paused");
        Assert.Equal("exception", e.GetProperty("reason").GetString());
        Assert.Equal(2, e.GetProperty("stack")[0].GetProperty("line").GetInt32());
        Assert.Contains("Divide by zero", e.GetProperty("text").GetString());
        Assert.Equal("0", (await run.LocalsAsync())["@x"]);

        await run.Runner.SignalAsync("continue");
        await run.ExpectAsync("terminated");
        Assert.DoesNotContain(run.Outputs, o => o.Contains("NeverRuns"));
        Assert.Contains(run.Outputs, o => o.Contains("line 2"));
    }

    [SkippableFact]
    public async Task UseStatement_DoesNotBreakPausing()
    {
        RequireSqlServer();
        var run = await DebugRun.StartAsync(Cs, "USE master;\nSELECT DB_NAME() AS Db;", [2]);
        Assert.Equal(2, (await run.ExpectPausedAsync()).line);
        await run.Runner.SignalAsync("continue");
        await run.ExpectAsync("terminated");
        Assert.Contains(run.Outputs, o => o.Contains("master"));
    }

    [SkippableFact]
    public async Task ModuleMode_TypedParameters_ReturnValueAndOutput()
    {
        RequireSqlServer();
        var proc = """
            CREATE PROCEDURE dbo.Calc @a INT, @b INT = 10, @when DATETIME, @result INT OUTPUT
            AS
            BEGIN
                SET @result = @a + @b;
                IF @when < '2000-01-01' RETURN 99;
                RETURN 7;
            END
            """;
        var run = await DebugRun.StartAsync(Cs, proc, [], mode: "module",
            parameters: new() { ["@a"] = "5", ["@b"] = "10", ["@when"] = "2024-01-31", ["@result"] = null });

        // RETURN är ett tvingat slutstopp i modulläge: returvärde + OUTPUT synliga
        var (line, _) = await run.ExpectPausedAsync();
        Assert.Equal(6, line);
        var locals = await run.LocalsAsync();
        Assert.Equal("15", locals["@result"]);   // INT + INT, inte '510'
        Assert.Equal("7", locals["@__dbg_return"]);
        Assert.StartsWith("2024-01-31", locals["@when"]); // ISO via stil 126

        await run.Runner.SignalAsync("continue");
        await run.ExpectAsync("terminated");
        Assert.Contains(run.Outputs, o => o.Contains("return value = 7") && o.Contains("@result = 15"));
    }

    [SkippableFact]
    public async Task PauseInsideUserTransaction_DoesNotDeadlock()
    {
        RequireSqlServer();
        // Regression: Pause skrev tidigare i Control inne i användarens transaktion,
        // och X-låset blockerade sidecarens continue-signal för evigt.
        var run = await DebugRun.StartAsync(Cs,
            "BEGIN TRAN;\nINSERT INTO dbo.AbortProbe VALUES (10);\nSELECT 1 AS InsideTran;\nROLLBACK;", [3]);
        Assert.Equal(3, (await run.ExpectPausedAsync()).line);
        Assert.NotNull(await run.LocalsAsync()); // NOLOCK-läsning får inte blockera
        await run.Runner.SignalAsync("continue");
        await run.ExpectAsync("terminated");
    }

    [SkippableFact]
    public async Task TransactionRollback_UndoesChanges()
    {
        RequireSqlServer();
        await using (var conn = new SqlConnection(Cs)) await conn.ExecuteAsync("TRUNCATE TABLE dbo.AbortProbe");
        var run = await DebugRun.StartAsync(Cs, "INSERT INTO dbo.AbortProbe VALUES (20);\nSELECT COUNT(*) AS DuringRun FROM dbo.AbortProbe;", [], transaction: "rollback");
        await run.ExpectAsync("terminated");
        Assert.Contains(run.Outputs, o => o.Contains("DuringRun") && o.Contains("1"));
        Assert.Contains(run.Outputs, o => o.Contains("rolled back"));
        await using var check = new SqlConnection(Cs);
        Assert.Equal(0, await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.AbortProbe"));
    }

    [SkippableFact]
    public async Task ConditionalBreakpoint_StopsOnlyWhenConditionIsTrue()
    {
        RequireSqlServer();
        var run = await DebugRun.StartAsync(Cs, "DECLARE @i INT = 0;\nWHILE @i < 5 SET @i = @i + 1;\nSELECT @i;", [],
            breakpoints: [new DebugRun.Bp(2, Condition: "@i = 3")]);
        Assert.Equal(2, (await run.ExpectPausedAsync()).line);
        Assert.Equal("3", (await run.LocalsAsync())["@i"]);
        await run.Runner.SignalAsync("continue");
        await run.ExpectAsync("terminated");
    }

    [SkippableFact]
    public async Task HitCountBreakpoint()
    {
        RequireSqlServer();
        var run = await DebugRun.StartAsync(Cs, "DECLARE @i INT = 0;\nWHILE @i < 5 SET @i = @i + 1;\nSELECT @i;", [],
            breakpoints: [new DebugRun.Bp(2, HitCondition: ">= 4")]);
        await run.ExpectPausedAsync();
        Assert.Equal("3", (await run.LocalsAsync())["@i"]); // fjärde träffen
        await run.Runner.SignalAsync("continue");
        await run.ExpectPausedAsync();
        Assert.Equal("4", (await run.LocalsAsync())["@i"]);
        await run.Runner.SignalAsync("continue");
        await run.ExpectAsync("terminated");
    }

    [SkippableFact]
    public async Task Logpoint_PrintsWithoutStopping()
    {
        RequireSqlServer();
        var run = await DebugRun.StartAsync(Cs, "DECLARE @i INT = 0;\nWHILE @i < 3 SET @i = @i + 1;\nSELECT @i;", [],
            breakpoints: [new DebugRun.Bp(2, LogMessage: "i is {@i} and doubled {@i * 2}")]);
        await run.ExpectAsync("terminated");
        Assert.Contains(run.Outputs, o => o.Contains("i is 0 and doubled 0"));
        Assert.Contains(run.Outputs, o => o.Contains("i is 2 and doubled 4"));
    }

    [SkippableFact]
    public async Task SetVariable_TakesEffectOnResume()
    {
        RequireSqlServer();
        var run = await DebugRun.StartAsync(Cs, "DECLARE @x INT = 1;\nSET @x = @x + 1;\nSELECT @x AS X;", [2]);
        await run.ExpectPausedAsync();
        await run.Runner.SetVariableAsync("@x", "10");
        Assert.Equal("10", (await run.LocalsAsync())["@x"]); // speglas direkt i Locals
        await run.Runner.SignalAsync("continue");
        await run.ExpectAsync("terminated");
        Assert.Contains(run.Outputs, o => o.Contains("X") && o.Contains("11"));
    }

    [SkippableFact]
    public async Task TempTable_AppearsInLocals()
    {
        RequireSqlServer();
        var run = await DebugRun.StartAsync(Cs, "CREATE TABLE #t (a INT);\nINSERT INTO #t VALUES (1), (2);\nSELECT 1;", [3]);
        await run.ExpectPausedAsync();
        var locals = await run.Runner.GetLocalsAsync();
        var t = Assert.Single(locals, l => l.Name == "#t");
        Assert.Equal("TABLE(2)", t.TypeName);
        Assert.Contains("\"a\":2", t.Value);
        await run.Runner.SignalAsync("continue");
        await run.ExpectAsync("terminated");
    }

    [SkippableFact]
    public async Task Evaluate_ComputesExpressionFromLocals()
    {
        RequireSqlServer();
        var run = await DebugRun.StartAsync(Cs, "DECLARE @x INT = 4;\nDECLARE @s NVARCHAR(10) = N'ab';\nSELECT 1;", [3]);
        await run.ExpectPausedAsync();
        Assert.Equal(("40", (string?)null), await run.Runner.EvaluateAsync("@x * 10"));
        Assert.Equal(("abab", (string?)null), await run.Runner.EvaluateAsync("@s + @s"));
        var (_, error) = await run.Runner.EvaluateAsync("1 / 0");
        Assert.NotNull(error);
        await run.Runner.SignalAsync("continue");
        await run.ExpectAsync("terminated");
    }

    [SkippableFact]
    public async Task ModuleMode_TableValuedFunction()
    {
        RequireSqlServer();
        var fn = """
            CREATE FUNCTION dbo.Numbers(@n INT)
            RETURNS @t TABLE (N INT NOT NULL)
            AS
            BEGIN
                DECLARE @i INT = 1;
                WHILE @i <= @n
                BEGIN
                    INSERT INTO @t (N) VALUES (@i);
                    SET @i = @i + 1;
                END
                RETURN;
            END
            """;
        var run = await DebugRun.StartAsync(Cs, fn, [], mode: "module", parameters: new() { ["@n"] = "3" });
        await run.ExpectPausedAsync(); // RETURN
        var locals = await run.LocalsAsync();
        Assert.Contains("\"N\":3", locals["@t"]);
        await run.Runner.SignalAsync("continue");
        await run.ExpectAsync("terminated");
    }
}
