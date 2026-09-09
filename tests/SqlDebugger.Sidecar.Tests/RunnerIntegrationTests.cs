using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace SqlDebugger.Sidecar.Tests;

/// <summary>Kör mot en riktig SQL Server (SQLDBGR_TEST_CONNECTION). Verifierar
/// pausmekaniken, abort, exception-stopp, output och modulläge end-to-end.</summary>
[Collection(SqlServerCollection.Name)]
public class RunnerIntegrationTests(SqlServerFixture fixture)
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

    /// <summary>Två VS Code-fönster mot samma databas är helt normalt, och
    /// __dbg-tabellerna delas. Sessionerna måste hållas isär på SessionId.</summary>
    [SkippableFact]
    public async Task TwoConcurrentSessions_DoNotSeeEachOthersState()
    {
        RequireSqlServer();
        var first = await DebugRun.StartAsync(Cs, "DECLARE @a INT = 111;\nSET @a = @a + 1;\nSELECT @a AS A;", [2]);
        var second = await DebugRun.StartAsync(Cs, "DECLARE @b INT = 222;\nSET @b = @b + 1;\nSELECT @b AS B;", [2]);

        await first.ExpectPausedAsync();
        await second.ExpectPausedAsync();

        // Var och en ser bara sina egna variabler.
        var firstLocals = await first.LocalsAsync();
        var secondLocals = await second.LocalsAsync();
        Assert.Equal("111", firstLocals["@a"]);
        Assert.DoesNotContain("@b", firstLocals.Keys);
        Assert.Equal("222", secondLocals["@b"]);
        Assert.DoesNotContain("@a", secondLocals.Keys);

        // Att fortsätta den ena får inte röra den andra.
        await first.Runner.SignalAsync("continue");
        await first.ExpectAsync("terminated");
        Assert.Equal("222", (await second.LocalsAsync())["@b"]);

        await second.Runner.SignalAsync("continue");
        await second.ExpectAsync("terminated");

        Assert.Contains(first.Outputs, o => o.Contains("112"));
        Assert.Contains(second.Outputs, o => o.Contains("223"));
    }

    /// <summary>transaction: commit är motsatsen till rollback-läget och den
    /// enda inställningen som med flit lämnar kvar ändringar.</summary>
    [SkippableFact]
    public async Task TransactionCommit_KeepsChanges()
    {
        RequireSqlServer();
        await using (var conn = new SqlConnection(Cs))
            await conn.ExecuteAsync("""
                IF OBJECT_ID('dbo.CommitProbe') IS NULL CREATE TABLE dbo.CommitProbe (Value INT);
                DELETE FROM dbo.CommitProbe;
                """);

        var run = await DebugRun.StartAsync(
            Cs, "INSERT INTO dbo.CommitProbe (Value) VALUES (7);\nSELECT 1 AS Done;", [],
            transaction: "commit");
        await run.ExpectAsync("terminated");

        await using var check = new SqlConnection(Cs);
        Assert.Equal(7, await check.ExecuteScalarAsync<int>("SELECT TOP 1 Value FROM dbo.CommitProbe"));
    }

    /// <summary>Pause-knappen sätter kommandot till stepOver på en batch som
    /// redan kör, och pausloopen stannar då vid nästa statement oavsett
    /// breakpoints. Utan det finns ingen väg att stanna en långkörande batch.</summary>
    [SkippableFact]
    public async Task Pause_StopsARunningBatchAtTheNextStatement()
    {
        RequireSqlServer();
        // WAITFOR ger ett fönster att hinna signalera i; utan det kan batchen
        // vara klar innan signalen når fram och testet bli tidsberoende.
        var run = await DebugRun.StartAsync(Cs, string.Join("\n", new[]
        {
            "DECLARE @x INT = 0;",
            "WAITFOR DELAY '00:00:02';",
            "SET @x = 1;",
            "WAITFOR DELAY '00:00:02';",
            "SET @x = 2;",
            "SELECT @x AS X;"
        }), []);   // inga breakpoints: batchen kör fritt

        await run.Runner.SignalAsync("stepOver");

        var (line, reason) = await run.ExpectPausedAsync();
        // Sidecaren rapporterar "step"; etiketten "pause" sätter adaptern själv
        // när det var Pause-knappen som skickade signalen.
        Assert.Equal("step", reason);
        // Den stannade någonstans mitt i, inte på sista raden.
        Assert.InRange(line, 2, 5);

        await run.Runner.SignalAsync("continue");
        await run.ExpectAsync("terminated");
        Assert.Contains(run.Outputs, o => o.Contains("2"));
    }

    /// <summary>debugDatabase finns för miljöer där man inte får skapa objekt i
    /// måldatabasen. Då måste hela mekaniken fungera med __dbg någon
    /// annanstans - och måldatabasen får inte röras.</summary>
    [SkippableFact]
    public async Task DebugDatabase_KeepsTheSchemaOutOfTheTargetDatabase()
    {
        RequireSqlServer();
        const string other = "sqldbgr_test_dbg";
        await using (var master = new SqlConnection(
            new SqlConnectionStringBuilder(Cs) { InitialCatalog = "master" }.ConnectionString))
        {
            await master.ExecuteAsync($"IF DB_ID('{other}') IS NULL CREATE DATABASE {other}");
        }
        // Måldatabasen ska inte ha något __dbg efteråt; städa bort spår från
        // tidigare tester så assertionen betyder något.
        await using (var target = new SqlConnection(Cs))
            await target.ExecuteAsync("""
                IF SCHEMA_ID('__dbg') IS NOT NULL
                BEGIN
                    IF OBJECT_ID('__dbg.Pause') IS NOT NULL DROP PROCEDURE __dbg.Pause;
                    IF OBJECT_ID('__dbg.ShouldPause') IS NOT NULL DROP FUNCTION __dbg.ShouldPause;
                    IF OBJECT_ID('__dbg.Locals') IS NOT NULL DROP TABLE __dbg.Locals;
                    IF OBJECT_ID('__dbg.Overrides') IS NOT NULL DROP TABLE __dbg.Overrides;
                    IF OBJECT_ID('__dbg.PauseState') IS NOT NULL DROP TABLE __dbg.PauseState;
                    IF OBJECT_ID('__dbg.Control') IS NOT NULL DROP TABLE __dbg.Control;
                    DROP SCHEMA __dbg;
                END
                """);

        var run = await DebugRun.StartAsync(
            Cs, "DECLARE @x INT = 5;\nSET @x = @x * 2;\nSELECT @x AS X;", [2],
            debugDatabase: other);

        await run.ExpectPausedAsync();
        Assert.Equal("5", (await run.LocalsAsync())["@x"]);
        await run.Runner.SignalAsync("continue");
        await run.ExpectAsync("terminated");
        Assert.Contains(run.Outputs, o => o.Contains("10"));

        await using var check = new SqlConnection(Cs);
        Assert.Equal(0, await check.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.schemas WHERE name = '__dbg'"));
    }

    /// <summary>Aliastyper (CREATE TYPE ... FROM) är vanliga i äldre scheman.
    /// De går att DECLARE:a men CONVERT tar bara systemtyper, så en genererad
    /// TRY_CONVERT(min_typ, ...) fällde hela batchen redan vid kompileringen.</summary>
    [SkippableFact]
    public async Task ModuleMode_UserDefinedAliasTypes()
    {
        RequireSqlServer();
        await using (var conn = new SqlConnection(Cs))
            await conn.ExecuteAsync("""
                IF TYPE_ID('pv_motnr') IS NULL CREATE TYPE pv_motnr FROM VARCHAR(20) NULL;
                IF TYPE_ID('pv_amount') IS NULL CREATE TYPE pv_amount FROM DECIMAL(18, 2) NULL;
                """);

        var proc = """
            CREATE PROCEDURE dbo.AliasTyped @sMotnr pv_motnr, @amount dbo.pv_amount, @sName sysname
            AS
            BEGIN
                DECLARE @local pv_motnr;
                SET @local = @sMotnr;
                SELECT @local AS motnr, @amount AS amount, @sName AS name;
            END
            """;
        var run = await DebugRun.StartAsync(Cs, proc, [5], mode: "module",
            parameters: new() { ["@sMotnr"] = "M-1", ["@amount"] = "12.50", ["@sName"] = "dbo" });

        var (line, _) = await run.ExpectPausedAsync();
        Assert.Equal(5, line);
        var locals = await run.LocalsAsync();
        Assert.Equal("M-1", locals["@sMotnr"]);
        Assert.Equal("12.50", locals["@amount"]);
        Assert.Equal("dbo", locals["@sName"]);

        // Overrides är den väg som genererade CONVERT till aliastypen. Assignment
        // konverterar implicit till variabelns egen typ.
        await run.Runner.SetVariableAsync("@sMotnr", "M-2");
        Assert.Equal("M-2", (await run.LocalsAsync())["@sMotnr"]);

        // Modulläge har ett tvingat slutstopp så returvärde och OUTPUT syns
        // innan sessionen tar slut - SELECT:en har då redan kört.
        await run.Runner.SignalAsync("continue");
        var (endLine, _) = await run.ExpectPausedAsync();
        Assert.Equal(7, endLine);

        await run.Runner.SignalAsync("continue");
        await run.ExpectAsync("terminated");
        Assert.Contains(run.Outputs, o => o.Contains("M-2") && o.Contains("12.50"));
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
    public async Task ModuleMode_BreakpointInsideProcedureBody()
    {
        RequireSqlServer();
        var proc = """
            CREATE PROCEDURE dbo.CalcSum @a INT, @b INT = 10, @result INT OUTPUT
            AS
            BEGIN
                DECLARE @sum INT = 0;
                SET @sum = @a + @b;
                SET @result = @sum * 2;
                RETURN 0;
            END
            """;
        // Breakpoint på rad 6 (SET @result) - alltså inuti kroppen, inte ett slutstopp
        var run = await DebugRun.StartAsync(Cs, proc, [6], mode: "module",
            parameters: new() { ["@a"] = "5", ["@b"] = "10", ["@result"] = null });

        var (line, reason) = await run.ExpectPausedAsync();
        Assert.Equal(6, line);
        Assert.Equal("breakpoint", reason);
        var atBreakpoint = await run.LocalsAsync();
        Assert.Equal("15", atBreakpoint["@sum"]);      // raden före har körts
        Assert.Null(atBreakpoint["@result"]);          // raden vi står på har inte

        await run.Runner.SignalAsync("continue");
        var (returnLine, _) = await run.ExpectPausedAsync(); // RETURN = tvingat slutstopp
        Assert.Equal(7, returnLine);
        var atReturn = await run.LocalsAsync();
        Assert.Equal("30", atReturn["@result"]);
        Assert.Equal("0", atReturn["@__dbg_return"]);

        await run.Runner.SignalAsync("continue");
        await run.ExpectAsync("terminated");
    }

    [SkippableFact]
    public async Task ModuleMode_SteppingThroughProcedureBody()
    {
        RequireSqlServer();
        var proc = """
            CREATE PROCEDURE dbo.CalcSum @a INT, @b INT = 10, @result INT OUTPUT
            AS
            BEGIN
                DECLARE @sum INT = 0;
                SET @sum = @a + @b;
                SET @result = @sum * 2;
                RETURN 0;
            END
            """;
        var run = await DebugRun.StartAsync(Cs, proc, [], stopOnEntry: true, mode: "module",
            parameters: new() { ["@a"] = "5", ["@b"] = "10", ["@result"] = null });

        Assert.Equal((4, "entry"), await run.ExpectPausedAsync()); // första statementet i kroppen
        await run.Runner.SignalAsync("stepOver");
        Assert.Equal((5, "step"), await run.ExpectPausedAsync());
        Assert.Equal("0", (await run.LocalsAsync())["@sum"]);
        await run.Runner.SignalAsync("stepOver");
        Assert.Equal((6, "step"), await run.ExpectPausedAsync());
        Assert.Equal("15", (await run.LocalsAsync())["@sum"]);
        await run.Runner.SignalAsync("stepOver");
        Assert.Equal((7, "step"), await run.ExpectPausedAsync());

        await run.Runner.SignalAsync("continue");
        await run.ExpectAsync("terminated");
    }

    [SkippableFact]
    public async Task ModuleMode_ConditionalBreakpointInsideLoopInProcedureBody()
    {
        RequireSqlServer();
        var proc = """
            CREATE PROCEDURE dbo.CountUp @n INT
            AS
            BEGIN
                DECLARE @i INT = 0;
                WHILE @i < @n
                BEGIN
                    SET @i = @i + 1;
                END
            END
            """;
        var run = await DebugRun.StartAsync(Cs, proc, [], mode: "module",
            parameters: new() { ["@n"] = "5" },
            breakpoints: [new DebugRun.Bp(7, Condition: "@i = 3")]);

        var (line, _) = await run.ExpectPausedAsync();
        Assert.Equal(7, line);
        Assert.Equal("3", (await run.LocalsAsync())["@i"]);

        await run.Runner.SignalAsync("continue");
        var (endLine, _) = await run.ExpectPausedAsync(); // slut på kroppen = tvingat slutstopp
        Assert.Equal(9, endLine);
        Assert.Equal("5", (await run.LocalsAsync())["@i"]);

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
