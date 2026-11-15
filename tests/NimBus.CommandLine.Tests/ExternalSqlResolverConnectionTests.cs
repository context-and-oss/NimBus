#pragma warning disable CA1707, CA2007

using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// GH#391: in external SQL mode the Resolver must receive the same <c>SqlConnection</c>
/// the WebApp receives, through secure deployment channels only.
/// </summary>
public sealed class ExternalSqlResolverConnectionTests
{
    private const string ConnectionStringMarker = "Server=tcp:external-marker.database.windows.net;Password=external-sql-marker";

    private static InfrastructureOptions Options(
        StorageProviderChoice storageProvider,
        SqlProvisioningMode sqlMode,
        string? sqlConnectionString = null,
        string? sqlAdminPassword = null) => new(
            "nimbus",
            "dev",
            "rg-nimbus-dev",
            ResourceNamePostFix: null,
            Location: null,
            WebAppVersion: "test",
            storageProvider,
            sqlMode,
            SqlConnectionString: sqlConnectionString,
            SqlAdminLogin: sqlAdminPassword is null ? null : "nimbusadmin",
            SqlAdminPassword: sqlAdminPassword,
            SqlServerName: null,
            ResolverPlan: null);

    [Fact]
    public async Task ApplyAsync_ExternalSqlMode_PassesConnectionStringToCoreDeploymentAsSecureParameter()
    {
        var azureCli = new RecordingAzureCliRunner();
        var deployer = new InfrastructureDeployer(new CommandContext(Path.GetTempPath()), azureCli);

        await deployer.ApplyAsync(
            Options(StorageProviderChoice.SqlServer, SqlProvisioningMode.External, sqlConnectionString: ConnectionStringMarker),
            CancellationToken.None);

        Assert.Equal(2, azureCli.Deployments.Count);
        var core = azureCli.Deployments[0];
        Assert.Equal(ConnectionStringMarker, core.SecureParameters["sqlConnectionString"]);
        Assert.Equal(ConnectionStringMarker, azureCli.Deployments[1].SecureParameters["sqlConnectionString"]);
        Assert.All(
            azureCli.Deployments,
            deployment => Assert.DoesNotContain(
                deployment.Arguments,
                argument => argument.Contains("external-sql-marker", StringComparison.Ordinal)));
        Assert.DoesNotContain(
            azureCli.Commands,
            command => command.Any(argument => argument.Contains("external-sql-marker", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ApplyAsync_ProvisionSqlMode_DoesNotPassSqlConnectionStringToCoreDeployment()
    {
        var azureCli = new RecordingAzureCliRunner();
        var deployer = new InfrastructureDeployer(new CommandContext(Path.GetTempPath()), azureCli);

        await deployer.ApplyAsync(
            Options(StorageProviderChoice.SqlServer, SqlProvisioningMode.Provision, sqlAdminPassword: "provision-password"),
            CancellationToken.None);

        AssertCoreHasNoSqlConnectionString(azureCli.Deployments[0]);
    }

    [Fact]
    public async Task ApplyAsync_Cosmos_DoesNotPassSqlConnectionStringToCoreDeployment()
    {
        var azureCli = new RecordingAzureCliRunner();
        var deployer = new InfrastructureDeployer(new CommandContext(Path.GetTempPath()), azureCli);

        await deployer.ApplyAsync(
            Options(StorageProviderChoice.Cosmos, SqlProvisioningMode.Provision),
            CancellationToken.None);

        AssertCoreHasNoSqlConnectionString(azureCli.Deployments[0]);
    }

    private static void AssertCoreHasNoSqlConnectionString(RecordedDeployment core)
    {
        Assert.False(core.SecureParameters.ContainsKey("sqlConnectionString"));
        Assert.DoesNotContain(core.Arguments, argument => argument.StartsWith("sqlConnectionString=", StringComparison.Ordinal));
    }
}
