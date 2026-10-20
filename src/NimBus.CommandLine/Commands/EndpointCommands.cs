using McMaster.Extensions.CommandLineUtils;
using NimBus.Core.Events;
using NimBus.Core;
using NimBus.MessageStore;
using Spectre.Console;
using CoreAsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;
using static NimBus.CommandLine.CliHelpers;

namespace NimBus.CommandLine.Commands;

/// <summary>Registers the <c>nb endpoint</c> command group.</summary>
internal static class EndpointCommands
{
    internal static void Register(CommandLineApplication app, CommandOption sbConnectionString, CommandOption dbConnectionString)
    {
        app.Command("endpoint", endpointCommand =>
        {
            endpointCommand.Description = "Manage Service Bus endpoints, sessions, and topics.";

            endpointCommand.OnExecute(() =>
            {
                AnsiConsole.MarkupLine("[yellow]Specify a subcommand[/]");
                endpointCommand.ShowHelp();
                return 1;
            });

            endpointCommand.Command("session", sessionCommand =>
            {
                sessionCommand.OnExecute(() =>
                {
                    AnsiConsole.MarkupLine("[yellow]Specify a subcommand[/]");
                    sessionCommand.ShowHelp();
                    return 1;
                });

                sessionCommand.Command("delete", deleteSessionCommand =>
                {
                    deleteSessionCommand.Description = "Deletes messages on a session and its session state";

                    var endpointName = deleteSessionCommand.Argument("endpoint-name", "Name of endpoint (required)").IsRequired();
                    var sessionId = deleteSessionCommand.Argument("session", "Session id (required)").IsRequired();

                    deleteSessionCommand.AddOption(sbConnectionString);
                    deleteSessionCommand.AddOption(dbConnectionString);

                    deleteSessionCommand.OnExecuteAsync(async ct =>
                    {
                        await CommandRunner.Run(sbConnectionString, dbConnectionString, (sbClient, dbClient, sbAdmin) => Endpoint.DeleteSession(sbClient, dbClient, endpointName, sessionId));
                        Console.WriteLine($"Endpoint '{endpointName.Value}' is ready.");
                    });
                });
            });

            endpointCommand.Command("topics", topicsCommand =>
            {
                topicsCommand.OnExecute(() =>
                {
                    AnsiConsole.MarkupLine("[yellow]Specify a subcommand[/]");
                    topicsCommand.ShowHelp();
                    return 1;
                });

                topicsCommand.Command("removeDeprecated", removeDeprecatedCommand =>
                {
                    removeDeprecatedCommand.Description = "Deletes deprecated topics and the underlying subscriptions and rules from the service bus";

                    var endpointName = removeDeprecatedCommand.Argument("endpoint-name", "Name of endpoint (required)").IsRequired();

                    removeDeprecatedCommand.AddOption(sbConnectionString);

                    removeDeprecatedCommand.OnExecuteAsync(async ct =>
                    {
                        await CommandRunner.Run(sbConnectionString, dbConnectionString, (sbClient, dbClient, sbAdmin) => Endpoint.RemoveDeprecated(sbAdmin, endpointName));
                    });
                });
            });

            endpointCommand.Command("purge", purgeCommand =>
            {
                purgeCommand.Description = "Purges messages from a Service Bus subscription by state and/or enqueued time";

                var endpointName = purgeCommand.Argument("endpoint-name", "Name of endpoint (required)").IsRequired();
                var subscriptionOption = purgeCommand.Option("--subscription <NAME>", "Subscription name (defaults to endpoint name)", CommandOptionType.SingleValue);
                var stateOption = purgeCommand.Option("--state <STATE>", "Comma-separated message states to purge: Active, Deferred (default: all)", CommandOptionType.SingleValue);
                var beforeOption = purgeCommand.Option("--before <UTC_DATETIME>", "Only purge messages enqueued before this UTC datetime (e.g. 2026-03-01T00:00:00)", CommandOptionType.SingleValue);
                purgeCommand.AddOption(sbConnectionString);

                purgeCommand.OnExecuteAsync(async ct =>
                {
                    var validStates = new[] { "active", "deferred" };
                    var stateFilters = new List<string>();

                    if (stateOption.HasValue())
                    {
                        foreach (var raw in stateOption.Value()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!validStates.Contains(raw, StringComparer.OrdinalIgnoreCase))
                            {
                                Console.Error.WriteLine($"Invalid state '{raw}'. Valid values: Active, Deferred");
                                return 1;
                            }
                            stateFilters.Add(raw.ToLower());
                        }
                    }

                    DateTime? before = null;
                    if (beforeOption.HasValue())
                    {
                        if (!DateTime.TryParse(beforeOption.Value(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedDate))
                        {
                            Console.Error.WriteLine($"Invalid date '{beforeOption.Value()}'. Expected format: yyyy-MM-ddTHH:mm:ss (UTC)");
                            return 1;
                        }
                        before = parsedDate;
                    }

                    string subscription = subscriptionOption.HasValue() ? subscriptionOption.Value()! : endpointName.Value!;

                    await CommandRunner.Run(sbConnectionString, (sbClient) =>
                        Endpoint.PurgeSubscription(sbClient, endpointName.Value!, subscription, stateFilters, before));
                    return 0;
                });
            });
        });
    }
}
