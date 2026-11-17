#pragma warning disable CA1707, CA2007
using Microsoft.Azure.Cosmos;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Services.IntegrationIntelligence;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class IntelligenceSettingsStoreTests
{
    [TestMethod]
    [DataRow("sql")]
    [DataRow("cosmos")]
    public async Task Shared_Stores_Fence_First_Save_And_Updates_And_Survive_New_Clients(string backend)
    {
        var databaseName = "IntelligenceAdminTest_" + Guid.NewGuid().ToString("N");
        CosmosClient? cosmos = null;
        CosmosClient? secondCosmos = null;
        string? sql = null;
        IIntelligenceSettingsStore first;
        IIntelligenceSettingsStore second;
        if (backend == "sql")
        {
            sql = Environment.GetEnvironmentVariable("NIMBUS_SQL_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(sql)) Assert.Inconclusive("SQL test connection not configured.");
            await SqlCommandAsync(sql, $"CREATE DATABASE [{databaseName}]");
            var connection = new SqlConnectionStringBuilder(sql) { InitialCatalog = databaseName }.ConnectionString;
            first = new SqlIntelligenceSettingsStore(connection);
            second = new SqlIntelligenceSettingsStore(connection);
        }
        else
        {
            var connection = Environment.GetEnvironmentVariable("NIMBUS_COSMOS_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(connection))
            {
                if (Environment.GetEnvironmentVariable("NIMBUS_COSMOS_TEST_REQUIRED") == "1") Assert.Fail("Cosmos test connection required.");
                Assert.Inconclusive("Cosmos test connection not configured.");
            }
            cosmos = new CosmosClient(connection, new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway, LimitToEndpoint = true });
            var database = await cosmos.CreateDatabaseAsync(databaseName);
            await database.Database.CreateContainerAsync("intelligencesettings", "/id");
            first = new CosmosIntelligenceSettingsStore(cosmos, databaseName);
            secondCosmos = new CosmosClient(connection, new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway, LimitToEndpoint = true });
            second = new CosmosIntelligenceSettingsStore(secondCosmos, databaseName);
        }
        try
        {
            Assert.IsNull(await first.ReadAsync(CancellationToken.None));
            var one = new IntelligenceSettingsDocument(new() { Enabled = true, IncludeEventPayload = true }, Guid.NewGuid().ToString("D"));
            var two = new IntelligenceSettingsDocument(new() { Enabled = false }, Guid.NewGuid().ToString("D"));
            var race = await Task.WhenAll(first.TrySaveAsync(one, "none", CancellationToken.None), second.TrySaveAsync(two, "none", CancellationToken.None));
            Assert.AreEqual(1, race.Count(won => won));
            var current = (await second.ReadAsync(CancellationToken.None))!;
            Assert.AreEqual(race[0] ? one.Revision : two.Revision, current.Revision);
            Assert.AreEqual(race[0], current.Settings.IncludeEventPayload, "The settings round-trip with their revision.");
            var update = new IntelligenceSettingsDocument(new() { IncludeEventPayload = false }, Guid.NewGuid().ToString("D"));
            race = await Task.WhenAll(first.TrySaveAsync(update, current.Revision, CancellationToken.None), second.TrySaveAsync(one with { Revision = Guid.NewGuid().ToString("D") }, current.Revision, CancellationToken.None));
            Assert.AreEqual(1, race.Count(won => won));
            Assert.IsFalse(await first.TrySaveAsync(two, current.Revision, CancellationToken.None));
            Assert.IsFalse(await first.TrySaveAsync(two, "none", CancellationToken.None));
            Assert.AreNotEqual(current.Revision, (await second.ReadAsync(CancellationToken.None))!.Revision);
            var restarted = await IntelligenceSettingsBootstrap.LoadAsync(new ConfigurationBuilder().Build(), second, CancellationToken.None);
            var persisted = (await first.ReadAsync(CancellationToken.None))!;
            Assert.AreEqual(persisted.Revision, restarted[IntelligenceSettingsBootstrap.RevisionKey]);
            Assert.AreEqual(persisted.Settings.IncludeEventPayload, IntelligenceAdminSettings.FromConfiguration(restarted).IncludeEventPayload);
            if (sql is not null)
            {
                var deployment = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["NimBus:StorageProvider"] = "sqlserver",
                    ["SqlConnection"] = new SqlConnectionStringBuilder(sql) { InitialCatalog = databaseName }.ConnectionString,
                }).Build();
                var bootstrapped = IntelligenceSettingsBootstrap.Load(deployment);
                Assert.AreEqual(persisted.Revision, bootstrapped[IntelligenceSettingsBootstrap.RevisionKey], "The stock bootstrap must resolve the same SQL database.");
                await SqlCommandAsync(deployment["SqlConnection"]!, "UPDATE dbo.IntelligenceAdminSettings SET SettingsJson='null' WHERE Id=1");
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => first.ReadAsync(CancellationToken.None));
                deployment["NimBus:IntegrationIntelligence:Enabled"] = "true";
                bootstrapped = IntelligenceSettingsBootstrap.Load(deployment);
                Assert.IsFalse(bootstrapped.GetValue<bool>("NimBus:IntegrationIntelligence:Enabled"));
                Assert.IsTrue(bootstrapped.GetValue<bool>(IntelligenceSettingsBootstrap.FailureKey));
            }
            else
            {
                await cosmos!.GetContainer(databaseName, "intelligencesettings").ReplaceItemAsync(
                    new Dictionary<string, object?> { ["id"] = "failure-classification", ["Document"] = null },
                    "failure-classification", new PartitionKey("failure-classification"));
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => first.ReadAsync(CancellationToken.None));
            }
        }
        finally
        {
            secondCosmos?.Dispose();
            if (cosmos is not null) { await cosmos.GetDatabase(databaseName).DeleteAsync(); cosmos.Dispose(); }
            if (sql is not null)
            {
                SqlConnection.ClearAllPools();
                await SqlCommandAsync(sql, $"DROP DATABASE [{databaseName}]");
            }
        }
    }

    private static async Task SqlCommandAsync(string connectionString, string text)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }
}
