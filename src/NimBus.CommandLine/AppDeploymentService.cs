using System.IO.Compression;
using System.Text.Json;

namespace NimBus.CommandLine;

internal sealed class AppDeploymentService
{
    private static readonly Version MinimumFlexConsumptionAzureCliVersion = new(2, 60, 0);
    private static readonly string[] AzureCliVersionArguments = ["version", "--output", "json"];

    private readonly IAzureCliRunner _az;
    private readonly IDeploymentArtifactSource _artifacts;
    private readonly PlatformPackage? _platformPackage;
    private readonly PrivateEndpointDnsCheck _dnsCheck;

    public AppDeploymentService(IAzureCliRunner az, IDeploymentArtifactSource artifacts, PlatformPackage? platformPackage = null, PrivateEndpointDnsCheck? dnsCheck = null)
    {
        _az = az;
        _artifacts = artifacts;
        _platformPackage = platformPackage;
        _dnsCheck = dnsCheck ?? new PrivateEndpointDnsCheck(az);
    }

    public async Task DeployAsync(AppDeploymentOptions options, CancellationToken cancellationToken)
    {
        var deployResolver = options.Target != AppDeploymentTarget.WebApp;
        var deployWebApp = options.Target != AppDeploymentTarget.Resolver;

        await _az.EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);

        // The app names carry the postfix 'nb infra apply' deployed with (recorded on the
        // resource group); the namespace override does not affect them.
        var names = await ResourceNaming.ResolveDeployedNamesAsync(
            _az,
            options.SolutionId,
            options.Environment,
            options.ResourceGroupName,
            explicitServiceBusNamespaceName: null,
            options.ResourceNamePostfix,
            cancellationToken).ConfigureAwait(false);

        // Deployments go through each app's Kudu/scm site, which a private deployment only
        // exposes through the app's private endpoint (spec 034 §5.7).
        var siteEndpoints = new List<string>();
        if (deployResolver) siteEndpoints.Add(PrivateEndpointNames.Site(names.ResolverFunctionAppName));
        if (deployWebApp) siteEndpoints.Add(PrivateEndpointNames.Site(names.WebAppName));
        await PrivateNetworkPreflight.RunIfPrivateAsync(
            _az,
            _dnsCheck,
            options.ResourceGroupName,
            siteEndpoints,
            options.DnsWait,
            "deploying the applications",
            cancellationToken).ConfigureAwait(false);

        var version = await _artifacts.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        CliOutput.WriteLine(version != null
            ? $"Deploying NimBus {version}."
            : "No version could be determined; deploying with the default version.");

        if (deployResolver)
        {
            var isFlexConsumption = await IsFlexConsumptionResolverAsync(options.ResourceGroupName, names.CoreAppServicePlanName, cancellationToken).ConfigureAwait(false);
            if (isFlexConsumption)
            {
                await EnsureAzureCliSupportsFlexConsumptionAsync(cancellationToken).ConfigureAwait(false);
            }

            var resolverZip = await _artifacts.GetResolverZipAsync(cancellationToken).ConfigureAwait(false);
            await DeployResolverAsync(options, names, resolverZip, isFlexConsumption, cancellationToken).ConfigureAwait(false);
        }

