using McMaster.Extensions.CommandLineUtils;
using NimBus.Core.Events;
using NimBus.Core;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using Spectre.Console;
using CoreAsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;
using static NimBus.CommandLine.CliHelpers;

namespace NimBus.CommandLine.Commands;

/// <summary>Registers the <c>nb container</c> command group.</summary>
internal static class ContainerCommands
{
    internal static void Register(CommandLineApplication app, CommandOption sbConnectionString, CommandOption dbConnectionString, CommandOption unresolvedRetentionDays)
    {
        app.Command("container", containerCommand =>
        {
            containerCommand.Description = "Manage Cosmos DB containers, events, and messages.";

            containerCommand.OnExecute(() =>
            {
                AnsiConsole.MarkupLine("[yellow]Specify a subcommand[/]");
                containerCommand.ShowHelp();
                return 1;
            });

            containerCommand.Command("event", eventCommand =>
            {
                eventCommand.OnExecute(() =>
                {
                    AnsiConsole.MarkupLine("[yellow]Specify a subcommand[/]");
                    eventCommand.ShowHelp();
                    return 1;
                });

                eventCommand.Command("delete", deleteEventCommand =>
                {
                    deleteEventCommand.Description = "Deletes a message from Cosmos DB";

                    deleteEventCommand.AddOption(dbConnectionString);

                    var endpointName = deleteEventCommand.Argument("endpoint-name", "Name of endpoint (required)").IsRequired();
                    var eventId = deleteEventCommand.Argument("event-id", "Id of event (required)").IsRequired();

                    deleteEventCommand.OnExecuteAsync(async ct =>
                    {
                        await CommandRunner.Run(dbConnectionString, (dbClient) => Container.DeleteDocument(dbClient, endpointName, eventId));
                    });
                });
            });

            containerCommand.Command("message", messageGroupCommand =>
            {
                messageGroupCommand.OnExecute(() =>
                {
                    AnsiConsole.MarkupLine("[yellow]Specify a subcommand[/]");
                    messageGroupCommand.ShowHelp();
                    return 1;
                });

                messageGroupCommand.Command("delete", deleteMessagesCommand =>
                {
                    deleteMessagesCommand.Description = "Deletes messages from the messages container filtered by the To field";

                    var toArg = deleteMessagesCommand.Argument("to", "Value of the To field to filter on (e.g. CrmEndpoint)").IsRequired();
                    deleteMessagesCommand.AddOption(dbConnectionString);

                    deleteMessagesCommand.OnExecuteAsync(async ct =>
                    {
                        var connStr = dbConnectionString.HasValue() ? dbConnectionString.Value() : Environment.GetEnvironmentVariable(CommandRunner.DbConnectionStringEnvName);
                        if (string.IsNullOrEmpty(connStr))
                        {
                            Console.Error.WriteLine($"Cosmos DB connection string is required. Use -dbc or set environment variable '{CommandRunner.DbConnectionStringEnvName}'.");
                            return 1;
                        }

                        using var cosmosClient = CommandRunner.CreateCosmosClient(connStr);
                        await Container.DeleteMessages(cosmosClient, toArg.Value!);
                        return 0;
                    });
                });
            });

            containerCommand.Command("delete", deleteCommand =>
            {
                deleteCommand.Description = "Deletes messages in Cosmos DB by status";

                var endpointName = deleteCommand.Argument("endpoint-name", "Name of endpoint (required)").IsRequired();
                var statusOption = deleteCommand.Option("-s|--status <STATUS>", "Comma-separated list of statuses to delete (e.g. failed,deadlettered). Default: deadlettered", CommandOptionType.SingleValue);
                deleteCommand.AddOption(dbConnectionString);

                deleteCommand.OnExecuteAsync(async ct =>
                {
                    var statuses = new List<string>();
                    if (statusOption.HasValue())
                    {
                        foreach (var raw in statusOption.Value()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!Enum.TryParse<ResolutionStatus>(raw, ignoreCase: true, out var status))
                            {
                                Console.Error.WriteLine($"Invalid status '{raw}'. Valid values: {string.Join(", ", Enum.GetNames<ResolutionStatus>())}");
                                return 1;
                            }
                            statuses.Add(status.ToString());
                        }
                    }
                    else
                    {
                        statuses.Add(ResolutionStatus.DeadLettered.ToString());
                    }

                    await CommandRunner.Run(dbConnectionString, (dbClient) => Container.DeleteDocuments(dbClient, endpointName, statuses));
                    return 0;
                });
            });

            containerCommand.Command("resubmit", resubmitCommand =>
            {
                resubmitCommand.Description = "Updates messages in Cosmos DB and resubmit messages";

                var endpointName = resubmitCommand.Argument("endpoint-name", "Name of endpoint (required)").IsRequired();
                resubmitCommand.AddOption(sbConnectionString);
                resubmitCommand.AddOption(dbConnectionString);
                // Resubmission rewrites the tracking document, so it is the one command that
                // would otherwise reset a bounded retention back to unlimited.
                resubmitCommand.AddOption(unresolvedRetentionDays);

                resubmitCommand.OnExecuteAsync(async ct =>
                {
                    await CommandRunner.Run(sbConnectionString, dbConnectionString, (sbClient, dbClient) => Container.ResubmitMessages(sbClient, dbClient, endpointName), unresolvedRetentionDays);
                });
            });

            containerCommand.Command("copy", copyCommand =>
            {
                copyCommand.Description = "Copies endpoint data (events + messages) from one Cosmos DB to another";

                var endpointName = copyCommand.Argument("endpoint-name", "Name of endpoint (required)").IsRequired();
                copyCommand.AddOption(dbConnectionString);

                var targetDbConnectionString = new CommandOption("--target-dbc|--target-db-connection-string", CommandOptionType.SingleValue)
                {
                    Description = "Target Cosmos DB connection string (required)"
                };
                targetDbConnectionString.IsRequired();
                copyCommand.AddOption(targetDbConnectionString);

                var fromOption = copyCommand.Option("--from <UTC_DATETIME>", "Only copy events from this UTC datetime (e.g. 2026-03-01T00:00:00)", CommandOptionType.SingleValue);
                var toOption = copyCommand.Option("--to <UTC_DATETIME>", "Only copy events up to this UTC datetime (e.g. 2026-03-20T00:00:00)", CommandOptionType.SingleValue);
                var statusOption = copyCommand.Option("-s|--status <STATUS>", "Comma-separated list of statuses to copy (e.g. failed,deferred). Default: all", CommandOptionType.SingleValue);
                var batchSizeOption = copyCommand.Option("-b|--batch-size <SIZE>", "Number of documents to copy per batch (default: all)", CommandOptionType.SingleValue);

                copyCommand.OnExecuteAsync(async ct =>
                {
                    var statuses = new List<string>();
                    if (statusOption.HasValue())
                    {
                        foreach (var raw in statusOption.Value()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!Enum.TryParse<ResolutionStatus>(raw, ignoreCase: true, out var status))
                            {
                                Console.Error.WriteLine($"Invalid status '{raw}'. Valid values: {string.Join(", ", Enum.GetNames<ResolutionStatus>())}");
                                return 1;
                            }
                            statuses.Add(status.ToString());
                        }
                    }

                    DateTime? from = null;
                    if (fromOption.HasValue())
                    {
                        if (!DateTime.TryParse(fromOption.Value(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedFrom))
                        {
                            Console.Error.WriteLine($"Invalid date '{fromOption.Value()}'. Expected format: yyyy-MM-ddTHH:mm:ss (UTC)");
                            return 1;
                        }
                        from = parsedFrom;
                    }

                    DateTime? to = null;
                    if (toOption.HasValue())
                    {
                        if (!DateTime.TryParse(toOption.Value(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedTo))
                        {
                            Console.Error.WriteLine($"Invalid date '{toOption.Value()}'. Expected format: yyyy-MM-ddTHH:mm:ss (UTC)");
                            return 1;
                        }
                        to = parsedTo;
                    }

                    int? batchSize = null;
                    if (batchSizeOption.HasValue())
                    {
                        if (!int.TryParse(batchSizeOption.Value(), out var parsedBatch) || parsedBatch <= 0)
                        {
                            Console.Error.WriteLine($"Invalid batch size '{batchSizeOption.Value()}'. Must be a positive integer.");
                            return 1;
                        }
                        batchSize = parsedBatch;
                    }

                    await CommandRunner.Run(dbConnectionString, targetDbConnectionString, (sourceClient, targetClient) =>
                        Container.CopyEndpointData(sourceClient, targetClient, endpointName, from, to, statuses, batchSize));
                    return 0;
                });
            });

            containerCommand.Command("skip", skipCommand =>
            {
                skipCommand.Description = "Sends a skip to the endpoint for each matching event, so the sessions they block continue";

                var endpointName = skipCommand.Argument("endpoint-name", "Name of endpoint (required)").IsRequired();
                var statusOption = skipCommand.Option("-s|--status <STATUS>", "Comma-separated list of source statuses to skip: failed, deadlettered, unsupported", CommandOptionType.SingleValue);
                statusOption.IsRequired();
                var beforeOption = skipCommand.Option("--before <UTC_DATETIME>", "Only skip messages last updated before this UTC datetime (e.g. 2026-03-01T00:00:00)", CommandOptionType.SingleValue);
                skipCommand.AddOption(sbConnectionString);
                skipCommand.AddOption(dbConnectionString);

                skipCommand.OnExecuteAsync(async ct =>
                {
                    var parsed = new List<string>();

                    foreach (var raw in statusOption.Value()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!Enum.TryParse<ResolutionStatus>(raw, ignoreCase: true, out var status))
                        {
                            Console.Error.WriteLine($"Invalid status '{raw}'. Valid values: {string.Join(", ", Enum.GetNames<ResolutionStatus>())}");
                            return 1;
                        }
                        if (!SkipRules.BulkSkipSourceStatuses.Contains(status))
                        {
                            Console.Error.WriteLine($"Cannot skip '{status}' events; a skip only settles {string.Join(", ", SkipRules.BulkSkipSourceStatuses)} events.");
                            return 1;
                        }
                        parsed.Add(status.ToString());
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

                    await CommandRunner.Run(sbConnectionString, dbConnectionString, (sbClient, dbClient) =>
                        Container.SkipMessages(sbClient, dbClient, endpointName, parsed, before));
                    return 0;
                });
            });
        });
    }
}
