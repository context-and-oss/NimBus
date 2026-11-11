using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NimBus.CommandLine;

internal sealed class InfrastructureDeployer
{
    private static readonly string[] RegisterEventGridArguments = ["provider", "register", "--namespace", "Microsoft.EventGrid"];

    private readonly CommandContext _context;
    private readonly IAzureCliRunner _az;
    private readonly PrivateEndpointDnsCheck _dnsCheck;

    public InfrastructureDeployer(CommandContext context, IAzureCliRunner az, PrivateEndpointDnsCheck? dnsCheck = null)
    {
        _context = context;
        _az = az;
        _dnsCheck = dnsCheck ?? new PrivateEndpointDnsCheck(az);
    }

    public async Task ApplyAsync(InfrastructureOptions options, CancellationToken cancellationToken)
    {
        // Fail fast on a plan-specific --resolver-max-instances range error when the plan is
        // explicit, before the login and provider-registration side effects. An auto-pinned
        // plan is only known after discovery, so that case is validated in
        // DeployCoreInfrastructureAsync.
        if (options.ResolverPlan is { } explicitPlan)
        {
            _ = PlanSelection.ResolveResolverMaxInstances(options.ResolverMaxInstances, explicitPlan);
        }

        // Private networking options (spec 034) are checked the same way as far as they can
        // be before the recorded setup is read: malformed or contradictory values fail
        // before login or any Azure call.
        var explicitNetwork = options.Network ?? NetworkOptions.None;
        NetworkSelection.ValidateSyntax(explicitNetwork);
        if (!string.IsNullOrWhiteSpace(options.ServiceBusNamespaceName))
        {
            NetworkSelection.ValidateServiceBusNamespaceName(options.ServiceBusNamespaceName.Trim());
        }

        // Legacy naming options fail on malformed values before login too.
        _ = ResourceNaming.NormalizePostfix(options.ResourceNamePostFix);
        _ = ResourceNaming.NormalizeFuncStorageAccountName(options.FuncStorageAccountName);

        await _az.EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);

        // Provider registration is subscription-scoped, so an RG-scoped pipeline
        // identity cannot perform it — and nothing in the bicep deploys Event Grid
        // resources (the provider only backs optional storage-hook webhooks).
        // Warn instead of failing the whole deployment.
        var eventGridRegistration = await _az.TryRunAsync(
            RegisterEventGridArguments,
            cancellationToken).ConfigureAwait(false);
        if (!eventGridRegistration.Succeeded)
        {
            CliOutput.WriteLine(
                "Warning: could not register the Microsoft.EventGrid provider (requires subscription-level permission). Pre-register it once per subscription if you plan to use Event Grid storage hooks." +
                $"{Environment.NewLine}{eventGridRegistration.StandardError}".TrimEnd());
        }

        // The recorded setup completes whatever the command line leaves out (spec 034 §5.13).
        var resourceGroup = await ResourceGroupTags.ReadAsync(_az, options.ResourceGroupName, cancellationToken).ConfigureAwait(false);
        var stored = NetworkIntent.FromTags(resourceGroup.Tags);
        var (network, serviceBusNamespaceName) = NetworkIntent.Merge(explicitNetwork, options.ServiceBusNamespaceName, stored);
        if (stored is { State: not NetworkState.Public } && explicitNetwork.Mode is null)
        {
            CliOutput.WriteLine($"Using the network setup recorded on '{options.ResourceGroupName}' ({NetworkIntent.ToTagValue(stored.State)}).");
        }

        // Legacy naming: a recorded postfix or storage account fills in a missing option, and a
        // different one fails before anything deploys.
        var resourceNamePostfix = ResourceNaming.ResolvePostfix(options.ResourceNamePostFix, resourceGroup.Tags, options.ResourceGroupName);
        var funcStorageAccountName = ResourceNaming.ResolveFuncStorageAccountName(options.FuncStorageAccountName, resourceGroup.Tags, options.ResourceGroupName);

        var names = NamingConventions.Build(options.SolutionId, options.Environment, serviceBusNamespaceName, resourceNamePostfix, funcStorageAccountName);
        var existingLocations = await DiscoverExistingLocationsAsync(options.ResourceGroupName, cancellationToken).ConfigureAwait(false);

        // The first postfix decision on a resource group: a postfix (or its absence) that does
        // not match the existing deployment would create a second set next to it.
        if (!resourceGroup.Tags.TryGetValue(ResourceNaming.PostfixTag, out var recordedPostfix) || string.IsNullOrWhiteSpace(recordedPostfix))
        {
            ResourceNaming.GuardAgainstParallelDeployment(names, existingLocations.Keys, options.ResourceGroupName);
        }
        var existingPlans = await DiscoverExistingPlansAsync(options.ResourceGroupName, cancellationToken).ConfigureAwait(false);

        // An existing deployment pins its plan choices (like the location pins):
        // EP <-> Flex cannot convert in place, and re-runs must not silently
        // rescale the management plan. Explicit CLI flags still win.
        existingPlans.TryGetValue(names.CoreAppServicePlanName, out var existingCorePlan);
        existingPlans.TryGetValue(names.ManagementAppServicePlanName, out var existingManagementPlan);

        var resolverPlan = PlanSelection.ResolveResolverPlan(options.ResolverPlan, existingCorePlan?.Tier);
        if (options.ResolverPlan is null && existingCorePlan is not null)
        {
            CliOutput.WriteLine($"Pinning resolver plan to the existing '{names.CoreAppServicePlanName}' plan type ({resolverPlan}).");
        }

        var managementPlanSku = PlanSelection.ResolveManagementPlanSku(options.ManagementPlanSku, existingManagementPlan?.SkuName, names.Environment);
        if (string.IsNullOrWhiteSpace(options.ManagementPlanSku) && existingManagementPlan is not null)
        {
            CliOutput.WriteLine($"Pinning management plan SKU to the existing '{names.ManagementAppServicePlanName}' SKU ({managementPlanSku}).");
        }

