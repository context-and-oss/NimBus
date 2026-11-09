#pragma warning disable CA1707, CA2007
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using Xunit;

namespace NimBus.CommandLine.Tests;

public sealed class EndpointContainerProvisionerTests
{
    [Fact]
    public async Task ApplyAsync_CreatesMissingEndpointContainersWithEndpointDefaults()
    {
        var az = new FakeAzureCliRunner { ListResponse = "[\"messages\", \"BillingEndpoint\"]" };
        var sut = new EndpointContainerProvisioner(az, () => new TestPlatform(
            new TestEndpoint("BillingEndpoint"),
            new TestEndpoint("StorefrontEndpoint")));

        await sut.ApplyAsync(new TopologyOptions("nbdemo", "dev", "rg-test"), CancellationToken.None);

        var create = Assert.Single(az.Commands, command => command.Contains("create"));
        Assert.Equal(
            new[]
            {
                "cosmosdb", "sql", "container", "create",
                "--resource-group", "rg-test",
                "--account-name", "cosmos-nbdemo-dev",
                "--database-name", "MessageDatabase",
                "--name", "StorefrontEndpoint",
                "--partition-key-path", "/id",
                "--ttl", "-1",
                "--output", "none",
            },
            create);
    }

    [Fact]
    public async Task ApplyAsync_LeavesExistingContainersUntouched()
    {
        var az = new FakeAzureCliRunner { ListResponse = "[\"BillingEndpoint\", \"StorefrontEndpoint\"]" };
        var sut = new EndpointContainerProvisioner(az, () => new TestPlatform(
            new TestEndpoint("BillingEndpoint"),
            new TestEndpoint("StorefrontEndpoint")));

        await sut.ApplyAsync(new TopologyOptions("nbdemo", "dev", "rg-test"), CancellationToken.None);

        Assert.DoesNotContain(az.Commands, command => command.Contains("create"));
    }

    [Fact]
    public async Task ApplyAsync_ContainerIdComparisonIsCaseSensitive()
    {
        // Cosmos container ids are case-sensitive: an existing 'billingendpoint'
        // must not satisfy the endpoint id 'BillingEndpoint'.
        var az = new FakeAzureCliRunner { ListResponse = "[\"billingendpoint\"]" };
        var sut = new EndpointContainerProvisioner(az, () => new TestPlatform(
            new TestEndpoint("BillingEndpoint")));

        await sut.ApplyAsync(new TopologyOptions("nbdemo", "dev", "rg-test"), CancellationToken.None);

        Assert.Single(az.Commands, command => command.Contains("create") && command.Contains("BillingEndpoint"));
    }

    [Fact]
    public async Task ApplyAsync_ThrowsForReservedEndpointIdBeforeTouchingAzure()
    {
        var az = new FakeAzureCliRunner();
        var sut = new EndpointContainerProvisioner(az, () => new TestPlatform(
            new TestEndpoint("messages")));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.ApplyAsync(new TopologyOptions("nbdemo", "dev", "rg-test"), CancellationToken.None));

        Assert.Empty(az.Commands);
    }

    private sealed class FakeAzureCliRunner : IAzureCliRunner
    {

        public Task<System.Text.Json.JsonDocument> CaptureJsonAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string failureMessage) =>
            Task.FromResult(System.Text.Json.JsonDocument.Parse("{}"));
        public List<IReadOnlyList<string>> Commands { get; } = new();

        public string ListResponse { get; set; } = "[]";

        public Task EnsureLoggedInAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureExtensionAsync(string extensionName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureSuccessAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string failureMessage)
        {
            Commands.Add(arguments);
            return Task.CompletedTask;
        }

        public Task EnsureSuccessAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken, string failureMessage)
        {
            Commands.Add(arguments);
            return Task.CompletedTask;
        }

        public Task<string> CaptureValueAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string failureMessage)
        {
            Commands.Add(arguments);
            return Task.FromResult(ListResponse);
        }

        public Task<ProcessResult> TryRunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Commands.Add(arguments);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class TestPlatform : Platform
    {
        public TestPlatform(params IEndpoint[] endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                AddEndpoint(endpoint);
            }
        }
    }

    private sealed class TestEndpoint : IEndpoint
    {
        public TestEndpoint(string id)
        {
            Id = id;
            Name = id;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description => string.Empty;
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => Array.Empty<IEventType>();
        public IEnumerable<IEventType> EventTypesConsumed => Array.Empty<IEventType>();
        public IEnumerable<IRoleAssignment> RoleAssignments => Array.Empty<IRoleAssignment>();
    }
}
