using McMaster.Extensions.CommandLineUtils;
using NimBus.Core.Events;
using NimBus.Core;
using NimBus.MessageStore;
using Spectre.Console;
using CoreAsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;
using static NimBus.CommandLine.CliHelpers;

namespace NimBus.CommandLine.Commands;

/// <summary>Registers the <c>nb infra</c> command group.</summary>
internal static class InfraCommands
{
    internal static void Register(CommandLineApplication app)
    {
        app.Command("infra", infraCommand =>
        {
            infraCommand.Description = "Deploy Azure infrastructure using the repository bicep definitions.";
            infraCommand.HelpOption(inherited: true);

            infraCommand.Command("apply", applyCommand =>
            {
                applyCommand.Description = "Deploy the core and web app infrastructure resources.";
                applyCommand.HelpOption(inherited: true);

                var solutionId = applyCommand.Option("--solution-id <ID>", "Solution identifier used in Azure resource names.", CommandOptionType.SingleValue).IsRequired();
                var environment = applyCommand.Option("--environment <NAME>", "Environment name used in Azure resource names.", CommandOptionType.SingleValue).IsRequired();
                var resourceGroup = applyCommand.Option("--resource-group <NAME>", "Azure resource group name.", CommandOptionType.SingleValue).IsRequired();
                var repoRoot = applyCommand.Option("--repo-root <PATH>", "Repository root. Defaults to the current directory or a parent directory containing deploy/ and src/.", CommandOptionType.SingleValue);
                var location = applyCommand.Option("--location <AZURE-REGION>", "Azure region for new resources. Defaults to the resource group's region; existing resources keep theirs.", CommandOptionType.SingleValue);
                var resourceNamePostfix = applyCommand.Option("--resource-name-postfix <VALUE>", "Reserved for compatibility with the legacy pipeline scripts.", CommandOptionType.SingleValue);
                var webAppVersion = applyCommand.Option("--webapp-version <VALUE>", "Version string stored in the web app settings.", CommandOptionType.SingleValue);
                var storageProvider = applyCommand.Option("--storage-provider <PROVIDER>", "Storage provider for NimBus message persistence: cosmos | sqlserver. Defaults to 'cosmos' for backwards compatibility.", CommandOptionType.SingleValue);
                var sqlMode = applyCommand.Option("--sql-mode <MODE>", $"When --storage-provider is sqlserver: 'provision' deploys a new Azure SQL resource; 'external' reads the connection string from {DeploymentSecrets.SqlConnectionStringEnvironmentVariable}.", CommandOptionType.SingleValue);
                var sqlAdminLogin = applyCommand.Option("--sql-admin-login <VALUE>", "SQL admin login when --sql-mode is 'provision'.", CommandOptionType.SingleValue);
                var sqlServerName = applyCommand.Option("--sql-server-name <NAME>", "Override the SQL server name (default: 'sql-{solution-id}-{environment}'). Use this when the default DNS name is held in Azure's global namespace from a recent delete (24-72h cooldown).", CommandOptionType.SingleValue);
                var resolverPlan = applyCommand.Option("--resolver-plan <PLAN>", "Hosting plan for the resolver Function App: ElasticPremium | FlexConsumption. Defaults to the existing plan when one is deployed, otherwise 'FlexConsumption' (FC1, scale-to-zero Linux). 'ElasticPremium' (EP1, Windows) remains available.", CommandOptionType.SingleValue);
                var resolverMaxSessions = applyCommand.Option("--resolver-max-sessions <N>", "Resolver Service Bus session concurrency per instance (1-200). Defaults to the template value (16). Applied as a template-owned host override.", CommandOptionType.SingleValue);
                var resolverMaxInstances = applyCommand.Option("--resolver-max-instances <N>", "Resolver Function App instance ceiling. Elastic Premium: 0 (no cap, default) to 10, applied as functionAppScaleLimit. Flex Consumption: 1-1000, applied as maximumInstanceCount (default 100).", CommandOptionType.SingleValue);
                var managementPlanSku = applyCommand.Option("--management-plan-sku <SKU>", "SKU for the management App Service Plan hosting the WebApp. Defaults to the existing plan's SKU when one is deployed, otherwise 'B1' for dev/development and 'S1' for other environments.", CommandOptionType.SingleValue);
                var networkOptions = NetworkCommandOptions.Register(applyCommand);

                applyCommand.OnExecuteAsync(async cancellationToken =>
                {
                    var context = CommandContext.Create(repoRoot.Value());
                    var az = new AzureCliRunner();
                    var deployer = new InfrastructureDeployer(context, az);

                    var providerChoice = ParseStorageProvider(storageProvider.Value());
                    var sqlProvisioningMode = ParseSqlMode(sqlMode.Value());
                    var resolverPlanChoice = PlanSelection.ParseResolverPlanOption(resolverPlan.Value());
                    var resolverMaxSessionsValue = PlanSelection.ParseResolverMaxSessionsOption(resolverMaxSessions.Value());
                    var resolverMaxInstancesValue = PlanSelection.ParseResolverMaxInstancesOption(resolverMaxInstances.Value());
                    var network = networkOptions.Build();
                    var serviceBusCapacity = networkOptions.ServiceBusCapacity;
                    var secrets = DeploymentSecrets.Load();

                    if (providerChoice == StorageProviderChoice.SqlServer)
                    {
                        if (sqlProvisioningMode == SqlProvisioningMode.External && string.IsNullOrWhiteSpace(secrets.SqlConnectionString))
                            throw new InvalidOperationException($"Environment variable '{DeploymentSecrets.SqlConnectionStringEnvironmentVariable}' is required when --sql-mode is 'external'.");
                        if (sqlProvisioningMode == SqlProvisioningMode.Provision &&
                            (string.IsNullOrWhiteSpace(sqlAdminLogin.Value()) || string.IsNullOrWhiteSpace(secrets.SqlAdminPassword)))
                            throw new InvalidOperationException($"--sql-admin-login and environment variable '{DeploymentSecrets.SqlAdminPasswordEnvironmentVariable}' are required when --sql-mode is 'provision'.");
                    }

                    var options = new InfrastructureOptions(
                        solutionId.Value()!,
                        environment.Value()!,
                        resourceGroup.Value()!,
                        resourceNamePostfix.Value(),
                        location.Value(),
                        webAppVersion.HasValue() ? webAppVersion.Value()! : $"local-{DateTime.UtcNow:yyyyMMddHHmmss}",
                        providerChoice,
                        sqlProvisioningMode,
                        secrets.SqlConnectionString,
                        sqlAdminLogin.Value(),
                        secrets.SqlAdminPassword,
                        sqlServerName.Value(),
                        resolverPlanChoice,
                        ManagementPlanSku: managementPlanSku.Value(),
                        ResolverMaxConcurrentSessions: resolverMaxSessionsValue,
                        ResolverMaxInstances: resolverMaxInstancesValue,
                        Network: network,
                        ServiceBusCapacity: serviceBusCapacity,
                        ServiceBusNamespaceName: networkOptions.ServiceBusNamespaceName,
                        DnsWait: networkOptions.DnsWait);

                    await deployer.ApplyAsync(options, cancellationToken).ConfigureAwait(false);
                    return 0;
                });
            });
        });
    }
}
