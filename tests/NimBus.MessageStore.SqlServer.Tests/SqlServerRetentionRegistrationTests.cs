#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.MessageStore.SqlServer;

namespace NimBus.MessageStore.SqlServer.Tests;

/// <summary>
/// Options binding, validation and registration of the SQL Server retention job (GH#291).
/// No database needed: every assertion stops before a connection is opened.
/// </summary>
[TestClass]
public sealed class SqlServerRetentionRegistrationTests
{
    private const string FakeConnection =
        "Server=localhost;Database=NimBus;User Id=sa;Password=not-a-real-password;TrustServerCertificate=True";

    [TestMethod]
    public void Defaults_are_never_enabled_and_one_hour()
    {
        var options = Resolve(new Dictionary<string, string?>());

        Assert.AreEqual(-1, options.UnresolvedRetentionDays);
        Assert.IsTrue(options.RetentionEnabled);
        Assert.AreEqual(TimeSpan.FromHours(1), options.RetentionInterval);
    }

    [TestMethod]
    public void Retention_settings_bind_from_the_NimBus_SqlServer_section()
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["NimBus:SqlServer:UnresolvedRetentionDays"] = "45",
            ["NimBus:SqlServer:RetentionEnabled"] = "false",
            ["NimBus:SqlServer:RetentionInterval"] = "00:15:00",
        });

        Assert.AreEqual(45, options.UnresolvedRetentionDays);
        Assert.IsFalse(options.RetentionEnabled);
        Assert.AreEqual(TimeSpan.FromMinutes(15), options.RetentionInterval);
    }

    [TestMethod]
    public void Configure_delegate_overrides_configuration()
    {
        var options = Resolve(
            new Dictionary<string, string?>
            {
                ["NimBus:SqlServer:UnresolvedRetentionDays"] = "45",
                ["NimBus:SqlServer:RetentionEnabled"] = "false",
                ["NimBus:SqlServer:RetentionInterval"] = "00:15:00",
            },
            o =>
            {
                o.UnresolvedRetentionDays = 10;
                o.RetentionEnabled = true;
                o.RetentionInterval = TimeSpan.FromMinutes(5);
            });

        Assert.AreEqual(10, options.UnresolvedRetentionDays);
        Assert.IsTrue(options.RetentionEnabled);
        Assert.AreEqual(TimeSpan.FromMinutes(5), options.RetentionInterval);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(1)]
    [DataRow(365)]
    public void Supported_UnresolvedRetentionDays_values_validate(int days)
    {
        var options = Resolve(new Dictionary<string, string?>(), o => o.UnresolvedRetentionDays = days);

        Assert.AreEqual(days, options.UnresolvedRetentionDays);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-2)]
    [DataRow(366)]
    public void Unsupported_UnresolvedRetentionDays_values_fail_validation_naming_option_and_value(int days)
    {
        var ex = Assert.ThrowsExactly<OptionsValidationException>(
            () => Resolve(new Dictionary<string, string?>(), o => o.UnresolvedRetentionDays = days));

        StringAssert.Contains(ex.Message, "SqlServerMessageStoreOptions.UnresolvedRetentionDays");
        StringAssert.Contains(ex.Message, $"was {days}.");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-60)]
    public void Non_positive_RetentionInterval_fails_validation(int seconds)
    {
        var ex = Assert.ThrowsExactly<OptionsValidationException>(
            () => Resolve(new Dictionary<string, string?>(), o => o.RetentionInterval = TimeSpan.FromSeconds(seconds)));

        StringAssert.Contains(ex.Message, "SqlServerMessageStoreOptions.RetentionInterval");
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-2")]
    [DataRow("366")]
    public async Task Invalid_UnresolvedRetentionDays_fails_host_startup(string days)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SqlConnection"] = FakeConnection,
            ["NimBus:SqlServer:UnresolvedRetentionDays"] = days,
        });
        new NimBusBuilder(builder.Services).AddSqlServerMessageStore();
        using var host = builder.Build();

        var ex = await Assert.ThrowsExactlyAsync<OptionsValidationException>(() => host.StartAsync());

        StringAssert.Contains(ex.Message, "SqlServerMessageStoreOptions.UnresolvedRetentionDays");
        StringAssert.Contains(ex.Message, $"was {days}.");
    }

    [TestMethod]
    public void AddSqlServerMessageStore_alone_registers_no_retention_job()
    {
        var services = NewServices(new Dictionary<string, string?>());
        new NimBusBuilder(services).AddSqlServerMessageStore(o => o.ConnectionString = FakeConnection);

        Assert.AreEqual(0, CountRetentionJobs(services));
    }

    [TestMethod]
    public void Retention_registration_called_twice_registers_one_job()
    {
        var services = NewServices(new Dictionary<string, string?>());
        var builder = new NimBusBuilder(services);
        builder.AddSqlServerMessageStore(o => o.ConnectionString = FakeConnection);
        builder.AddSqlServerMessageStoreRetention();
        builder.AddSqlServerMessageStoreRetention();

        Assert.AreEqual(1, CountRetentionJobs(services));
    }

    internal static int CountRetentionJobs(IServiceCollection services)
        => services.Count(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(SqlServerRetentionService));

    private static SqlServerMessageStoreOptions Resolve(
        Dictionary<string, string?> settings,
        Action<SqlServerMessageStoreOptions>? configure = null)
    {
        var services = NewServices(settings);
        new NimBusBuilder(services).AddSqlServerMessageStore(o =>
        {
            o.ConnectionString = FakeConnection;
            configure?.Invoke(o);
        });

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<SqlServerMessageStoreOptions>>().Value;
    }

    private static ServiceCollection NewServices(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services;
    }
}
