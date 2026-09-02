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
        Assert.Contains(run.Outputs, o => o.Contains("rad 2"));
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
        Assert.Contains(run.Outputs, o => o.Contains("returvärde = 7") && o.Contains("@result = 15"));
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
