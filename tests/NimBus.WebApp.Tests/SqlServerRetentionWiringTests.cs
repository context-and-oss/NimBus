#pragma warning disable CA1707, CA2007
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.WebApp.Tests;

/// <summary>
/// GH#291: the WebApp is the one host that runs SQL Server retention, and only on its SQL
/// Server storage path.
/// </summary>
[TestClass]
public sealed class SqlServerRetentionWiringTests
{
    private const string RetentionServiceName = "SqlServerRetentionService";

    [TestMethod]
    public void Sql_server_storage_registers_exactly_one_retention_job()
    {
        var services = Register(new Dictionary<string, string?>
        {
            ["NimBus:StorageProvider"] = "sqlserver",
            ["SqlConnection"] = "Server=localhost;Database=NimBus;User Id=sa;Password=not-a-real-password;TrustServerCertificate=True",
        });

        Assert.AreEqual(1, CountRetentionJobs(services));
    }

    [TestMethod]
    public void Cosmos_storage_registers_no_retention_job()
    {
        var services = Register(new Dictionary<string, string?>
        {
            ["NimBus:StorageProvider"] = "cosmos",
            ["CosmosConnection"] = "AccountEndpoint=https://localhost:8081/;AccountKey=a2V5",
        });

        Assert.AreEqual(0, CountRetentionJobs(services));
    }

    private static ServiceCollection Register(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        Startup.AddStorage(services, configuration);
        return services;
    }

    private static int CountRetentionJobs(IServiceCollection services)
        => services.Count(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType?.Name == RetentionServiceName);
}
