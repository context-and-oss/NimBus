using McMaster.Extensions.CommandLineUtils;
using NimBus.Core.Events;
using NimBus.Core;
using NimBus.MessageStore;
using Spectre.Console;
using CoreAsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;
using static NimBus.CommandLine.CliHelpers;

namespace NimBus.CommandLine.Commands;

/// <summary>Registers the <c>nb setup</c> command group.</summary>
internal static class SetupCommand
{
    internal static void Register(CommandLineApplication app)
    {
        app.Command("setup", setupCommand =>
        {
            setupCommand.Description = "Run infrastructure deployment, topology provisioning, and app deployment in sequence.";
            setupCommand.HelpOption(inherited: true);

            var solutionId = setupCommand.Option("--solution-id <ID>", "Solution identifier used in Azure resource names.", CommandOptionType.SingleValue).IsRequired();
            var environment = setupCommand.Option("--environment <NAME>", "Environment name used in Azure resource names.", CommandOptionType.SingleValue).IsRequired();
            var resourceGroup = setupCommand.Option("--resource-group <NAME>", "Azure resource group name.", CommandOptionType.SingleValue).IsRequired();
            var repoRoot = setupCommand.Option("--repo-root <PATH>", "Repository root for a source build. Implies --from-source.", CommandOptionType.SingleValue);
            var setupFromSource = setupCommand.Option("--from-source", "Build the applications from a repository clone instead of deploying the published release artifacts.", CommandOptionType.NoValue);
            var location = setupCommand.Option("--location <AZURE-REGION>", "Azure region for new resources. Defaults to the resource group's region; existing resources keep theirs.", CommandOptionType.SingleValue);
            var resourceNamePostfix = setupCommand.Option("--resource-name-postfix <VALUE>", "Reserved for compatibility with the legacy pipeline scripts.", CommandOptionType.SingleValue);
            var webAppVersion = setupCommand.Option("--webapp-version <VALUE>", "Version string stored in the web app settings.", CommandOptionType.SingleValue);
            var configuration = setupCommand.Option("--configuration <NAME>", "Build configuration passed to dotnet publish.", CommandOptionType.SingleValue);
            var setupResolverPlan = setupCommand.Option("--resolver-plan <PLAN>", "Hosting plan for the resolver Function App: ElasticPremium | FlexConsumption. Defaults to the existing plan when one is deployed, otherwise 'FlexConsumption' (FC1, scale-to-zero Linux).", CommandOptionType.SingleValue);
            var setupResolverMaxSessions = setupCommand.Option("--resolver-max-sessions <N>", "Resolver Service Bus session concurrency per instance (1-200). Defaults to the template value (16). Applied as a template-owned host override.", CommandOptionType.SingleValue);
            var setupResolverMaxInstances = setupCommand.Option("--resolver-max-instances <N>", "Resolver Function App instance ceiling. Elastic Premium: 0 (no cap, default) to 10, applied as functionAppScaleLimit. Flex Consumption: 1-1000, applied as maximumInstanceCount (default 100).", CommandOptionType.SingleValue);
            var setupManagementPlanSku = setupCommand.Option("--management-plan-sku <SKU>", "SKU for the management App Service Plan hosting the WebApp. Defaults to the existing plan's SKU when one is deployed, otherwise 'B1' for dev/development and 'S1' for other environments.", CommandOptionType.SingleValue);
            var setupStorageProvider = setupCommand.Option("--storage-provider <PROVIDER>", "Storage provider for NimBus message persistence: cosmos | sqlserver. Defaults to 'cosmos' for backwards compatibility.", CommandOptionType.SingleValue);
            var setupSqlMode = setupCommand.Option("--sql-mode <MODE>", $"When --storage-provider is sqlserver: 'provision' deploys a new Azure SQL resource; 'external' reads the connection string from {DeploymentSecrets.SqlConnectionStringEnvironmentVariable}.", CommandOptionType.SingleValue);
            var setupSqlAdminLogin = setupCommand.Option("--sql-admin-login <VALUE>", "SQL admin login when --sql-mode is 'provision'.", CommandOptionType.SingleValue);
            var setupSqlServerName = setupCommand.Option("--sql-server-name <NAME>", "Override the SQL server name (default: 'sql-{solution-id}-{environment}'). Use this when the default DNS name is held in Azure's global namespace from a recent delete (24-72h cooldown).", CommandOptionType.SingleValue);
            var setupIdentityAdminEmail = setupCommand.Option("--identity-admin-email <EMAIL>", "When using --storage-provider sqlserver, enables username/password sign-in and seeds this email as the first admin on first boot.", CommandOptionType.SingleValue);
            var setupAssembly = setupCommand.Option("-a|--assembly <PATH>",
                "Host assembly exposing a public parameterless IPlatform; default is the built-in platform. Required to provision a catalog that is not compiled into this CLI.",
                CommandOptionType.SingleValue);
            var setupPackage = setupCommand.Option("--platform-package <ID@VERSION>",
                "NuGet package containing your IPlatform catalog, e.g. Acme.Contracts@1.4.0. Provisions your topology and deploys the catalog with the WebApp — no local build required.",
                CommandOptionType.SingleValue);
            var setupFeed = setupCommand.Option("--platform-feed <URL>",
                $"Feed serving --platform-package (default: {PlatformPackage.FeedEnvironmentVariable}, else the artifact feed, else nuget.org).",
                CommandOptionType.SingleValue);
            var setupPlatform = setupCommand.Option("--platform <TYPE>",
                "IPlatform type name when the assembly or package exposes more than one",
                CommandOptionType.SingleValue);
            var setupNetworkOptions = NetworkCommandOptions.Register(setupCommand);

            setupCommand.OnExecuteAsync(async cancellationToken =>
            {
                var context = CommandContext.Create(repoRoot.Value());
                var az = new AzureCliRunner();
                // Parse the capacity and network options before the platform package
                // download so a malformed value fails before any network work.
                var setupResolverMaxSessionsValue = PlanSelection.ParseResolverMaxSessionsOption(setupResolverMaxSessions.Value());
                var setupResolverMaxInstancesValue = PlanSelection.ParseResolverMaxInstancesOption(setupResolverMaxInstances.Value());
                var setupNetwork = setupNetworkOptions.Build();
                var setupServiceBusCapacity = setupNetworkOptions.ServiceBusCapacity;
                var setupDnsWait = setupNetworkOptions.DnsWait;
                NetworkSelection.ValidateSyntax(setupNetwork);
                var setupPlatformPackage = setupPackage.HasValue()
                    ? await PlatformPackage.ResolveAsync(PlatformHttpClient, setupPackage.Value()!, setupFeed.Value(), setupPlatform.Value(), cancellationToken).ConfigureAwait(false)
                    : null;
                var setupPlatformFactory = setupPlatformPackage?.CreateFactory()
                    ?? PlatformLoader.CreateFactory(setupAssembly.Value(), setupPlatform.Value());
                var infra = new InfrastructureDeployer(context, az);
                var topology = new ServiceBusTopologyProvisioner(az, setupPlatformFactory);
                var apps = new AppDeploymentService(az, DeploymentArtifactSource.Create(
                    context,
                    setupFromSource.HasValue(),
                    repoRoot.HasValue(),
                    configuration.Value(),
                    solutionId.Value()!,
                    environment.Value()!), setupPlatformPackage);

                var providerChoice = ParseStorageProvider(setupStorageProvider.Value());
                var sqlProvisioningMode = ParseSqlMode(setupSqlMode.Value());
                var secrets = DeploymentSecrets.Load();

                if (providerChoice == StorageProviderChoice.SqlServer)
                {
                    if (sqlProvisioningMode == SqlProvisioningMode.External && string.IsNullOrWhiteSpace(secrets.SqlConnectionString))
                        throw new InvalidOperationException($"Environment variable '{DeploymentSecrets.SqlConnectionStringEnvironmentVariable}' is required when --sql-mode is 'external'.");
                    if (sqlProvisioningMode == SqlProvisioningMode.Provision &&
                        (string.IsNullOrWhiteSpace(setupSqlAdminLogin.Value()) || string.IsNullOrWhiteSpace(secrets.SqlAdminPassword)))
                        throw new InvalidOperationException($"--sql-admin-login and environment variable '{DeploymentSecrets.SqlAdminPasswordEnvironmentVariable}' are required when --sql-mode is 'provision'.");
                }

                if (!string.IsNullOrWhiteSpace(setupIdentityAdminEmail.Value()) && string.IsNullOrWhiteSpace(secrets.IdentityAdminPassword))
                    throw new InvalidOperationException($"Environment variable '{DeploymentSecrets.IdentityAdminPasswordEnvironmentVariable}' is required when --identity-admin-email is set.");
                if (!string.IsNullOrWhiteSpace(setupIdentityAdminEmail.Value()) && providerChoice != StorageProviderChoice.SqlServer)
                    throw new InvalidOperationException("--identity-admin-email requires --storage-provider sqlserver.");

                var infraOptions = new InfrastructureOptions(
                    solutionId.Value()!,
                    environment.Value()!,
                    resourceGroup.Value()!,
                    resourceNamePostfix.Value(),
                    location.Value(),
                    webAppVersion.HasValue() ? webAppVersion.Value()! : $"local-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    providerChoice,
                    sqlProvisioningMode,
                    secrets.SqlConnectionString,
                    setupSqlAdminLogin.Value(),
                    secrets.SqlAdminPassword,
                    setupSqlServerName.Value(),
                    PlanSelection.ParseResolverPlanOption(setupResolverPlan.Value()),
                    setupIdentityAdminEmail.Value(),
                    secrets.IdentityAdminPassword,
                    setupManagementPlanSku.Value(),
                    ResolverMaxConcurrentSessions: setupResolverMaxSessionsValue,
                    ResolverMaxInstances: setupResolverMaxInstancesValue,
                    Network: setupNetwork,
                    ServiceBusCapacity: setupServiceBusCapacity,
                    ServiceBusNamespaceName: setupNetworkOptions.ServiceBusNamespaceName,
                    DnsWait: setupDnsWait);

                var topologyOptions = new TopologyOptions(solutionId.Value()!, environment.Value()!, resourceGroup.Value()!, setupNetworkOptions.ServiceBusNamespaceName, setupDnsWait);
                var appOptions = new AppDeploymentOptions(
                    solutionId.Value()!,
                    environment.Value()!,
                    resourceGroup.Value()!,
                    configuration.HasValue() ? configuration.Value()! : "Release",
                    DnsWait: setupDnsWait);

                await infra.ApplyAsync(infraOptions, cancellationToken).ConfigureAwait(false);
                await topology.ApplyAsync(topologyOptions, cancellationToken).ConfigureAwait(false);

                // See the topology apply command: managed identity cannot create the
                // per-endpoint Cosmos containers lazily, so provision them here.
                if (providerChoice == StorageProviderChoice.Cosmos)
                {
                    await new EndpointContainerProvisioner(az, setupPlatformFactory).ApplyAsync(topologyOptions, cancellationToken).ConfigureAwait(false);
                }

                await apps.DeployAsync(appOptions, cancellationToken).ConfigureAwait(false);
                return 0;
            });
        });
    }
}
