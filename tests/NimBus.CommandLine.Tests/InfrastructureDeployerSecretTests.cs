#pragma warning disable CA1707, CA1861, CA2007

using System.Text.Json;
using Xunit;

namespace NimBus.CommandLine.Tests;

public sealed class InfrastructureDeployerSecretTests
{
    [Fact]
    public async Task ApplyAsync_KeepsEveryDeploymentSecretOutOfAzureCliArguments()
    {
        const string sqlPassword = "sql-password-marker";
        const string identityPassword = "identity-password-marker";
        var azureCli = new RecordingAzureCliRunner();
        var deployer = new InfrastructureDeployer(new CommandContext(Path.GetTempPath()), azureCli);
        var options = new InfrastructureOptions(
            "nimbus",
            "dev",
            "rg-nimbus-dev",
            ResourceNamePostFix: null,
            Location: null,
            WebAppVersion: "test",
            StorageProviderChoice.SqlServer,
            SqlProvisioningMode.Provision,
            SqlConnectionString: null,
            SqlAdminLogin: "nimbusadmin",
            SqlAdminPassword: sqlPassword,
            SqlServerName: null,
            ResolverPlan: null,
            IdentityAdminEmail: "admin@example.com",
            IdentityAdminPassword: identityPassword);

        await deployer.ApplyAsync(options, CancellationToken.None);

        Assert.Equal(2, azureCli.Deployments.Count);
        Assert.All(
            azureCli.Deployments,
            deployment =>
            {
                Assert.DoesNotContain(deployment.Arguments, argument => argument.Contains(sqlPassword, StringComparison.Ordinal));
                Assert.DoesNotContain(deployment.Arguments, argument => argument.Contains(identityPassword, StringComparison.Ordinal));
                Assert.DoesNotContain(deployment.Arguments, argument => argument.Contains(RecordingAzureCliRunner.InstrumentationKey, StringComparison.Ordinal));
            });

        Assert.Equal(sqlPassword, azureCli.Deployments[0].SecureParameters["sqlAdminPassword"]);
        Assert.Equal(RecordingAzureCliRunner.InstrumentationKey, azureCli.Deployments[1].SecureParameters["instrumentationKey"]);
        Assert.Contains(sqlPassword, azureCli.Deployments[1].SecureParameters["sqlConnectionString"], StringComparison.Ordinal);
        Assert.Equal(identityPassword, azureCli.Deployments[1].SecureParameters["identityAdminPassword"]);
    }

    /// <summary>
    /// Issue #391: an external connection string reached only the WebApp, so the Resolver
    /// started with no storage setting. Both deployments now get it, as a secure parameter.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_PassesTheExternalSqlConnectionStringToBothDeploymentsAsASecureParameter()
    {
        const string connectionString = "Server=tcp:external.example.net,1433;Initial Catalog=MessageDatabase;Password=external-sql-marker;";
        var azureCli = new RecordingAzureCliRunner();
        var deployer = new InfrastructureDeployer(new CommandContext(Path.GetTempPath()), azureCli);
        var options = new InfrastructureOptions(
            "nimbus",
            "dev",
            "rg-nimbus-dev",
            ResourceNamePostFix: null,
            Location: null,
            WebAppVersion: "test",
            StorageProviderChoice.SqlServer,
            SqlProvisioningMode.External,
            SqlConnectionString: connectionString);

        await deployer.ApplyAsync(options, CancellationToken.None);

        Assert.Equal(2, azureCli.Deployments.Count);
        Assert.All(
            azureCli.Deployments,
            deployment =>
            {
                Assert.DoesNotContain(deployment.Arguments, argument => argument.Contains("external-sql-marker", StringComparison.Ordinal));
                Assert.Equal(connectionString, deployment.SecureParameters["sqlConnectionString"]);
            });
        Assert.Contains("sqlMode=external", azureCli.Deployments[0].Arguments);
        Assert.False(azureCli.Deployments[0].SecureParameters.ContainsKey("sqlAdminPassword"));
    }

    /// <summary>
    /// The core template builds a provisioned server's connection string itself, and a Cosmos
    /// deployment has none, so neither passes one to the core deployment.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyAsync_PassesNoSqlConnectionStringToTheCoreDeploymentOutsideExternalMode(bool provisionedSql)
    {
        var azureCli = new RecordingAzureCliRunner();
        var deployer = new InfrastructureDeployer(new CommandContext(Path.GetTempPath()), azureCli);
        var options = new InfrastructureOptions(
            "nimbus",
            "dev",
            "rg-nimbus-dev",
            ResourceNamePostFix: null,
            Location: null,
            WebAppVersion: "test",
            provisionedSql ? StorageProviderChoice.SqlServer : StorageProviderChoice.Cosmos,
            SqlProvisioningMode.Provision,
            SqlConnectionString: null,
            SqlAdminLogin: "nimbusadmin",
            SqlAdminPassword: "sql-password-marker");

        await deployer.ApplyAsync(options, CancellationToken.None);

        Assert.False(azureCli.Deployments[0].SecureParameters.ContainsKey("sqlConnectionString"));
        Assert.DoesNotContain(azureCli.Deployments[0].Arguments, argument => argument.StartsWith("sqlConnectionString=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_LeavesApplicationInsightsApiKeysAlone()
    {
        // The Application Insights query API stopped accepting API keys on 2026-03-31. The
        // WebApp queries with its managed identity, so the CLI neither creates nor deletes keys
        // and no longer passes the deprecated apiKey template parameter.
        var azureCli = new RecordingAzureCliRunner();
        var deployer = new InfrastructureDeployer(new CommandContext(Path.GetTempPath()), azureCli);
        var options = new InfrastructureOptions(
            "nimbus",
            "dev",
            "rg-nimbus-dev",
            ResourceNamePostFix: null,
            Location: null,
            WebAppVersion: "test");

        await deployer.ApplyAsync(options, CancellationToken.None);

        Assert.DoesNotContain(azureCli.Commands, command => command.Contains("api-key", StringComparer.Ordinal));
        var webAppDeployment = azureCli.Deployments[1];
        Assert.False(webAppDeployment.SecureParameters.ContainsKey("apiKey"));
        Assert.DoesNotContain(webAppDeployment.Arguments, argument => argument.StartsWith("apiKey=", StringComparison.Ordinal));
    }
}
