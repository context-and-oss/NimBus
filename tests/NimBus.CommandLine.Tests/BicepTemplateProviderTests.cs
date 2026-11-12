#pragma warning disable CA1707, CA2007
using System.Text.RegularExpressions;
using NimBus.CommandLine;
using Xunit;

namespace NimBus.CommandLine.Tests;

[Collection(ExtractedTemplatesCollection.Name)]
public class BicepTemplateProviderTests
{
    private static readonly Regex ModuleReference = new(
        @"^\s*module\s+\w+\s+'(?<path>[^']+)'",
        RegexOptions.Multiline | RegexOptions.Compiled);

    [Fact]
    public void AssetsRoot_ExtractsBothEntryTemplates()
    {
        var context = new CommandContext(null);

        Assert.True(File.Exists(context.CoreBicepPath), $"missing {context.CoreBicepPath}");
        Assert.True(File.Exists(context.WebAppBicepPath), $"missing {context.WebAppBicepPath}");
    }

    /// <summary>
    /// The reason the extraction preserves directory structure: both entry templates
    /// reference their modules as 'templates/*.bicep' relative to themselves, so a
    /// flattened extraction would deploy nothing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryModuleReference_ResolvesRelativeToItsTemplate(bool webApp)
    {
        var context = new CommandContext(null);
        var entryTemplate = webApp ? context.WebAppBicepPath : context.CoreBicepPath;

        var references = ModuleReference.Matches(File.ReadAllText(entryTemplate))
            .Select(match => match.Groups["path"].Value)
            .Where(path => !path.StartsWith("br/", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(references);

        var templateDirectory = Path.GetDirectoryName(entryTemplate)!;
        foreach (var reference in references)
        {
            var resolved = Path.GetFullPath(
                Path.Combine(templateDirectory, reference.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(File.Exists(resolved), $"'{reference}' referenced by {Path.GetFileName(entryTemplate)} did not resolve to {resolved}");
        }
    }

    [Fact]
    public void Extraction_IsIdempotentAndRepairsTruncatedFiles()
    {
        var context = new CommandContext(null);
        var target = context.CoreBicepPath;
        var expected = File.ReadAllText(target);

        // Simulate a run interrupted mid-write: without an unconditional overwrite the
        // truncated file would persist for the lifetime of the version-keyed directory.
        File.WriteAllText(target, string.Empty);

        var restored = File.ReadAllText(ReExtract().CoreBicepPath);

        Assert.Equal(expected, restored);
    }

    /// <summary>
    /// Extraction is cached per process, so re-running it means invoking the private
    /// extraction path again through a fresh context after the cache has been populated.
    /// </summary>
    private static CommandContext ReExtract()
    {
        typeof(BicepTemplateProvider)
            .GetMethod("Extract", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, null);
        return new CommandContext(null);
    }

    [Fact]
    public void ResolveVersion_StripsSourceRevisionSuffix()
    {
        var version = BicepTemplateProvider.ResolveVersion();

        Assert.DoesNotContain('+', version);
        Assert.False(string.IsNullOrWhiteSpace(version));
    }

    [Fact]
    public void WebApp_identity_retains_Service_Bus_Data_Owner()
    {
        var context = new CommandContext(null);
        var roleAssignments = Path.Combine(
            Path.GetDirectoryName(context.WebAppBicepPath)!,
            "templates",
            "roleAssignments.bicep");
        var template = File.ReadAllText(roleAssignments);

        Assert.Contains("090c5cfd-751d-490a-894a-3ce6f1109419", template, StringComparison.Ordinal);
        Assert.Contains("serviceBusDataOwnerRoleId", template, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Application Insights query API stopped accepting API keys on 2026-03-31. The WebApp
    /// queries with its managed identity, which needs Reader on the component (the role
    /// Microsoft documents for Entra-authenticated queries). The Resolver never queries. The
    /// deprecated apiKey parameter must stay optional, because the CLI no longer passes it.
    /// </summary>
    [Fact]
    public void WebApp_identity_queries_Application_Insights_with_Reader_instead_of_an_api_key()
    {
        var context = new CommandContext(null);
        var webApp = File.ReadAllText(context.WebAppBicepPath);
        var core = File.ReadAllText(context.CoreBicepPath);
        var roleAssignments = File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(context.WebAppBicepPath)!,
            "templates",
            "roleAssignments.bicep"));

        Assert.Contains("var readerRoleId = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'", roleAssignments, StringComparison.Ordinal);
        Assert.Contains("scope: appInsightsComponent", roleAssignments, StringComparison.Ordinal);
        Assert.Contains("grantAppInsightsQueryAccess: ", webApp, StringComparison.Ordinal);
        Assert.DoesNotContain("grantAppInsightsQueryAccess", core, StringComparison.Ordinal);
        Assert.DoesNotContain("AppInsights:ApiKey", webApp, StringComparison.Ordinal);
        Assert.Contains("param apiKey string = ''", webApp, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Resolver's app settings are template-owned and replaced on every deploy, so the one
    /// tunable consumer bound (session concurrency) is expressed as a host override in the core
    /// template. The fixed bounds (prefetch, session idle timeout, dynamic concurrency) ship in
    /// host.json with the binary and are pinned there; each value has exactly one owner.
    /// </summary>
    [Fact]
    public void Core_template_owns_only_the_session_concurrency_override()
    {
        var context = new CommandContext(null);
        var template = File.ReadAllText(context.CoreBicepPath);

        Assert.Contains("AzureFunctionsJobHost__extensions__serviceBus__maxConcurrentSessions", template, StringComparison.Ordinal);
        Assert.Contains("param resolverMaxConcurrentSessions int = 16", template, StringComparison.Ordinal);
        Assert.DoesNotContain("AzureFunctionsJobHost__extensions__serviceBus__prefetchCount", template, StringComparison.Ordinal);
        Assert.DoesNotContain("AzureFunctionsJobHost__extensions__serviceBus__sessionIdleTimeout", template, StringComparison.Ordinal);
        Assert.DoesNotContain("AzureFunctionsJobHost__concurrency__dynamicConcurrencyEnabled", template, StringComparison.Ordinal);
    }

    [Fact]
    public void Function_app_templates_expose_their_instance_ceiling_parameters()
    {
        var context = new CommandContext(null);
        var templates = Path.Combine(Path.GetDirectoryName(context.CoreBicepPath)!, "templates");

        var elastic = File.ReadAllText(Path.Combine(templates, "functionApp.bicep"));
        Assert.Contains("param functionAppScaleLimit int = 0", elastic, StringComparison.Ordinal);
        // 0 is the platform's "unrestricted" value; it must be written, not dropped as null,
        // so a cap applied by an earlier deployment can be cleared.
        Assert.Contains("functionAppScaleLimit: functionAppScaleLimit", elastic, StringComparison.Ordinal);
        Assert.DoesNotContain("functionAppScaleLimit > 0", elastic, StringComparison.Ordinal);

        var flex = File.ReadAllText(Path.Combine(templates, "flexConsumptionFunctionApp.bicep"));
        Assert.Contains("param maximumInstanceCount int = 100", flex, StringComparison.Ordinal);
    }
}
