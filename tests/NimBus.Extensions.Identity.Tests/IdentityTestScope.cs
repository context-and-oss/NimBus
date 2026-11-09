#pragma warning disable CA1707, CA2007
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Extensions.Identity;

namespace NimBus.Extensions.Identity.Tests;

// Per-test isolation: each scope spins up a unique schema in the shared
// NIMBUS_SQL_TEST_CONNECTION database so test classes don't collide.
// The schema is dropped on DisposeAsync; CI containers are throwaway so
// drop failures are tolerated.
internal sealed class IdentityTestScope : IAsyncDisposable
{
    public ServiceProvider Services { get; }
    public string Schema { get; }
    private readonly string _connectionString;

    private IdentityTestScope(ServiceProvider services, string schema, string connectionString)
    {
        Services = services;
        Schema = schema;
        _connectionString = connectionString;
    }

    public static IdentityTestScope Create(Action<NimBusIdentityOptions>? configure = null)
    {
        var connectionString = Environment.GetEnvironmentVariable("NIMBUS_SQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("NIMBUS_SQL_TEST_CONNECTION not set; skipping Identity SQL Server integration tests.");
        }

        var schema = string.Concat("id_", Guid.NewGuid().ToString("N").AsSpan(0, 16));
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        // NimBus Identity wires AddIdentity which depends on auth infra;
        // a stub scheme keeps SignInManager.SignOutAsync happy in tests.
        services.AddAuthentication().AddCookie();
        services.AddNimBusIdentity(opts =>
        {
            opts.ConnectionString = connectionString!;
            opts.Schema = schema;
            opts.RequireEmailConfirmation = false;
            configure?.Invoke(opts);
        });

        var sp = services.BuildServiceProvider();
        return new IdentityTestScope(sp, schema, connectionString!);
    }

    public async ValueTask DisposeAsync()
    {
        // Best-effort schema cleanup. Drops every table the schema owns
        // before dropping the schema itself. Swallow errors — CI tear-down
        // wipes the container anyway.
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            var dropSql = $@"
                DECLARE @sql NVARCHAR(MAX) = N'';
                SELECT @sql = @sql + N'DROP TABLE [{Schema}].' + QUOTENAME(t.name) + N';'
                FROM sys.tables t
                INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
                WHERE s.name = N'{Schema}';
                EXEC sp_executesql @sql;
                IF EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'{Schema}')
                    EXEC('DROP SCHEMA [{Schema}]');";
            await using var cmd = new SqlCommand(dropSql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // intentionally ignored
        }

        await Services.DisposeAsync();
    }
}
