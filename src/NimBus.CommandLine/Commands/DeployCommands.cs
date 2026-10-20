using McMaster.Extensions.CommandLineUtils;
using NimBus.Core.Events;
using NimBus.Core;
using NimBus.MessageStore;
using Spectre.Console;
using CoreAsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;
using static NimBus.CommandLine.CliHelpers;

namespace NimBus.CommandLine.Commands;

/// <summary>Registers the <c>nb deploy</c> command group.</summary>
internal static class DeployCommands
{
    internal static void Register(CommandLineApplication app)
    {
        app.Command("deploy", deployCommand =>
        {
            deployCommand.Description = "Build and deploy NimBus applications.";
            deployCommand.HelpOption(inherited: true);

            deployCommand.Command("apps", appsCommand =>
            {
                appsCommand.Description = "Deploy the resolver and web app to Azure from the published release artifacts (or from source with --from-source).";
                appsCommand.HelpOption(inherited: true);

                var solutionId = appsCommand.Option("--solution-id <ID>", "Solution identifier used in Azure resource names.", CommandOptionType.SingleValue).IsRequired();
                var environment = appsCommand.Option("--environment <NAME>", "Environment name used in Azure resource names.", CommandOptionType.SingleValue).IsRequired();
                var resourceGroup = appsCommand.Option("--resource-group <NAME>", "Azure resource group containing the target apps.", CommandOptionType.SingleValue).IsRequired();
                var repoRoot = appsCommand.Option("--repo-root <PATH>", "Repository root for a source build. Implies --from-source.", CommandOptionType.SingleValue);
                var fromSource = appsCommand.Option("--from-source", "Build the applications from a repository clone instead of deploying the published release artifacts.", CommandOptionType.NoValue);
                var configuration = appsCommand.Option("--configuration <NAME>", "Build configuration passed to dotnet publish. Source builds only.", CommandOptionType.SingleValue);
                var only = appsCommand.Option("--only <APP>", "Deploy a single application: resolver | webapp. Defaults to both.", CommandOptionType.SingleValue);
                var appsDnsWait = appsCommand.Option(NetworkSelection.DnsWaitOptionTemplate, NetworkSelection.DnsWaitOptionDescription, CommandOptionType.SingleValue);
                var appsPackage = appsCommand.Option("--platform-package <ID@VERSION>",
                    "NuGet package containing your IPlatform catalog, e.g. Acme.Contracts@1.4.0. Its assemblies are deployed with the WebApp so Endpoints, Event Types and PII masking show your platform instead of the built-in one.",
                    CommandOptionType.SingleValue);
                var appsFeed = appsCommand.Option("--platform-feed <URL>",
                    $"Feed serving --platform-package (default: {PlatformPackage.FeedEnvironmentVariable}, else the artifact feed, else nuget.org).",
                    CommandOptionType.SingleValue);
                var appsPlatformType = appsCommand.Option("--platform <TYPE>",
                    "IPlatform type name when the package exposes more than one",
                    CommandOptionType.SingleValue);

                appsCommand.OnExecuteAsync(async cancellationToken =>
                {
                    var context = CommandContext.Create(repoRoot.Value());
                    var az = new AzureCliRunner();
                    var platformPackage = appsPackage.HasValue()
                        ? await PlatformPackage.ResolveAsync(PlatformHttpClient, appsPackage.Value()!, appsFeed.Value(), appsPlatformType.Value(), cancellationToken).ConfigureAwait(false)
                        : null;
                    var deployer = new AppDeploymentService(az, DeploymentArtifactSource.Create(
                        context,
                        fromSource.HasValue(),
                        repoRoot.HasValue(),
                        configuration.Value(),
                        solutionId.Value()!,
                        environment.Value()!), platformPackage);
                    var options = new AppDeploymentOptions(
                        solutionId.Value()!,
                        environment.Value()!,
                        resourceGroup.Value()!,
                        configuration.HasValue() ? configuration.Value()! : "Release",
                        DeployTargetSelection.ParseOnlyOption(only.Value()),
                        NetworkSelection.ParseDnsWaitOption(appsDnsWait.Value()));

                    await deployer.DeployAsync(options, cancellationToken).ConfigureAwait(false);
                    return 0;
                });
            });
        });
    }
}
