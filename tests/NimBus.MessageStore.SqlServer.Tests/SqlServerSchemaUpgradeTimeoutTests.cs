#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbUp.Engine;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.MessageStore.SqlServer;

namespace NimBus.MessageStore.SqlServer.Tests;

/// <summary>
/// A schema upgrade runs its migration scripts under <see cref="SqlServerMessageStoreOptions.SchemaUpgradeTimeoutSeconds"/>,
/// not SqlClient's 30-second command default: an index build on a large Messages table (0030 on
/// Azure SQL Basic) ran past 30 seconds and crash-looped the host. A second instance that starts
/// during a long upgrade waits for it instead of giving up after a minute.
/// </summary>
[TestClass]
public sealed class SqlServerSchemaUpgradeTimeoutTests
{
    // --- Options ------------------------------------------------------------------------------

    [TestMethod]
    public void The_default_allows_a_long_index_build()
    {
        Assert.AreEqual(1800, new SqlServerMessageStoreOptions().SchemaUpgradeTimeoutSeconds);
    }

    [TestMethod]
    public void The_timeout_binds_from_NimBus_SqlServer()
    {
        var options = Resolve(new Dictionary<string, string?> { ["NimBus:SqlServer:SchemaUpgradeTimeoutSeconds"] = "3600" });

        Assert.AreEqual(3600, options.Value.SchemaUpgradeTimeoutSeconds);
    }

    [TestMethod]
    public void The_registration_delegate_overrides_the_bound_timeout()
    {
        var options = Resolve(
            new Dictionary<string, string?> { ["NimBus:SqlServer:SchemaUpgradeTimeoutSeconds"] = "3600" },
            o => o.SchemaUpgradeTimeoutSeconds = 0);

        Assert.AreEqual(0, options.Value.SchemaUpgradeTimeoutSeconds);
    }

    [TestMethod]
    public void A_negative_timeout_is_refused()
    {
        var options = Resolve(new Dictionary<string, string?> { ["NimBus:SqlServer:SchemaUpgradeTimeoutSeconds"] = "-1" });

        var error = Assert.ThrowsExactly<OptionsValidationException>(() => options.Value);
        StringAssert.Contains(error.Message, "SchemaUpgradeTimeoutSeconds", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(1800, 1800)]
    [DataRow(30, 60)]
    [DataRow(1, 60)]
    [DataRow(0, -1)]
    public void A_second_instance_waits_for_the_lock_as_long_as_one_script_may_run(int timeoutSeconds, int expectedLockWaitSeconds)
    {
        // At least the minute it always waited; -1 waits without limit, as a script may run without one.
        var wait = SqlServerSchemaInitializer.LockWaitMilliseconds(new SqlServerMessageStoreOptions { SchemaUpgradeTimeoutSeconds = timeoutSeconds });

        Assert.AreEqual(expectedLockWaitSeconds < 0 ? -1 : expectedLockWaitSeconds * 1000, wait);
    }

    // --- Live SQL Server ----------------------------------------------------------------------

    [TestMethod]
    public async Task A_script_longer_than_SqlClients_default_runs_under_the_configured_timeout()
    {
        // The regression: 35 s is past SqlClient's 30 s default, inside the configured 60 s.
        var initializer = Initializer(NewSchemaName(), timeoutSeconds: 60, Wait("00:00:35"));

        await initializer.StartAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task A_script_longer_than_the_configured_timeout_fails_the_upgrade()
    {
        var initializer = Initializer(NewSchemaName(), timeoutSeconds: 1, Wait("00:00:05"));

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initializer.StartAsync(CancellationToken.None));

        StringAssert.Contains(error.Message, "DbUp failed", StringComparison.Ordinal);
        Assert.IsInstanceOfType<SqlException>(error.InnerException);
    }

    [TestMethod]
    public async Task No_limit_lets_a_script_run_as_long_as_it_takes()
    {
        var initializer = Initializer(NewSchemaName(), timeoutSeconds: 0, Wait("00:00:35"));

        await initializer.StartAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task A_second_instance_waits_while_another_holds_the_upgrade_lock_and_says_so()
    {
        var schema = NewSchemaName();
        var logs = new ListLogger();
        await using var holder = await HoldUpgradeLockAsync(schema);

        var clock = Stopwatch.StartNew();
        var start = Initializer(schema, timeoutSeconds: 120, logger: logs).StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.IsFalse(start.IsCompleted, "The second instance must wait while the lock is held.");
        await ReleaseAsync(holder, schema);
        await start;

        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(3), clock.Elapsed);
        Assert.IsTrue(logs.Messages.Any(m => m.Contains("waiting", StringComparison.OrdinalIgnoreCase) && m.Contains(schema, StringComparison.Ordinal)),
            string.Join(Environment.NewLine, logs.Messages));
    }

    private static SqlScript Wait(string delay) => new("9999_Wait.sql", $"WAITFOR DELAY '{delay}';");

    private static SqlServerSchemaInitializer Initializer(string schema, int timeoutSeconds, SqlScript? extraScript = null, ILogger<SqlServerSchemaInitializer>? logger = null)
        => new(
            Options.Create(new SqlServerMessageStoreOptions
            {
                ConnectionString = SqlServerStoreTestHarness.GetConnectionString(),
                Schema = schema,
                ProvisioningMode = SchemaProvisioningMode.AutoApply,
                SchemaUpgradeTimeoutSeconds = timeoutSeconds,
            }),
            logger ?? NullLogger<SqlServerSchemaInitializer>.Instance,
            readiness: null,
            additionalScripts: extraScript is null ? [] : [extraScript]);

    private static async Task<SqlConnection> HoldUpgradeLockAsync(string schema)
    {
        var builder = new SqlConnectionStringBuilder(SqlServerStoreTestHarness.GetConnectionString()) { Pooling = false };
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC sp_getapplock @Resource = @res, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0";
        command.Parameters.AddWithValue("@res", $"NimBus.Schema.{schema}");
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task ReleaseAsync(SqlConnection holder, string schema)
    {
        await using var command = holder.CreateCommand();
        command.CommandText = "EXEC sp_releaseapplock @Resource = @res, @LockOwner = N'Session'";
        command.Parameters.AddWithValue("@res", $"NimBus.Schema.{schema}");
        await command.ExecuteNonQueryAsync();
    }

    private static IOptions<SqlServerMessageStoreOptions> Resolve(Dictionary<string, string?> settings, Action<SqlServerMessageStoreOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        new NimBusBuilder(services).AddSqlServerMessageStore(options =>
        {
            options.ConnectionString = "Server=localhost;Database=NimBus;Integrated Security=true";
            configure?.Invoke(options);
        });
        return services.BuildServiceProvider().GetRequiredService<IOptions<SqlServerMessageStoreOptions>>();
    }

    private static string NewSchemaName() => $"nimbus_test_{Guid.NewGuid():N}"[..24];

    private sealed class ListLogger : ILogger<SqlServerSchemaInitializer>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                    return [.. _messages];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
                _messages.Add(formatter(state, exception));
        }
    }
}