        // Checked once the plan is known: a new Elastic Premium Resolver derives its content
        // share name from the app name. Still before anything is recorded or deployed.
        ResourceNaming.ValidateDerivedNames(
            names,
            serviceBusNamespaceName,
            options.SqlServerName,
            new ResourceNaming.DerivedNameScope(
                UsesCosmos: options.StorageProvider == StorageProviderChoice.Cosmos,
                ProvisionsSqlServer: options.StorageProvider == StorageProviderChoice.SqlServer && options.SqlMode == SqlProvisioningMode.Provision,
                CreatesResolverContentShare: resolverPlan == ResolverPlanChoice.ElasticPremium && !existingLocations.ContainsKey(names.ResolverFunctionAppName)));
        if (resourceNamePostfix is not null)
        {
            CliOutput.WriteLine(ResourceNaming.DescribePostfix(names, PostfixedNames(options, names, serviceBusNamespaceName)));
        }

        // The existing namespace decides the Service Bus tier (Azure cannot convert Standard
        // to Premium in place) and guards against silently reopening a private deployment.
        var existingServiceBus = await DiscoverServiceBusAsync(options.ResourceGroupName, names.ServiceBusNamespace, cancellationToken).ConfigureAwait(false);
        var networkMode = NetworkSelection.ResolveNetworkMode(
            network.Mode,
            explicitlyRequested: explicitNetwork.Mode is not null,
            network.AllowPublicAccess,
            existingServiceBus,
            names.ServiceBusNamespace);
        network = network with { Mode = networkMode };
        NetworkSelection.ValidateOptions(network);

        var isPrivate = networkMode == NetworkModeChoice.Private;
        var (serviceBusSku, serviceBusCapacity) = NetworkSelection.ResolveServiceBusSku(isPrivate, existingServiceBus, options.ServiceBusCapacity, names.ServiceBusNamespace);

        var deploymentExists = existingServiceBus is not null
            || existingLocations.ContainsKey(names.ResolverFunctionAppName)
            || existingLocations.ContainsKey(names.WebAppName);
        var currentState = NetworkIntent.CurrentState(stored, existingServiceBus);
        var targetState = NetworkIntent.TargetState(networkMode, network.AllowPublicAccess);
        NetworkIntent.ValidateTransition(currentState, targetState, deploymentExists, network.SkipTransition);

        // Pass 2 locks everything, so this is the last chance to stop: the endpoints exist
        // from pass 1, and a runner that does not resolve them now would lock itself out of
        // the topology and app deployment steps that follow (spec 034 §5.7).
        if (currentState == NetworkState.PrivateTransition && targetState == NetworkState.Private)
        {
            await PrivateNetworkPreflight.RunAsync(
                _dnsCheck,
                options.ResourceGroupName,
                new[]
                {
                    PrivateEndpointNames.ServiceBus(names.ServiceBusNamespace),
                    PrivateEndpointNames.Site(names.ResolverFunctionAppName),
                    PrivateEndpointNames.Site(names.WebAppName),
                },
                options.DnsWait,
                failOnMismatch: true,
                "turning public access off",
                cancellationToken).ConfigureAwait(false);
        }
        if (existingServiceBus is { IsPremium: true } && !isPrivate)
        {
            CliOutput.WriteLine($"Keeping the existing Premium Service Bus namespace '{names.ServiceBusNamespace}' ({serviceBusCapacity} messaging unit(s)).");
        }

        ResolvedNetwork? privateNetwork = null;
        if (isPrivate)
        {
            NetworkSelection.ValidateManagementPlanSku(managementPlanSku);
            privateNetwork = await ResolvePrivateNetworkAsync(options, network, names, resolverPlan, existingLocations, resourceGroup.Location, cancellationToken).ConfigureAwait(false);
            CliOutput.WriteLine(network.AllowPublicAccess
                ? "Network mode: private-transition (private endpoints and VNet integration; public access stays on)."
                : "Network mode: private (public network access off).");
        }

        var serviceBus = new ServiceBusDeployment(serviceBusSku, serviceBusCapacity, serviceBusNamespaceName);

        // A reused storage account keeps its SKU and kind; an unusable kind fails here, before
        // anything is recorded or deployed.
        var funcStorage = funcStorageAccountName is null
            ? null
            : new FuncStorageDeployment(
                funcStorageAccountName,
                await DiscoverFuncStorageAccountAsync(options.ResourceGroupName, funcStorageAccountName, cancellationToken).ConfigureAwait(false));

        // Private mode left a reused account at Deny with no bypass. Keeping that while the
        // cleanup removes the private endpoints and VNet integration would cut the Resolver
        // off from its storage, so leaving private reopens the firewall (its rules are kept).
        var leavesPrivate = targetState != NetworkState.Private
            && (currentState == NetworkState.Private || NetworkIntent.NeedsCleanup(stored, targetState));
        if (leavesPrivate && funcStorage?.Existing is { NetworkAcls: { } lockedAcls } reused)
        {
            CliOutput.WriteLine($"Reopening the firewall of the storage account '{funcStorage.Name}' (default action Allow, Azure services bypass) as the deployment leaves private mode.");
            funcStorage = funcStorage with { Existing = reused with { NetworkAcls = ReopenNetworkAcls(lockedAcls) } };
        }

        // Record the intent before deploying: an interrupted run then converges on the next
        // one, and a validation failure above never records a setup that cannot deploy. A
        // deployment that never used private mode or a namespace override gets no tags.
        // Leaving private mode records a pending cleanup with the intent, so an interrupted
        // cleanup still runs on the next deployment; it is cleared only once it finishes.
        var needsCleanup = NetworkIntent.NeedsCleanup(stored, targetState);
        if (stored is not null || isPrivate || !string.IsNullOrWhiteSpace(serviceBusNamespaceName))
        {
            var desired = new Dictionary<string, string>(NetworkIntent.ToTags(network, serviceBusNamespaceName), StringComparer.OrdinalIgnoreCase);
            if (needsCleanup)
            {
                desired[NetworkIntent.CleanupPendingTag] = NetworkIntent.CleanupPendingValue;
            }

            await RecordNetworkIntentAsync(resourceGroup, desired, cancellationToken).ConfigureAwait(false);
        }

        await RecordResourceNamesAsync(resourceGroup, resourceNamePostfix, funcStorageAccountName, cancellationToken).ConfigureAwait(false);

        CliOutput.WriteLine("Deploying core infrastructure...");
        await DeployCoreInfrastructureAsync(options, names, resolverPlan, managementPlanSku, existingLocations, serviceBus, privateNetwork, funcStorage, cancellationToken).ConfigureAwait(false);