        if (deployWebApp)
        {
            var webAppZip = await _artifacts.GetWebAppZipAsync(cancellationToken).ConfigureAwait(false);

            // The WebApp renders Endpoints, Event Types and — through EventJsonMasker —
            // PII masking from an IPlatform catalog. The released artifact only carries
            // NimBus's built-in one, so a customer's catalog has to travel with it.
            var staged = _platformPackage is null ? null : StageWebAppWithPlatform(webAppZip);

            try
            {
                await _az.EnsureSuccessAsync(
                    new[]
                    {
                        "webapp", "deploy",
                        "--resource-group", options.ResourceGroupName,
                        "--name", names.WebAppName,
                        "--src-path", staged ?? webAppZip,
                        "--type", "zip",
                    },
                    cancellationToken,
                    $"Failed to deploy the web app '{names.WebAppName}'.").ConfigureAwait(false);
            }
            finally
            {
                if (staged is not null && File.Exists(staged)) File.Delete(staged);
            }

            // Always reconcile, never only on the way in: the settings outlive a zip deploy,
            // so a later deployment without a platform package would leave the WebApp
            // pointing at an assembly that is no longer in the zip.
            await ApplyPlatformSettingsAsync(options, names, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Copies the released WebApp zip and adds the platform package's assemblies at its
    /// root, next to NimBus.WebApp.dll. The cached artifact is never modified — it is
    /// shared by every deployment this machine performs, and mutating it would make the
    /// next deployment to a different environment carry this customer's catalog.
    /// </summary>
    private string StageWebAppWithPlatform(string webAppZip)
    {
        var staged = Path.Combine(
            Path.GetTempPath(),
            "nb",
            $"webapp-{_platformPackage!.PackageId}-{_platformPackage.Version}-{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.Copy(webAppZip, staged, overwrite: true);

        using (var archive = ZipFile.Open(staged, ZipArchiveMode.Update))
        {
            foreach (var assemblyPath in _platformPackage.AssemblyPaths)
            {
                var entryName = Path.GetFileName(assemblyPath);
                // The released WebApp already ships NimBus's own assemblies; letting the
                // package overwrite one would swap a tested binary for whatever the
                // customer happened to build against.
                if (archive.GetEntry(entryName) is not null)
                {
                    // Skipping the assembly that actually exposes the platform would leave
                    // NimBus__PlatformAssembly pointing at the WebApp's own file of that
                    // name, and startup would throw on a type it cannot find there.
                    if (string.Equals(assemblyPath, _platformPackage.PrimaryAssemblyPath, StringComparison.Ordinal))
                    {
                        throw new CommandException(
                            $"The platform package's assembly '{entryName}' has the same name as one the WebApp already ships, so the catalog cannot be deployed. Rename the assembly that contains your IPlatform type.");
                    }

                    CliOutput.WriteLine($"Skipping '{entryName}' from the platform package: the WebApp already ships it.");
                    continue;
                }

                archive.CreateEntryFromFile(assemblyPath, entryName, CompressionLevel.Optimal);
            }
        }

        CliOutput.WriteLine($"Staged the WebApp with the catalog from {_platformPackage.PackageId} {_platformPackage.Version}.");
        return staged;
    }

    /// <summary>
    /// Points the deployed WebApp at the catalog that just travelled with it, or clears the
    /// settings when none did. The assembly setting is a bare file name; the WebApp resolves
    /// it against its content root, which keeps the setting identical on Windows and Linux
    /// App Service.
    /// </summary>
    /// <remarks>
    /// Clearing matters as much as setting. <c>AddPlatformCatalog</c> throws when
    /// PlatformType names a type it cannot load, so a stale setting left over from an
    /// earlier <c>--platform-package</c> deployment would fail every request that touches
    /// <c>IPlatform</c>. Falling back to the built-in catalog is the recoverable outcome.
    /// </remarks>
    private async Task ApplyPlatformSettingsAsync(AppDeploymentOptions options, DeploymentNames names, CancellationToken cancellationToken)
    {
        if (_platformPackage is null)
        {
            // Deleting settings that are not present succeeds, so this is safe to run on
            // every plain deployment.
            await _az.EnsureSuccessAsync(
                new[]
                {
                    "webapp", "config", "appsettings", "delete",
                    "--resource-group", options.ResourceGroupName,
                    "--name", names.WebAppName,
                    "--setting-names", "NimBus__PlatformType", "NimBus__PlatformAssembly",
                    "--output", "none",
                },
                cancellationToken,
                $"Failed to clear the platform catalog settings on '{names.WebAppName}'.").ConfigureAwait(false);
            return;
        }

        await _az.EnsureSuccessAsync(
            new[]
            {
                "webapp", "config", "appsettings", "set",
                "--resource-group", options.ResourceGroupName,
                "--name", names.WebAppName,
                "--settings",
                $"NimBus__PlatformType={_platformPackage.PlatformTypeName}",
                $"NimBus__PlatformAssembly={Path.GetFileName(_platformPackage.PrimaryAssemblyPath)}",
                "--output", "none",
            },
            cancellationToken,
            $"Failed to point '{names.WebAppName}' at the platform catalog.").ConfigureAwait(false);
    }

    private async Task DeployResolverAsync(AppDeploymentOptions options, DeploymentNames names, string resolverZip, bool isFlexConsumption, CancellationToken cancellationToken)
    {
        var deployZipArguments = new[]
        {
            "functionapp", "deployment", "source", "config-zip",
            "--resource-group", options.ResourceGroupName,
            "--name", names.ResolverFunctionAppName,
            "--src", resolverZip,
        };

        if (isFlexConsumption)
        {
            // Flex Consumption deployments must run against a STARTED app: after
            // publishing, the az CLI polls the host status endpoint and reports the
            // deployment as failed when the app is stopped. OneDeploy swaps the
            // package atomically, so the stop/start dance is unnecessary anyway.
            CliOutput.WriteLine($"Deploying '{names.ResolverFunctionAppName}' (Flex Consumption)...");
            await _az.EnsureSuccessAsync(
                deployZipArguments,
                cancellationToken,
                $"Failed to deploy the resolver app '{names.ResolverFunctionAppName}'.").ConfigureAwait(false);
        }
        else
        {
            CliOutput.WriteLine($"Stopping '{names.ResolverFunctionAppName}' for deployment...");
            await _az.EnsureSuccessAsync(
                new[] { "functionapp", "stop", "--resource-group", options.ResourceGroupName, "--name", names.ResolverFunctionAppName },
                cancellationToken,
                $"Failed to stop '{names.ResolverFunctionAppName}'.").ConfigureAwait(false);

            try
            {
                await _az.EnsureSuccessAsync(
                    deployZipArguments,
                    cancellationToken,
                    $"Failed to deploy the resolver app '{names.ResolverFunctionAppName}'.").ConfigureAwait(false);
            }
            finally
            {
                CliOutput.WriteLine($"Starting '{names.ResolverFunctionAppName}'...");
                await _az.EnsureSuccessAsync(
                    new[] { "functionapp", "start", "--resource-group", options.ResourceGroupName, "--name", names.ResolverFunctionAppName },
                    CancellationToken.None,
                    $"Failed to start '{names.ResolverFunctionAppName}'.").ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> IsFlexConsumptionResolverAsync(string resourceGroupName, string coreAppServicePlanName, CancellationToken cancellationToken)
    {
        var tier = await _az.CaptureValueAsync(
            new[]
            {
                "appservice", "plan", "show",
                "--resource-group", resourceGroupName,
                "--name", coreAppServicePlanName,
                "--query", "sku.tier",
                "--output", "tsv",
            },
            cancellationToken,
            $"Failed to read the core App Service Plan '{coreAppServicePlanName}'. Run 'nb infra apply' first.").ConfigureAwait(false);

        return string.Equals(tier.Trim(), "FlexConsumption", StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureAzureCliSupportsFlexConsumptionAsync(CancellationToken cancellationToken)
    {
        using var versionDocument = await _az.CaptureJsonAsync(
            AzureCliVersionArguments,
            cancellationToken,
            "Failed to read the Azure CLI version.").ConfigureAwait(false);

        var rawVersion = versionDocument.RootElement.TryGetProperty("azure-cli", out var azureCliVersion)
            ? azureCliVersion.GetString()
            : null;

        // Pre-2.60 CLIs push Flex Consumption zips to the legacy Kudu zipdeploy
        // endpoint, which the Flex SCM front-end rejects at the TLS layer — the
        // resulting SSLEOFError blames a proxy and hides the real cause.
        if (Version.TryParse(rawVersion, out var installedVersion) && installedVersion < MinimumFlexConsumptionAzureCliVersion)
        {
            throw new CommandException(
                $"Azure CLI {rawVersion} cannot deploy to Flex Consumption function apps; version {MinimumFlexConsumptionAzureCliVersion} or later is required. Upgrade with 'az upgrade' (or 'winget upgrade Microsoft.AzureCLI') and retry.");
        }
    }
}
