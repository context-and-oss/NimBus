#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// Deployed apps reach Cosmos with Entra data-plane RBAC, which cannot create containers, so
/// every container the store creates lazily must also be declared in
/// <c>deploy/bicep/templates/cosmosDB.bicep</c>. A container missing there works locally
/// (account keys) and fails in Azure with 403 / substatus 5300 on first use.
/// </summary>
[TestClass]
public sealed class CosmosBicepContainerSyncTests
{
    /// <summary>
    /// Reserved containers the platform template deliberately does not declare. The Cosmos
    /// inbox is an opt-in consumer feature with a configurable container id that requires
    /// Strong consistency (the template's account is Session), so a subscriber that enables
    /// it provisions its own container (docs/inbox-pattern.md).
    /// </summary>
    private static readonly string[] NotDeclaredByPlatformTemplate = ["inbox"];

    /// <summary>
    /// Partition key paths the store creates each container with (<c>CosmosDbClient</c>).
    /// A container provisioned with a different path rejects the store's writes.
    /// </summary>
    private static readonly Dictionary<string, string> ExpectedPartitionKeys = new(StringComparer.Ordinal)
    {
        ["subscriptions"] = "/id",
        ["messages"] = "/eventId",
        ["audits"] = "/eventId",
        ["eventschemas"] = "/id",
        ["eventreports"] = "/EndpointId",
        ["accesscontrol"] = "/id",
        ["Metadata"] = "/id",
        ["settings"] = "/id",
        ["servicehealth"] = "/id",
        ["heartbeatuptimedays"] = "/EndpointId",
        ["heartbeatgaps"] = "/EndpointId",
        ["endpointacknowledgements"] = "/id",
        ["operatoroperations"] = "/id",
    };

    [TestMethod]
    public void Bicep_declares_every_reserved_container_with_the_stores_partition_key()
    {
        var declared = DeclaredContainers();

        foreach (var id in CosmosContainerDefaults.ReservedContainerIds.Except(NotDeclaredByPlatformTemplate))
        {
            Assert.IsTrue(declared.TryGetValue(id, out var partitionKey),
                $"cosmosDB.bicep does not declare the '{id}' container; Entra RBAC deployments cannot create it at runtime.");
            Assert.IsTrue(ExpectedPartitionKeys.TryGetValue(id, out var expected),
                $"Add the partition key of reserved container '{id}' to {nameof(ExpectedPartitionKeys)}.");
            Assert.AreEqual(expected, partitionKey, $"Partition key of '{id}' in cosmosDB.bicep.");
        }
    }

    [TestMethod]
    public void Bicep_shared_containers_are_all_reserved_by_the_store()
    {
        var shared = SharedContainers();

        Assert.IsNotEmpty(shared, "Could not parse the sharedContainers array in cosmosDB.bicep.");
        foreach (var id in shared.Keys)
        {
            Assert.IsTrue(CosmosContainerDefaults.ReservedContainerIds.Contains(id),
                $"cosmosDB.bicep declares shared container '{id}', which is missing from ReservedContainerIds; "
                + "an endpoint with that id would share its physical container.");
        }
    }

    [TestMethod]
    public void Bicep_does_not_declare_the_documented_exceptions()
    {
        var declared = DeclaredContainers();

        foreach (var id in NotDeclaredByPlatformTemplate)
        {
            Assert.IsFalse(declared.ContainsKey(id),
                $"cosmosDB.bicep now declares '{id}'; remove it from {nameof(NotDeclaredByPlatformTemplate)}.");
        }
    }

    /// <summary>Entries of the <c>sharedContainers</c> array: <c>{ name: 'x', pk: '/y' ... }</c>.</summary>
    private static Dictionary<string, string> SharedContainers()
    {
        var bicep = ReadCosmosBicep();
        var block = Regex.Match(bicep, @"var sharedContainers = \[(?<body>.*?)^\]", RegexOptions.Singleline | RegexOptions.Multiline);
        Assert.IsTrue(block.Success, "cosmosDB.bicep has no sharedContainers array.");

        return Regex.Matches(block.Groups["body"].Value, @"\{\s*name:\s*'(?<name>[^']+)',\s*pk:\s*'(?<pk>[^']+)'")
            .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["pk"].Value, StringComparer.Ordinal);
    }

    /// <summary>Shared-array entries plus dedicated container resources (<c>id: 'x'</c> + <c>paths: ['/y']</c>).</summary>
    private static Dictionary<string, string> DeclaredContainers()
    {
        var declared = SharedContainers();
        foreach (Match m in Regex.Matches(ReadCosmosBicep(), @"id:\s*'(?<name>[^']+)'\s*partitionKey:\s*\{\s*paths:\s*\['(?<pk>[^']+)'\]"))
        {
            declared.Add(m.Groups["name"].Value, m.Groups["pk"].Value);
        }

        return declared;
    }

    private static string ReadCosmosBicep()
    {
        var relative = Path.Combine("deploy", "bicep", "templates", "cosmosDB.bicep");
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate).Replace("\r\n", "\n", StringComparison.Ordinal);
            }
        }

        throw new FileNotFoundException($"Could not locate {relative} above {AppContext.BaseDirectory}.");
    }
}