        // The template stops declaring the allow-all-Azure rule once a server is private, but
        // an incremental deployment does not delete it (spec 034 §5.13).
        if (targetState == NetworkState.Private
            && options.StorageProvider == StorageProviderChoice.SqlServer
            && options.SqlMode == SqlProvisioningMode.Provision)
        {
            await DeleteSqlAllowAzureRuleAsync(options.ResourceGroupName, EffectiveSqlServerName(options, names), cancellationToken).ConfigureAwait(false);
        }

        CliOutput.WriteLine("Preparing web app infrastructure inputs...");
        await _az.EnsureExtensionAsync("application-insights", cancellationToken).ConfigureAwait(false);

        // No Application Insights API key: the query API stopped accepting them on 2026-03-31.
        // The WebApp queries with its managed identity, which deploy.webapp.bicep grants Reader
        // on the component.
        var cosmosAccountEndpoint = options.StorageProvider == StorageProviderChoice.Cosmos
            ? await GetCosmosAccountEndpointAsync(options.ResourceGroupName, names.CosmosAccountName, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        var sqlConnectionString = ResolveSqlConnectionString(options, names);
        var serviceBusFullyQualifiedNamespace = GetServiceBusFullyQualifiedNamespace(names.ServiceBusNamespace);
        var appInsightsAppId = await _az.CaptureValueAsync(
            new[]
            {
                "monitor", "app-insights", "component", "show",
                "--app", names.AppInsightsName,
                "--resource-group", options.ResourceGroupName,
                "--query", "appId",
                "--output", "tsv",
            },
            cancellationToken,
            $"Failed to read Application Insights app id for '{names.AppInsightsName}'.").ConfigureAwait(false);

        var instrumentationKey = await _az.CaptureValueAsync(
            new[]
            {
                "monitor", "app-insights", "component", "show",
                "--app", names.AppInsightsName,
                "--resource-group", options.ResourceGroupName,
                "--query", "instrumentationKey",
                "--output", "tsv",
            },
            cancellationToken,
            $"Failed to read Application Insights instrumentation key for '{names.AppInsightsName}'.").ConfigureAwait(false);

        CliOutput.WriteLine("Deploying web app infrastructure...");
        await DeployWebAppInfrastructureAsync(
            options,
            names,
            appInsightsAppId,
            instrumentationKey,
            cosmosAccountEndpoint,
            sqlConnectionString,
            serviceBusFullyQualifiedNamespace,
            PlanSelection.SupportsAlwaysOn(managementPlanSku),
            existingLocations,
            serviceBus,
            privateNetwork,
            cancellationToken).ConfigureAwait(false);

        // Leaving private mode: both deployments above reopened public access while the
        // private endpoints and VNet integration still existed, so clients kept working on
        // either path. Only now is the private network taken down (spec 034 §5.13). Runs on
        // every public deployment that has a record, so an interrupted cleanup continues.
        if (needsCleanup)
        {
            await new PrivateNetworkCleanup(_az).RunAsync(
                options.ResourceGroupName,
                PrivateNetworkCleanup.OwnerValue(names),
                names,
                stored!.Network.DnsMode,
                cancellationToken).ConfigureAwait(false);

            await _az.EnsureSuccessAsync(
                TagUpdateArguments(
                    resourceGroup.Id,
                    "Delete",
                    new Dictionary<string, string> { [NetworkIntent.CleanupPendingTag] = NetworkIntent.CleanupPendingValue }),
                cancellationToken,
                "The private network cleanup finished, but its pending marker could not be removed from the resource group. The next public deployment repeats the (now empty) cleanup.").ConfigureAwait(false);
        }
    }

    private async Task DeleteSqlAllowAzureRuleAsync(string resourceGroupName, string serverName, CancellationToken cancellationToken)
    {
        const string ruleName = "AllowAllWindowsAzureIps";
        var existing = await _az.TryRunAsync(
            new[] { "sql", "server", "firewall-rule", "show", "--resource-group", resourceGroupName, "--server", serverName, "--name", ruleName, "--output", "json" },
            cancellationToken).ConfigureAwait(false);
        if (!existing.Succeeded || string.IsNullOrWhiteSpace(existing.StandardOutput) || existing.StandardOutput.TrimStart().StartsWith('['))
        {
            return;
        }

        CliOutput.WriteLine($"Deleting the '{ruleName}' firewall rule from '{serverName}': it admits every Azure tenant and has no place on a private server.");
        await _az.EnsureSuccessAsync(
            new[] { "sql", "server", "firewall-rule", "delete", "--resource-group", resourceGroupName, "--server", serverName, "--name", ruleName },
            cancellationToken,
            $"Could not delete the '{ruleName}' firewall rule from '{serverName}'. Public access is already off; delete the rule manually.").ConfigureAwait(false);
    }

