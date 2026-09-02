using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace SqlDebugger.Sidecar.Tests;

/// <summary>Skapar testdatabasen. ConnectionString är null när
/// SQLDBGR_TEST_CONNECTION saknas - integrationstesterna hoppas då över.</summary>
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

    /// <summary>SQL Server i en service-container kan behöva en stund efter "healthy".</summary>
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
