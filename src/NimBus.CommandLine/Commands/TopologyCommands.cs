using McMaster.Extensions.CommandLineUtils;
using NimBus.Core.Events;
using NimBus.Core;
using NimBus.MessageStore;
using Spectre.Console;
using CoreAsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;
using static NimBus.CommandLine.CliHelpers;

namespace NimBus.CommandLine.Commands;

/// <summary>Registers the <c>nb topology</c> command group.</summary>
internal static class TopologyCommands
{
    internal static void Register(CommandLineApplication app, CommandOption sbConnectionString)
    {
        app.Command("topology", topologyCommand =>
        {
            topologyCommand.Description = "Export or apply the NimBus Service Bus topology.";
            topologyCommand.HelpOption(inherited: true);

            topologyCommand.Command("export", exportCommand =>
            {
                exportCommand.Description = "Export the current PlatformConfiguration to JSON.";
                exportCommand.HelpOption(inherited: true);

                var output = exportCommand.Option("-o|--output <PATH>", "Output path. Defaults to platform-config.json in the current directory.", CommandOptionType.SingleValue);
                var exportAssembly = exportCommand.Option("-a|--assembly <PATH>",
                    "Host assembly exposing a public parameterless IPlatform; default is the built-in platform.",
                    CommandOptionType.SingleValue);
                var exportPackage = exportCommand.Option("--platform-package <ID@VERSION>",
                    "NuGet package containing your IPlatform catalog, e.g. Acme.Contracts@1.4.0.",
                    CommandOptionType.SingleValue);
                var exportFeed = exportCommand.Option("--platform-feed <URL>",
                    $"Feed serving --platform-package (default: {PlatformPackage.FeedEnvironmentVariable}, else the artifact feed, else nuget.org).",
                    CommandOptionType.SingleValue);
                var exportPlatformType = exportCommand.Option("--platform <TYPE>",
                    "IPlatform type name when the assembly or package exposes more than one",
                    CommandOptionType.SingleValue);

                exportCommand.OnExecuteAsync(async cancellationToken =>
                {
                    var outputPath = output.HasValue() ? output.Value()! : Path.Combine(Environment.CurrentDirectory, "platform-config.json");
                    var factory = await ResolvePlatformFactoryAsync(
                        exportAssembly.Value(), exportPackage.Value(), exportFeed.Value(), exportPlatformType.Value(), cancellationToken).ConfigureAwait(false);
                    await PlatformConfigExporter.ExportAsync(outputPath, cancellationToken, factory).ConfigureAwait(false);
                    return 0;
                });
            });

            topologyCommand.Command("apply", applyCommand =>
            {
                applyCommand.Description = "Provision the Service Bus topology for the current PlatformConfiguration.";
                applyCommand.HelpOption(inherited: true);
                applyCommand.AddOption(sbConnectionString);

                var solutionId = applyCommand.Option("--solution-id <ID>", "Solution identifier used in Azure resource names (required without --sb-connection-string).", CommandOptionType.SingleValue);
                var environment = applyCommand.Option("--environment <NAME>", "Environment name used in Azure resource names (required without --sb-connection-string).", CommandOptionType.SingleValue);
                var resourceGroup = applyCommand.Option("--resource-group <NAME>", "Azure resource group containing the Service Bus namespace (required without --sb-connection-string).", CommandOptionType.SingleValue);
                var storageProvider = applyCommand.Option("--storage-provider <PROVIDER>", "Storage provider for NimBus message persistence: cosmos | sqlserver. Controls whether the per-endpoint Cosmos containers are provisioned. Defaults to 'cosmos'.", CommandOptionType.SingleValue);
                var topologyAssembly = applyCommand.Option("-a|--assembly <PATH>",
                    "Host assembly exposing a public parameterless IPlatform; default is the built-in platform. Required to provision a catalog that is not compiled into this CLI.",
                    CommandOptionType.SingleValue);
                var topologyPackage = applyCommand.Option("--platform-package <ID@VERSION>",
                    "NuGet package containing your IPlatform catalog, e.g. Acme.Contracts@1.4.0. Resolved from --platform-feed; use this instead of --assembly to provision without a local build.",
                    CommandOptionType.SingleValue);
                var topologyFeed = applyCommand.Option("--platform-feed <URL>",
                    $"Feed serving --platform-package (default: {PlatformPackage.FeedEnvironmentVariable}, else the artifact feed, else nuget.org).",
                    CommandOptionType.SingleValue);
                var topologyPlatform = applyCommand.Option("--platform <TYPE>",
                    "IPlatform type name when the assembly or package exposes more than one",
                    CommandOptionType.SingleValue);
                var topologyNamespaceName = applyCommand.Option("--service-bus-namespace-name <NAME>",
                    "Override the Service Bus namespace name (default: 'sb-{solution-id}-{environment}'). Use the value passed to 'nb infra apply'.",
                    CommandOptionType.SingleValue);
                var topologyPostfix = applyCommand.Option(ResourceNaming.PostfixOptionTemplate, ResourceNaming.PostfixLookupOptionDescription, CommandOptionType.SingleValue);
                var topologyDnsWait = applyCommand.Option(NetworkSelection.DnsWaitOptionTemplate, NetworkSelection.DnsWaitOptionDescription, CommandOptionType.SingleValue);

                applyCommand.OnExecuteAsync(async cancellationToken =>
                {
                    if (!string.IsNullOrWhiteSpace(topologyNamespaceName.Value()))
                    {
                        NetworkSelection.ValidateServiceBusNamespaceName(topologyNamespaceName.Value()!.Trim());
                    }

                    _ = ResourceNaming.NormalizePostfix(topologyPostfix.Value());
                    var dnsWait = NetworkSelection.ParseDnsWaitOption(topologyDnsWait.Value());
                    var az = new AzureCliRunner();
                    var platformFactory = await ResolvePlatformFactoryAsync(
                        topologyAssembly.Value(), topologyPackage.Value(), topologyFeed.Value(), topologyPlatform.Value(), cancellationToken).ConfigureAwait(false);
                    var suppliedConnection = sbConnectionString.Value()
                        ?? Environment.GetEnvironmentVariable(CommandRunner.SbConnectionStringEnvName);
                    ServiceBusTopologyProvisioner provisioner;
                    TopologyOptions options;
                    var runsAgainstResourceGroup = string.IsNullOrWhiteSpace(suppliedConnection);
                    if (!runsAgainstResourceGroup)
                    {
                        provisioner = new ServiceBusTopologyProvisioner(az, suppliedConnection!, platformFactory);
                        options = new TopologyOptions(string.Empty, string.Empty, string.Empty);
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(solutionId.Value()) ||
                            string.IsNullOrWhiteSpace(environment.Value()) ||
                            string.IsNullOrWhiteSpace(resourceGroup.Value()))
                        {
                            throw new InvalidOperationException(
                                "Provide --sb-connection-string (or AzureServiceBus_ConnectionString), or all of --solution-id, --environment, and --resource-group.");
                        }

                        provisioner = new ServiceBusTopologyProvisioner(az, platformFactory);
                        options = new TopologyOptions(solutionId.Value()!, environment.Value()!, resourceGroup.Value()!, topologyNamespaceName.Value(), dnsWait, topologyPostfix.Value());
                    }

                    await provisioner.ApplyAsync(options, cancellationToken).ConfigureAwait(false);

                    // Per-endpoint Cosmos containers go through the control plane here
                    // because the deployed apps' Entra data-plane tokens cannot create
                    // containers lazily. Connection-string runs have no resource group
                    // to target, and the SQL provider has no per-endpoint containers.
                    if (runsAgainstResourceGroup && ParseStorageProvider(storageProvider.Value()) == StorageProviderChoice.Cosmos)
                    {
                        await new EndpointContainerProvisioner(az, platformFactory).ApplyAsync(options, cancellationToken).ConfigureAwait(false);
                    }

                    return 0;
                });
            });
        });
    }
}
