#pragma warning disable CA1707, CA2007
using Xunit;

namespace NimBus.CommandLine.Tests;

public class CommandRunnerClientFactoryTests
{
    private const string SbNamespace = "nimbus-test.servicebus.windows.net";
    private const string SbConnectionString =
        "Endpoint=sb://nimbus-test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=Zm9vYmFyZm9vYmFy";

    private const string CosmosEndpoint = "https://nimbus-test.documents.azure.com/";
    private const string CosmosConnectionString =
        "AccountEndpoint=https://nimbus-test.documents.azure.com/;AccountKey=Zm9vYmFyZm9vYmFyZm9vYmFyZm9vYmFyZm9vYmFyZm9vYmFyZm9vYmFyZm9vYmFy;";

    [Fact]
    public async Task CreateServiceBusClient_WithConnectionString_UsesConnectionString()
    {
        await using var client = CommandRunner.CreateServiceBusClient(SbConnectionString);
        Assert.Equal(SbNamespace, client.FullyQualifiedNamespace);
    }

    [Fact]
    public async Task CreateServiceBusClient_WithFullyQualifiedNamespace_UsesEntraIdCredential()
    {
        await using var client = CommandRunner.CreateServiceBusClient(SbNamespace);
        Assert.Equal(SbNamespace, client.FullyQualifiedNamespace);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateServiceBusClient_WithoutValue_ThrowsWithGuidance(string? value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CommandRunner.CreateServiceBusClient(value));
        Assert.Contains(CommandRunner.SbConnectionStringEnvName, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateServiceBusAdministrationClient_WithConnectionString_Succeeds()
    {
        var client = CommandRunner.CreateServiceBusAdministrationClient(SbConnectionString);
        Assert.NotNull(client);
    }

    [Fact]
    public void CreateServiceBusAdministrationClient_WithFullyQualifiedNamespace_Succeeds()
    {
        var client = CommandRunner.CreateServiceBusAdministrationClient(SbNamespace);
        Assert.NotNull(client);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CreateServiceBusAdministrationClient_WithoutValue_ThrowsWithGuidance(string? value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CommandRunner.CreateServiceBusAdministrationClient(value));
        Assert.Contains(CommandRunner.SbConnectionStringEnvName, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateCosmosClient_WithConnectionString_UsesConnectionString()
    {
        using var client = CommandRunner.CreateCosmosClient(CosmosConnectionString);
        Assert.Equal(new Uri(CosmosEndpoint), client.Endpoint);
    }

    [Fact]
    public void CreateCosmosClient_WithAccountEndpoint_UsesEntraIdCredential()
    {
        using var client = CommandRunner.CreateCosmosClient(CosmosEndpoint);
        Assert.Equal(new Uri(CosmosEndpoint), client.Endpoint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CreateCosmosClient_WithoutValue_ThrowsWithGuidance(string? value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CommandRunner.CreateCosmosClient(value));
        Assert.Contains(CommandRunner.DbConnectionStringEnvName, ex.Message, StringComparison.Ordinal);
    }
}
