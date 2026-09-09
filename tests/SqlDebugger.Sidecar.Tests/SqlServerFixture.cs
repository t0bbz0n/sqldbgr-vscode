using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace SqlDebugger.Sidecar.Tests;

/// <summary>Shared by every class that touches the server. With an
/// IClassFixture each, the classes run in parallel against the SAME database and
/// collide on the objects the tests create themselves ("There is already an
/// object named ..."), on top of applying the schema at the same time.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sqlserver";
}

/// <summary>Creates the test database. ConnectionString is null when
/// SQLDBGR_TEST_CONNECTION is not set, and the integration tests then skip
/// themselves.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string DatabaseName = "sqldbgr_test";
    public string? ConnectionString { get; private set; }

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("SQLDBGR_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(configured)) return;

        var master = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
        await using (var conn = await OpenWithRetryAsync(master.ConnectionString))
        {
            await conn.ExecuteAsync($"IF DB_ID('{DatabaseName}') IS NULL CREATE DATABASE {DatabaseName}");
        }

        var test = new SqlConnectionStringBuilder(configured) { InitialCatalog = DatabaseName };
        ConnectionString = test.ConnectionString;
        await using (var conn = new SqlConnection(ConnectionString))
        {
            await conn.ExecuteAsync("""
                IF OBJECT_ID('dbo.AbortProbe') IS NULL CREATE TABLE dbo.AbortProbe (Id INT NOT NULL);
                TRUNCATE TABLE dbo.AbortProbe;
                """);
        }
    }

    /// <summary>SQL Server can need a moment after it reports itself healthy.</summary>
    private static async Task<SqlConnection> OpenWithRetryAsync(string connectionString)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (true)
        {
            try
            {
                var conn = new SqlConnection(connectionString);
                await conn.OpenAsync();
                return conn;
            }
            catch (SqlException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(3000);
            }
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
