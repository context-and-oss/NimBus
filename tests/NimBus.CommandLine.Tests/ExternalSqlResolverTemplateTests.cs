#pragma warning disable CA1707, CA2007

using System.Text.RegularExpressions;
using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// GH#391: pins the Resolver's <c>SqlConnection</c> in the shipped core template. CI has no
/// Bicep compiler, so these are text checks; compile with `az bicep build` when changing it.
/// </summary>
[Collection(ExtractedTemplatesCollection.Name)]
public sealed class ExternalSqlResolverTemplateTests
{
    private static readonly CommandContext Context = new(null);

    private static string Core => File.ReadAllText(Context.CoreBicepPath).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string SqlResolverSecretSettings
    {
        get
        {
            var match = Regex.Match(Core, @"var sqlResolverSecretSettings = (?<body>.*?)\n\n", RegexOptions.Singleline);
            Assert.True(match.Success, "deploy.core.bicep must declare sqlResolverSecretSettings.");
            return match.Groups["body"].Value;
        }
    }

    [Fact]
    public void Core_template_declares_a_secure_sqlConnectionString_parameter()
    {
        Assert.Contains("@secure()\nparam sqlConnectionString string = ''", Core, StringComparison.Ordinal);
    }

    [Fact]
    public void External_mode_assigns_the_parameter_to_the_resolver_SqlConnection()
    {
        var settings = SqlResolverSecretSettings;
        Assert.Contains("storageProvider == 'sqlserver' && sqlMode == 'external'", settings, StringComparison.Ordinal);
        Assert.Contains("SqlConnection: sqlConnectionString", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void Provision_mode_connection_string_is_unchanged()
    {
        var settings = SqlResolverSecretSettings;
        Assert.Contains("storageProvider == 'sqlserver' && sqlMode == 'provision'", settings, StringComparison.Ordinal);
        Assert.Contains(
            "SqlConnection: 'Server=tcp:${azureSql.outputs.serverFqdn},1433;Initial Catalog=${sqlDbName};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=true;'",
            settings,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Cosmos_gets_no_resolver_SqlConnection()
    {
        var settings = SqlResolverSecretSettings;
        // Every branch that yields SqlConnection is guarded by the SQL Server provider, and the
        // final fallback is the empty object.
        Assert.Equal(2, Regex.Matches(settings, @"storageProvider == 'sqlserver' && sqlMode == '(provision|external)' \?").Count);
        Assert.EndsWith(": {}", settings.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_SqlConnection_travels_only_in_the_secure_settings()
    {
        Assert.Contains("secretSettings: resolverSecretSettings", Core, StringComparison.Ordinal);
        Assert.Contains("sqlResolverSecretSettings,", Core, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"value:\s*sqlConnectionString", Core);
    }
}