    private async Task<Dictionary<string, string>> DiscoverExistingLocationsAsync(string resourceGroupName, CancellationToken cancellationToken)
    {
        var locations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = await _az.TryRunAsync(
            new[]
            {
                "resource", "list",
                "--resource-group", resourceGroupName,
                "--query", "[].{name:name, location:location}",
                "--output", "json",
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return locations;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("name", out var nameElement) || !element.TryGetProperty("location", out var locationElement))
                {
                    continue;
                }

                var name = nameElement.GetString();
                var location = locationElement.GetString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(location))
                {
                    continue;
                }

                locations[name] = location;
            }
        }
        catch (JsonException)
        {
            // Resource group is empty or the response isn't JSON-shaped — proceed with no pins.
        }

        return locations;
    }

    private async Task<Dictionary<string, ExistingAppServicePlan>> DiscoverExistingPlansAsync(string resourceGroupName, CancellationToken cancellationToken)
    {
        // The generic `az resource list` does not reliably populate sku, so ask the
        // Web resource provider directly; one call covers both NimBus plans.
        var plans = new Dictionary<string, ExistingAppServicePlan>(StringComparer.OrdinalIgnoreCase);
        var result = await _az.TryRunAsync(
            new[]
            {
                "appservice", "plan", "list",
                "--resource-group", resourceGroupName,
                "--query", "[].{name:name, skuName:sku.name, tier:sku.tier}",
                "--output", "json",
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return plans;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var name = element.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var skuName = element.TryGetProperty("skuName", out var skuElement) ? skuElement.GetString() : null;
                var tier = element.TryGetProperty("tier", out var tierElement) ? tierElement.GetString() : null;
                plans[name] = new ExistingAppServicePlan(skuName ?? string.Empty, tier ?? string.Empty);
            }
        }
        catch (JsonException)
        {
            // Resource group is empty or the response isn't JSON-shaped — proceed with no pins.
        }

        return plans;
    }

    private static void AddPinnedLocation(List<string> arguments, Dictionary<string, string> existingLocations, string resourceName, string bicepParamName, List<(string Name, string Location)> pinned)
    {
        if (existingLocations.TryGetValue(resourceName, out var location) && !string.IsNullOrWhiteSpace(location))
        {
            arguments.Add($"{bicepParamName}={location}");
            pinned.Add((resourceName, location));
        }
    }

    private static string ResolveSqlConnectionString(InfrastructureOptions options, DeploymentNames names)
    {
        if (options.StorageProvider != StorageProviderChoice.SqlServer) return string.Empty;
        if (options.SqlMode == SqlProvisioningMode.External)
        {
            return options.SqlConnectionString ?? string.Empty;
        }

        var sqlServerName = EffectiveSqlServerName(options, names);
        var builder = new DbConnectionStringBuilder
        {
            ["Server"] = $"tcp:{sqlServerName}.database.windows.net,1433",
            ["Initial Catalog"] = "MessageDatabase",
            ["User ID"] = options.SqlAdminLogin ?? string.Empty,
            ["Password"] = options.SqlAdminPassword ?? string.Empty,
            ["Encrypt"] = true,
        };

        return builder.ConnectionString;
    }

    private static string EffectiveSqlServerName(InfrastructureOptions options, DeploymentNames names) =>
        string.IsNullOrWhiteSpace(options.SqlServerName)
            ? names.SqlServerName
            : options.SqlServerName!.ToLowerInvariant();

    private async Task DeployCoreInfrastructureAsync(
        InfrastructureOptions options,
        DeploymentNames names,
        ResolverPlanChoice resolverPlan,
        string managementPlanSku,
        Dictionary<string, string> existingLocations,
        ServiceBusDeployment serviceBus,
        ResolvedNetwork? privateNetwork,
        FuncStorageDeployment? funcStorage,
        CancellationToken cancellationToken)
    {
        var storageProviderParam = options.StorageProvider == StorageProviderChoice.SqlServer ? "sqlserver" : "cosmos";
        var sqlModeParam = options.SqlMode == SqlProvisioningMode.External ? "external" : "provision";
        var resolverPlanParam = resolverPlan == ResolverPlanChoice.FlexConsumption ? "FlexConsumption" : "ElasticPremium";

        var arguments = new List<string>
        {
            "deployment", "group", "create",
            "--resource-group", options.ResourceGroupName,
            "--template-file", _context.CoreBicepPath,
            "--parameters",
            $"solutionId={names.SolutionId}",
            $"environment={names.Environment}",
            $"resolverId={NimBus.Core.Messages.Constants.ResolverId}",
            $"storageProvider={storageProviderParam}",
            $"sqlMode={sqlModeParam}",
            $"resolverPlan={resolverPlanParam}",
            $"managementPlanSku={managementPlanSku}",
        };

        // An existing Elastic Premium Resolver keeps the content share its code lives on:
        // WEBSITE_CONTENTSHARE must not change between deployments.
        if (resolverPlan == ResolverPlanChoice.ElasticPremium && existingLocations.ContainsKey(names.ResolverFunctionAppName))
        {
            var existingShare = await ReadResolverContentShareAsync(options.ResourceGroupName, names.ResolverFunctionAppName, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(existingShare))
            {
                arguments.Add($"existingResolverContentShare={existingShare}");
            }
        }

        if (options.StorageProvider == StorageProviderChoice.SqlServer && options.SqlMode == SqlProvisioningMode.Provision)
        {
            arguments.Add($"sqlAdminLogin={options.SqlAdminLogin}");
        }

        if (!string.IsNullOrWhiteSpace(options.SqlServerName))
        {
            arguments.Add($"sqlServerName={EffectiveSqlServerName(options, names)}");
        }

        if (!string.IsNullOrWhiteSpace(options.Location))
        {
            arguments.Add($"locationParam={options.Location}");
        }

        // Resolver capacity: nothing is passed when the options are unset, so the
        // Bicep defaults (16 sessions, no Elastic Premium cap, Flex maximum 100) apply.
        if (options.ResolverMaxConcurrentSessions is { } resolverMaxConcurrentSessions)
        {
            arguments.Add(FormattableString.Invariant($"resolverMaxConcurrentSessions={resolverMaxConcurrentSessions}"));
        }

        if (PlanSelection.ResolveResolverMaxInstances(options.ResolverMaxInstances, resolverPlan) is { } resolverMaxInstances)
        {
            arguments.Add(FormattableString.Invariant($"{resolverMaxInstances.ParameterName}={resolverMaxInstances.Value}"));
        }

        AddAlertMailParameters(arguments, options.AlertMail);
        AddServiceBusParameters(arguments, serviceBus);
        AddResourceNamePostfixParameter(arguments, names);

        // Nothing is passed without an override, so the template's derived name and its
        // Standard_LRS / StorageV2 defaults apply exactly as before.
        if (funcStorage is not null)
        {
            arguments.Add($"funcStorageAccountName={funcStorage.Name}");
            if (funcStorage.Existing is { } existing)
            {
                arguments.Add($"funcStorageSkuName={existing.SkuName}");
                arguments.Add($"funcStorageKind={existing.Kind}");
                if (existing.NetworkAcls is { } networkAcls)
                {
                    arguments.Add($"funcStorageNetworkAcls={networkAcls}");
                }
            }
        }

        if (privateNetwork is not null)
        {
            AddSharedNetworkParameters(arguments, privateNetwork);
            arguments.Add($"resolverSubnetId={privateNetwork.ResolverSubnetId}");
            if (privateNetwork.DnsMode == PrivateDnsModeChoice.Create && privateNetwork.DnsLinkVnetIds.Count > 0)
            {
                arguments.Add($"privateDnsLinkVnetIds={JsonSerializer.Serialize(privateNetwork.DnsLinkVnetIds)}");
            }
        }

        var pinned = new List<(string Name, string Location)>();
        AddPinnedLocation(arguments, existingLocations, names.ServiceBusNamespace, "serviceBusLocation", pinned);
        AddPinnedLocation(arguments, existingLocations, names.AppInsightsName, "appInsightsLocation", pinned);
        AddPinnedLocation(arguments, existingLocations, names.CosmosAccountName, "cosmosLocation", pinned);
        AddPinnedLocation(arguments, existingLocations, EffectiveSqlServerName(options, names), "sqlLocation", pinned);
        AddPinnedLocation(arguments, existingLocations, names.FuncStorageAccountName, "funcStorageLocation", pinned);
        AddPinnedLocation(arguments, existingLocations, names.ManagementAppServicePlanName, "managementAppServicePlanLocation", pinned);
        AddPinnedLocation(arguments, existingLocations, names.CoreAppServicePlanName, "coreAppServicePlanLocation", pinned);
        AddPinnedLocation(arguments, existingLocations, names.ResolverFunctionAppName, "resolverFunctionAppLocation", pinned);

        if (pinned.Count > 0)
        {
            CliOutput.WriteLine($"Pinning {pinned.Count} existing resource(s) to their current location:");
            foreach (var (name, location) in pinned)
            {
                CliOutput.WriteLine($"  {name} → {location}");
            }
        }

        using var command = new AzureDeploymentCommand(arguments);
        if (options.StorageProvider == StorageProviderChoice.SqlServer && options.SqlMode == SqlProvisioningMode.Provision)
        {
            command.AddSecureParameter("sqlAdminPassword", options.SqlAdminPassword ?? string.Empty);
        }

        if (options.AlertMail?.SmtpPassword is { } alertMailSmtpPassword)
        {
            command.AddSecureParameter("alertMailSmtpPassword", alertMailSmtpPassword);
        }

        await _az.EnsureSuccessAsync(
            command.BuildArguments(),
            _context.DeployDirectory,
            cancellationToken,
            "Core infrastructure deployment failed.").ConfigureAwait(false);
    }

    /// <summary>
    /// Endpoint alert mail for the Resolver. Nothing is passed when it is off, so the template
    /// leaves the settings out; the Resolver's app settings are template-owned, so that also
    /// turns off alert mail an earlier run turned on. Unset optional values keep the template
    /// defaults. The password is a secure parameter, never a process argument.
    /// </summary>
    private static void AddAlertMailParameters(List<string> arguments, AlertMailDeployment? alertMail)
    {
        if (alertMail is null)
        {
            return;
        }

        arguments.Add($"alertMailSmtpHost={alertMail.SmtpHost}");
        arguments.Add($"alertMailFrom={alertMail.From}");
        if (alertMail.SmtpPort is { } port)
        {
            arguments.Add(FormattableString.Invariant($"alertMailSmtpPort={port}"));
        }

        if (alertMail.SmtpUseSsl is { } useSsl)
        {
            arguments.Add($"alertMailSmtpUseSsl={(useSsl ? "true" : "false")}");
        }

        if (alertMail.SmtpUsername is { } username)
        {
            arguments.Add($"alertMailSmtpUsername={username}");
        }

        if (alertMail.WebAppUrl is { } webAppUrl)
        {
            arguments.Add($"alertMailWebAppUrl={webAppUrl}");
        }
    }

    private async Task DeployWebAppInfrastructureAsync(
        InfrastructureOptions options,
        DeploymentNames names,
        string appInsightsAppId,
        string instrumentationKey,
        string cosmosAccountEndpoint,
        string sqlConnectionString,
        string serviceBusFullyQualifiedNamespace,
        bool alwaysOnEnabled,
        Dictionary<string, string> existingLocations,
        ServiceBusDeployment serviceBus,
        ResolvedNetwork? privateNetwork,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "deployment", "group", "create",
            "--resource-group", options.ResourceGroupName,
            "--template-file", _context.WebAppBicepPath,
            "--parameters",
            $"solutionId={names.SolutionId}",
            $"environment={names.Environment}",
            $"webAppVersion={options.WebAppVersion}",
            $"appInsightsAppId={appInsightsAppId}",
            $"cosmosAccountEndpoint={cosmosAccountEndpoint}",
            $"serviceBusFullyQualifiedNamespace={serviceBusFullyQualifiedNamespace}",
            $"alwaysOnEnabled={(alwaysOnEnabled ? "true" : "false")}",
            // Lets the template read the site's current app settings and preserve the
            // out-of-band ones (AzureAd__*, ServiceBusManagement__*) across the
            // full-replace appSettings deployment.
            $"webAppExists={(existingLocations.ContainsKey(names.WebAppName) ? "true" : "false")}",
        };

        if (!string.IsNullOrWhiteSpace(options.IdentityAdminEmail))
        {
            arguments.Add($"identityAdminEmail={options.IdentityAdminEmail}");
        }

        if (!string.IsNullOrWhiteSpace(options.Location))
        {
            arguments.Add($"locationParam={options.Location}");
        }

        if (!string.IsNullOrWhiteSpace(serviceBus.NamespaceName))
        {
            arguments.Add($"serviceBusNamespaceName={serviceBus.NamespaceName}");
        }

        AddResourceNamePostfixParameter(arguments, names);

        if (privateNetwork is not null)
        {
            AddSharedNetworkParameters(arguments, privateNetwork);
            arguments.Add($"managementSubnetId={privateNetwork.WebAppSubnetId}");
        }

        var pinned = new List<(string Name, string Location)>();
        AddPinnedLocation(arguments, existingLocations, names.WebAppName, "webAppLocation", pinned);
        AddPinnedLocation(arguments, existingLocations, names.ManagementAppServicePlanName, "managementAppServicePlanLocation", pinned);

        using var command = new AzureDeploymentCommand(arguments);
        command.AddSecureParameter("instrumentationKey", instrumentationKey);
        command.AddSecureParameter("sqlConnectionString", sqlConnectionString);
        if (!string.IsNullOrWhiteSpace(options.IdentityAdminEmail))
        {
            command.AddSecureParameter("identityAdminPassword", options.IdentityAdminPassword ?? string.Empty);
        }

        await _az.EnsureSuccessAsync(
            command.BuildArguments(),
            _context.DeployDirectory,
            cancellationToken,
            "Web app infrastructure deployment failed.").ConfigureAwait(false);
    }

    private Task<string> GetCosmosAccountEndpointAsync(string resourceGroupName, string cosmosAccountName, CancellationToken cancellationToken) =>
        _az.CaptureValueAsync(
            new[]
            {
                "cosmosdb", "show",
                "--resource-group", resourceGroupName,
                "--name", cosmosAccountName,
                "--query", "documentEndpoint",
                "--output", "tsv",
            },
            cancellationToken,
            $"Failed to read the Cosmos DB endpoint for '{cosmosAccountName}'.");

    private static string GetServiceBusFullyQualifiedNamespace(string namespaceName) =>
        $"{namespaceName}.servicebus.windows.net";

    private async Task<string?> ReadResolverContentShareAsync(string resourceGroupName, string functionAppName, CancellationToken cancellationToken)
    {
        var result = await _az.TryRunAsync(
            new[]
            {
                "functionapp", "config", "appsettings", "list",
                "--resource-group", resourceGroupName,
                "--name", functionAppName,
                "--query", "[?name=='WEBSITE_CONTENTSHARE'].value | [0]",
                "--output", "tsv",
            },
            cancellationToken).ConfigureAwait(false);

        // Guessing here would repoint the Resolver at another share, so an unreadable setting
        // stops the deployment. A readable but absent setting gets the stable default name.
        if (!result.Succeeded)
        {
            throw new CommandException(
                $"Could not read WEBSITE_CONTENTSHARE of '{functionAppName}'. Deploying without it could move the Resolver to a different content share. {result.StandardError}".Trim());
        }

        var share = result.StandardOutput.Trim();
        return share.Length > 0 && !share.StartsWith('[') ? share : null;
    }

    /// <summary>
    /// Writes the desired network tags and removes stale NimBus network tags. Merge and
    /// delete touch only the named tags, so the customer's own tags stay as they are.
    /// </summary>
    private async Task RecordNetworkIntentAsync(ResourceGroupInfo resourceGroup, IReadOnlyDictionary<string, string> desired, CancellationToken cancellationToken)
    {
        var (merge, delete) = NetworkIntent.TagChanges(resourceGroup.Tags, desired);
        const string failure =
            "Could not record the network setup as tags on the resource group; an Azure Policy may forbid tag changes there. " +
            "Allow the nimbus-network-* tags, or pass the full set of network options on every run.";

        if (merge.Count > 0)
        {
            await _az.EnsureSuccessAsync(
                TagUpdateArguments(resourceGroup.Id, "Merge", merge),
                cancellationToken,
                failure).ConfigureAwait(false);
        }

        if (delete.Count > 0)
        {
            await _az.EnsureSuccessAsync(
                TagUpdateArguments(resourceGroup.Id, "Delete", delete),
                cancellationToken,
                failure).ConfigureAwait(false);
        }
    }

    private static List<string> TagUpdateArguments(string resourceId, string operation, IReadOnlyDictionary<string, string> tags)
    {
        var arguments = new List<string> { "tag", "update", "--resource-id", resourceId, "--operation", operation, "--tags" };
        arguments.AddRange(tags.OrderBy(tag => tag.Key, StringComparer.Ordinal).Select(tag => $"{tag.Key}={tag.Value}"));
        return arguments;
    }

    private async Task<ExistingServiceBus?> DiscoverServiceBusAsync(string resourceGroupName, string namespaceName, CancellationToken cancellationToken)
    {
        var result = await _az.TryRunAsync(
            new[]
            {
                "servicebus", "namespace", "show",
                "--resource-group", resourceGroupName,
                "--name", namespaceName,
                "--query", "{tier:sku.tier, capacity:sku.capacity, publicNetworkAccess:publicNetworkAccess}",
                "--output", "json",
            },
            cancellationToken).ConfigureAwait(false);

        // A missing namespace (a fresh deployment) fails the show call.
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tier", out var tier) || tier.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var capacity = root.TryGetProperty("capacity", out var capacityElement) && capacityElement.ValueKind == JsonValueKind.Number
                ? capacityElement.GetInt32()
                : 0;
            var publicNetworkAccess = root.TryGetProperty("publicNetworkAccess", out var access) && access.ValueKind == JsonValueKind.String
                ? access.GetString()
                : null;

            return new ExistingServiceBus(tier.GetString()!, capacity, publicNetworkAccess);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the three customer subnets and checks them against their roles before anything
    /// deploys: a wrong delegation or region otherwise fails minutes into the deployment.
    /// </summary>
    private async Task<ResolvedNetwork> ResolvePrivateNetworkAsync(
        InfrastructureOptions options,
        NetworkOptions network,
        DeploymentNames names,
        ResolverPlanChoice resolverPlan,
        IReadOnlyDictionary<string, string> existingLocations,
        string? resourceGroupLocation,
        CancellationToken cancellationToken)
    {
        var vnetLocations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var privateEndpointSubnet = await DescribeSubnetAsync(network.PrivateEndpointSubnetId!, vnetLocations, cancellationToken).ConfigureAwait(false);
        var resolverSubnet = await DescribeSubnetAsync(network.ResolverSubnetId!, vnetLocations, cancellationToken).ConfigureAwait(false);
        var webAppSubnet = await DescribeSubnetAsync(network.WebAppSubnetId!, vnetLocations, cancellationToken).ConfigureAwait(false);

        // Each app stays where it is (or where its plan is); a new one goes to --location,
        // else to the resource group's region, as the templates' locationParam default does.
        var resolverLocation = LocationOf(existingLocations, names.ResolverFunctionAppName)
            ?? LocationOf(existingLocations, names.CoreAppServicePlanName)
            ?? options.Location
            ?? resourceGroupLocation;
        var webAppLocation = LocationOf(existingLocations, names.WebAppName)
            ?? LocationOf(existingLocations, names.ManagementAppServicePlanName)
            ?? options.Location
            ?? resourceGroupLocation;

        NetworkSelection.ValidateSubnet(privateEndpointSubnet, SubnetRole.PrivateEndpoints, resolverPlan, appLocation: null);
        NetworkSelection.ValidateSubnet(resolverSubnet, SubnetRole.Resolver, resolverPlan, resolverLocation);
        NetworkSelection.ValidateSubnet(webAppSubnet, SubnetRole.WebApp, resolverPlan, webAppLocation);

        await WarnIfProviderNotRegisteredAsync("Microsoft.Network", "private endpoints and DNS zones", cancellationToken).ConfigureAwait(false);
        if (resolverPlan == ResolverPlanChoice.FlexConsumption)
        {
            await WarnIfProviderNotRegisteredAsync("Microsoft.App", "the Flex Consumption subnet delegation", cancellationToken).ConfigureAwait(false);
        }

        return new ResolvedNetwork(
            network.AllowPublicAccess,
            privateEndpointSubnet.Id,
            resolverSubnet.Id,
            webAppSubnet.Id,
            // A private endpoint lives in its subnet's region, which can differ from the
            // region of the resource it connects to.
            privateEndpointSubnet.VnetLocation,
            network.DnsMode!.Value,
            network.DnsZoneScope?.Trim().TrimEnd('/'),
            network.DnsLinkVnetIds ?? Array.Empty<string>());
    }

    private async Task<SubnetInfo> DescribeSubnetAsync(string subnetId, Dictionary<string, string> vnetLocations, CancellationToken cancellationToken)
    {
        using var subnet = await _az.CaptureJsonAsync(
            new[]
            {
                "network", "vnet", "subnet", "show",
                "--ids", subnetId,
                "--query", "{name:name, delegations:delegations[].serviceName}",
                "--output", "json",
            },
            cancellationToken,
            $"Could not read the subnet '{subnetId}'. Check that it exists and that the deploying identity can read it.").ConfigureAwait(false);

        var root = subnet.RootElement;
        var name = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()!
            : NetworkSelection.SubnetNameOf(subnetId);
        var delegations = root.TryGetProperty("delegations", out var delegationElement) && delegationElement.ValueKind == JsonValueKind.Array
            ? delegationElement.EnumerateArray().Select(d => d.GetString()).OfType<string>().ToList()
            : new List<string>();

        var vnetId = NetworkSelection.VnetIdOf(subnetId);
        if (!vnetLocations.TryGetValue(vnetId, out var vnetLocation))
        {
            vnetLocation = await _az.CaptureValueAsync(
                new[] { "network", "vnet", "show", "--ids", vnetId, "--query", "location", "--output", "tsv" },
                cancellationToken,
                $"Could not read the virtual network '{vnetId}'.").ConfigureAwait(false);
            vnetLocations[vnetId] = vnetLocation;
        }

        return new SubnetInfo(subnetId, name, vnetLocation, delegations);
    }

    private async Task WarnIfProviderNotRegisteredAsync(string providerNamespace, string neededFor, CancellationToken cancellationToken)
    {
        var result = await _az.TryRunAsync(
            new[] { "provider", "show", "--namespace", providerNamespace, "--query", "registrationState", "--output", "tsv" },
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded || !string.Equals(result.StandardOutput.Trim(), "Registered", StringComparison.OrdinalIgnoreCase))
        {
            CliOutput.WriteLine(
                $"Warning: the {providerNamespace} resource provider does not report as registered; {neededFor} need it. " +
                $"Run 'az provider register --namespace {providerNamespace}' once per subscription if the deployment fails.");
        }
    }

    private static string? LocationOf(IReadOnlyDictionary<string, string> existingLocations, string resourceName) =>
        existingLocations.TryGetValue(resourceName, out var location) && !string.IsNullOrWhiteSpace(location) ? location : null;

    private static void AddServiceBusParameters(List<string> arguments, ServiceBusDeployment serviceBus)
    {
        // Public deployments of a Standard namespace pass nothing, so their parameters stay
        // exactly as before; the template default is Standard.
        if (serviceBus.Sku is { } sku)
        {
            arguments.Add($"serviceBusSku={sku}");
        }

        if (serviceBus.Capacity is { } capacity)
        {
            arguments.Add(FormattableString.Invariant($"serviceBusCapacity={capacity}"));
        }

        if (!string.IsNullOrWhiteSpace(serviceBus.NamespaceName))
        {
            arguments.Add($"serviceBusNamespaceName={serviceBus.NamespaceName}");
        }
    }

    // Parameters both entry templates declare with the same meaning.
    private static void AddSharedNetworkParameters(List<string> arguments, ResolvedNetwork network)
    {
        arguments.Add("networkMode=private");
        arguments.Add($"allowPublicAccess={(network.AllowPublicAccess ? "true" : "false")}");
        arguments.Add($"privateEndpointSubnetId={network.PrivateEndpointSubnetId}");
        arguments.Add($"privateEndpointLocation={network.PrivateEndpointLocation}");
        arguments.Add($"privateDnsMode={NetworkSelection.ToParameterValue(network.DnsMode)}");
        if (network.DnsMode == PrivateDnsModeChoice.Existing)
        {
            arguments.Add($"privateDnsZoneScope={network.DnsZoneScope}");
        }
    }

    private sealed record ExistingAppServicePlan(string SkuName, string Tier);

    private sealed record ExistingStorageAccount(string SkuName, string Kind, string? NetworkAcls);

    private sealed record FuncStorageDeployment(string Name, ExistingStorageAccount? Existing);

    /// <summary>
    /// The account's networkAcls with the default action and bypass of a public deployment;
    /// IP, VNet and resource access rules stay as they are.
    /// </summary>
    internal static string ReopenNetworkAcls(string networkAclsJson)
    {
        var acls = JsonNode.Parse(networkAclsJson) as JsonObject
            ?? throw new CommandException("Could not read the storage account's network rules.");
        acls["defaultAction"] = "Allow";
        acls["bypass"] = "AzureServices";
        return acls.ToJsonString();
    }

    // The names the legacy pipeline postfixed (see NamingConventions.Build), as deployed by
    // this run's storage provider.
    // An explicit name override wins over the postfix, so an overridden name is not listed.
    private static IEnumerable<string> PostfixedNames(InfrastructureOptions options, DeploymentNames names, string? serviceBusNamespaceOverride)
    {
        if (string.IsNullOrWhiteSpace(serviceBusNamespaceOverride))
        {
            yield return names.ServiceBusNamespace;
        }

        if (options.StorageProvider == StorageProviderChoice.Cosmos)
        {
            yield return names.CosmosAccountName;
        }
        else if (options.SqlMode == SqlProvisioningMode.Provision && string.IsNullOrWhiteSpace(options.SqlServerName))
        {
            yield return names.SqlServerName;
        }

        yield return names.ResolverFunctionAppName;
        yield return names.WebAppName;
    }

    private static void AddResourceNamePostfixParameter(List<string> arguments, DeploymentNames names)
    {
        // Nothing is passed without a postfix, so those deployments' parameters stay as before.
        if (names.ResourceNamePostfix is { } postfix)
        {
            arguments.Add($"resourceNamePostfix={postfix}");
        }
    }

    /// <summary>
    /// Records the legacy naming options on the resource group so later runs and the other
    /// commands use the same names. Only merged, never deleted: dropping a recorded value
    /// would let the next run deploy a second set of resources.
    /// </summary>
    private async Task RecordResourceNamesAsync(ResourceGroupInfo resourceGroup, string? postfix, string? funcStorageAccountName, CancellationToken cancellationToken)
    {
        var merge = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddIfChanged(merge, resourceGroup.Tags, ResourceNaming.PostfixTag, postfix);
        AddIfChanged(merge, resourceGroup.Tags, ResourceNaming.FuncStorageAccountTag, funcStorageAccountName);
        if (merge.Count == 0)
        {
            return;
        }

        await _az.EnsureSuccessAsync(
            TagUpdateArguments(resourceGroup.Id, "Merge", merge),
            cancellationToken,
            "Could not record the resource naming options as tags on the resource group; an Azure Policy may forbid tag changes there. " +
            $"Allow the {ResourceNaming.PostfixTag} and {ResourceNaming.FuncStorageAccountTag} tags, or pass the same naming options to every command.").ConfigureAwait(false);

        static void AddIfChanged(Dictionary<string, string> merge, IReadOnlyDictionary<string, string> current, string tag, string? value)
        {
            if (value is not null && !(current.TryGetValue(tag, out var existing) && string.Equals(existing, value, StringComparison.Ordinal)))
            {
                merge[tag] = value;
            }
        }
    }

    /// <summary>
    /// SKU, kind and firewall of a reused function storage account, so the deployment keeps
    /// them instead of applying the template defaults (a kind change to StorageV2 cannot be
    /// undone, and the defaults would replace the account's network rules). Null only when the
    /// name is free, so the template creates the account. Any other failure to read it stops
    /// the run: assuming "new" would redeploy an existing account with the defaults.
    /// </summary>
    private async Task<ExistingStorageAccount?> DiscoverFuncStorageAccountAsync(string resourceGroupName, string accountName, CancellationToken cancellationToken)
    {
        // The generic resource view returns networkAcls in the ARM shape the template takes.
        var result = await _az.TryRunAsync(
            new[]
            {
                "resource", "show",
                "--resource-group", resourceGroupName,
                "--name", accountName,
                "--resource-type", "Microsoft.Storage/storageAccounts",
                "--query", "{sku:sku.name, kind:kind, networkAcls:properties.networkAcls}",
                "--output", "json",
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            await EnsureFuncStorageAccountNameIsFreeAsync(resourceGroupName, accountName, result.StandardError, cancellationToken).ConfigureAwait(false);
            return null;
        }

        string? sku = null;
        string? kind = null;
        string? networkAcls = null;
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                sku = root.TryGetProperty("sku", out var skuElement) && skuElement.ValueKind == JsonValueKind.String ? skuElement.GetString() : null;
                kind = root.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String ? kindElement.GetString() : null;
                networkAcls = root.TryGetProperty("networkAcls", out var aclElement) && aclElement.ValueKind == JsonValueKind.Object
                    ? aclElement.GetRawText()
                    : null;
            }
        }
        catch (JsonException)
        {
            // Reported below as an unreadable account.
        }

        if (string.IsNullOrWhiteSpace(sku) || string.IsNullOrWhiteSpace(kind))
        {
            throw new CommandException(
                $"Could not read the SKU and kind of the storage account '{accountName}' in '{resourceGroupName}'. " +
                "A reused account keeps them, so nothing was deployed.");
        }

        if (string.Equals(kind, "BlobStorage", StringComparison.OrdinalIgnoreCase) || string.Equals(kind, "FileStorage", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "BlockBlobStorage", StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandException(
                $"The storage account '{accountName}' is of kind '{kind}', which cannot host a function app (it needs blob, queue, table and file storage). " +
                "Use a general-purpose account (StorageV2 or Storage) for --func-storage-account-name.");
        }

        // A Premium general-purpose account holds page blobs only: no queues or tables.
        if (sku.StartsWith("Premium_", StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandException(
                $"The storage account '{accountName}' has the SKU '{sku}', which has no queue or table storage and cannot host a function app. " +
                "Use a Standard general-purpose account for --func-storage-account-name.");
        }

        CliOutput.WriteLine($"Keeping the existing storage account '{accountName}' ({sku}, {kind}) and its network rules for the Resolver.");
        return new ExistingStorageAccount(sku, kind, networkAcls);
    }

    /// <summary>
    /// The account could not be read in the deployment resource group. Creating it is safe only
    /// when the globally unique name is free; a taken name means the account is in another
    /// resource group or subscription, or this identity cannot read it.
    /// </summary>
    private async Task EnsureFuncStorageAccountNameIsFreeAsync(string resourceGroupName, string accountName, string showError, CancellationToken cancellationToken)
    {
        var check = await _az.TryRunAsync(
            new[]
            {
                "storage", "account", "check-name",
                "--name", accountName,
                "--query", "{available:nameAvailable, reason:reason}",
                "--output", "json",
            },
            cancellationToken).ConfigureAwait(false);

        bool? available = null;
        string? reason = null;
        if (check.Succeeded)
        {
            try
            {
                using var document = JsonDocument.Parse(check.StandardOutput);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    available = root.TryGetProperty("available", out var availableElement) && availableElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? availableElement.GetBoolean()
                        : null;
                    reason = root.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
                        ? reasonElement.GetString()
                        : null;
                }
            }
            catch (JsonException)
            {
                // Reported below as an unknown outcome.
            }
        }

        if (available == true)
        {
            return;
        }

        var detail = string.Join(
            Environment.NewLine,
            new[] { showError, check.StandardError }.Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => text.Trim()));
        var message = available == false
            ? $"The storage account name '{accountName}' is taken ({reason ?? "no reason given"}), but the account could not be read in resource group '{resourceGroupName}'. " +
                "It is in another resource group or subscription, or this identity cannot read it; --func-storage-account-name reuses an account in the deployment resource group only."
            : $"Could not tell whether the storage account '{accountName}' exists, so nothing was deployed.";
        throw new CommandException(detail.Length > 0 ? message + Environment.NewLine + detail : message);
    }

    private sealed record ServiceBusDeployment(string? Sku, int? Capacity, string? NamespaceName);

    private sealed record ResolvedNetwork(
        bool AllowPublicAccess,
        string PrivateEndpointSubnetId,
        string ResolverSubnetId,
        string WebAppSubnetId,
        string PrivateEndpointLocation,
        PrivateDnsModeChoice DnsMode,
        string? DnsZoneScope,
        IReadOnlyList<string> DnsLinkVnetIds);
}
