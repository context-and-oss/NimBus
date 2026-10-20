using Azure.Messaging.ServiceBus;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.States;
using NimBus.ServiceBus;
using Spectre.Console;

namespace NimBus.CommandLine;

static class Container
{
    private const string DatabaseId = "MessageDatabase";
    private const string MessagesContainer = "messages";

    public static async Task DeleteDocuments(CosmosDbClient dbClient, CommandArgument nameArg, List<string> statuses)
    {
        await RemoveMessagesFromCosmosDB(dbClient, nameArg.Value!, statuses);
    }

    public static async Task DeleteDocument(CosmosDbClient dbClient, CommandArgument nameArg, CommandArgument eventIdArg)
    {
        await RemoveMessageFromCosmosDB(dbClient, nameArg.Value!, eventIdArg.Value!);
    }

    public static async Task ResubmitMessages(ServiceBusClient serviceBusClient, CosmosDbClient dbClient, CommandArgument nameArg)
    {
        await UpdateMessagesAndResubmit(serviceBusClient, dbClient, nameArg.Value!, ResolutionStatus.Failed);
    }

    public static async Task SkipMessages(CosmosDbClient dbClient, CommandArgument nameArg, List<string> statuses, DateTime? before)
    {
        await UpdateMessagesAsSkipped(dbClient, nameArg.Value!, statuses, before);
    }

    public static async Task DeleteMessages(CosmosClient cosmosClient, string to)
    {
        var container = cosmosClient.GetDatabase(DatabaseId).GetContainer(MessagesContainer);

        var conditions = new List<string> { "c.message.To = @to" };
        var query = new QueryDefinition("SELECT * FROM c WHERE " + string.Join(" AND ", conditions))
            .WithParameter("@to", to);

        int count = 0;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Deleting messages...", async ctx =>
            {
                using var iterator = container.GetItemQueryIterator<JObject>(query);

                while (iterator.HasMoreResults)
                {
                    var batch = await iterator.ReadNextAsync();

                    foreach (var doc in batch)
                    {
                        var id = doc["id"]?.ToString();
                        var eventId = doc["eventId"]?.ToString();
                        if (id == null || eventId == null)
                            continue;

                        await container.DeleteItemAsync<JObject>(id, new PartitionKey(eventId));
                        count++;

                        ctx.Status($"Deleting messages... ({count} deleted)");
                        AnsiConsole.MarkupLine($"[dim]Deleted message {id.EscapeMarkup()}[/]");
                    }
                }
            });

        AnsiConsole.MarkupLine($"[green]Deleted {count} message(s) with To={to.EscapeMarkup()}[/]");
    }

    public static async Task CopyEndpointData(CosmosClient sourceClient, CosmosClient targetClient, CommandArgument endpointNameArg, DateTime? from, DateTime? to, List<string> statuses, int? batchSize = null)
    {
        var endpointName = endpointNameArg.Value!;
        CosmosContainerDefaults.EnsureNotReservedEndpointId(endpointName);

        var sourceDb = sourceClient.GetDatabase(DatabaseId);
        var targetDb = targetClient.GetDatabase(DatabaseId);

        var sourceEndpointContainer = sourceDb.GetContainer(endpointName);
        // Endpoint containers must be created with TTL enabled or the item-level ttl values
        // written by CosmosDbClient.UploadMessage are inert in the target — and
        // CreateContainerIfNotExistsAsync never reconciles a container it did not create.
        var targetEndpointContainer =
            (await targetDb.CreateContainerIfNotExistsAsync(CosmosContainerDefaults.EndpointContainer(endpointName))).Container;

        var copiedEventIds = new HashSet<string>();
        var eventCount = await CopyDocuments(
            sourceEndpointContainer,
            targetEndpointContainer,
            BuildEventQuery(from, to, statuses),
            "event",
            doc =>
            {
                var eventId = doc["event"]?["EventId"]?.ToString();
                if (eventId != null)
                    copiedEventIds.Add(eventId);
                return doc["id"]?.ToString() ?? "unknown";
            },
            batchSize);

        var sourceMessagesContainer = sourceDb.GetContainer(MessagesContainer);
        var targetMessagesContainer = (await targetDb.CreateContainerIfNotExistsAsync(MessagesContainer, "/eventId")).Container;

        var messageCount = await CopyDocuments(
            sourceMessagesContainer,
            targetMessagesContainer,
            BuildMessageQuery(endpointName, from, to),
            "message",
            doc => doc["id"]?.ToString() ?? "unknown",
            batchSize,
            copiedEventIds);

        AnsiConsole.MarkupLine($"[green]Copy complete: {eventCount} events + {messageCount} messages[/]");
    }

    static async Task RemoveMessagesFromCosmosDB(CosmosDbClient dbClient, string endpoint, List<string> statuses)
    {
        AnsiConsole.MarkupLine("[blue]Removing messages in CosmosDB...[/]");

        string? continuationToken = string.Empty;
        int maxSearchItemsCount = 20;
        SearchResponse searchResponse;

        do
        {
            AnsiConsole.MarkupLine("[blue]Retrieving messages[/]");
            searchResponse = await dbClient.GetEventsByFilter(new EventFilter() { EndPointId = endpoint, ResolutionStatus = statuses }, continuationToken, maxSearchItemsCount);
            continuationToken = searchResponse.ContinuationToken;
            AnsiConsole.MarkupLine($"[blue]Retrieved {searchResponse.Events.Count()} messages[/]");

            foreach (var @event in searchResponse.Events)
            {
                AnsiConsole.MarkupLine($"[dim]Removing message with EventId {@event.EventId.EscapeMarkup()} from DB[/]");
                bool messageRemovedFromDb = await dbClient.RemoveMessage(@event.EventId, @event.SessionId!, endpoint);
                if (messageRemovedFromDb)
                {
                    AnsiConsole.MarkupLine("[green]Removed message from DB[/]");
                }
            }
        }
        while (continuationToken != null);
    }

    static async Task UpdateMessagesAndResubmit(ServiceBusClient serviceBusClient, CosmosDbClient dbClient, string endpoint, ResolutionStatus resolutionStatus)
    {
        var sender = new Sender(serviceBusClient.CreateSender(Constants.ManagerId));

        AnsiConsole.MarkupLine("[blue]Updating status for messages in CosmosDB...[/]");

        string? continuationToken = string.Empty;
        int maxSearchItemsCount = 20;
        SearchResponse searchResponse;

        do
        {
            AnsiConsole.MarkupLine("[blue]Retrieving messages[/]");
            searchResponse = await dbClient.GetEventsByFilter(new EventFilter() { EndPointId = endpoint, ResolutionStatus = new List<string> { resolutionStatus.ToString() } }, continuationToken, maxSearchItemsCount);
            continuationToken = searchResponse.ContinuationToken;
            AnsiConsole.MarkupLine($"[blue]Retrieved {searchResponse.Events.Count()} messages[/]");

            foreach (var @event in searchResponse.Events)
            {
                if (@event.UpdatedAt < DateTime.UtcNow.AddMinutes(-10))
                {
                    @event.ResolutionStatus = resolutionStatus;
                    @event.MessageType = MessageType.EventRequest;
                    AnsiConsole.MarkupLine($"[dim]Updating message with EventId {@event.EventId.EscapeMarkup()} from DB[/]");
                    bool messageUpdated = await dbClient.UploadFailedMessage(@event.EventId, @event.SessionId!, endpoint, @event);
                    if (messageUpdated)
                    {
                        AnsiConsole.MarkupLine($"[green]Updated message as Failed[/]");
                        await Resubmit(sender, @event, @event.EndpointId, @event.EventTypeId!, @event.MessageContent.EventContent!.EventJson);
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]Message {@event.EventId.EscapeMarkup()} is not old enough[/]");
                }
            }
        }
        while (continuationToken != null);
    }

    static Task Resubmit(Sender sender, UnresolvedEvent errorResponse, string endpoint, string eventTypeId, string? eventJson)
    {
        AnsiConsole.MarkupLine($"[dim]MANAGER RESUBMIT EVENT: EventId: {errorResponse.EventId.EscapeMarkup()} EventtypeId: {eventTypeId.EscapeMarkup()}[/]");
        return sender.Send(new Message
        {
            CorrelationId = errorResponse.CorrelationId,
            EventId = errorResponse.EventId,
            SessionId = errorResponse.SessionId!,
            To = endpoint,
            OriginatingMessageId = errorResponse.OriginatingMessageId,
            ParentMessageId = errorResponse.LastMessageId,
            MessageType = MessageType.ResubmissionRequest,
            EventTypeId = eventTypeId,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = eventJson
                }
            },
        });
    }

    static async Task UpdateMessagesAsSkipped(CosmosDbClient dbClient, string endpoint, List<string> resolutionStatus, DateTime? before)
    {
        if (before.HasValue)
            AnsiConsole.MarkupLine($"[blue]Skipping messages updated before {before.Value:u} in CosmosDB...[/]");
        else
            AnsiConsole.MarkupLine("[blue]Skipping messages in CosmosDB...[/]");

        string? continuationToken = string.Empty;
        int maxSearchItemsCount = 20;
        SearchResponse searchResponse;

        do
        {
            AnsiConsole.MarkupLine("[blue]Retrieving messages[/]");
            searchResponse = await dbClient.GetEventsByFilter(new EventFilter() { EndPointId = endpoint, ResolutionStatus = resolutionStatus }, continuationToken, maxSearchItemsCount);
            continuationToken = searchResponse.ContinuationToken;
            AnsiConsole.MarkupLine($"[blue]Retrieved {searchResponse.Events.Count()} messages[/]");

            foreach (var @event in searchResponse.Events)
            {
                if (before.HasValue && @event.UpdatedAt >= before.Value)
                {
                    AnsiConsole.MarkupLine($"[yellow]Skipping message {@event.EventId.EscapeMarkup()} - updated at {@event.UpdatedAt:u}, not before cutoff[/]");
                    continue;
                }

                AnsiConsole.MarkupLine($"[dim]Skipping message with EventId {@event.EventId.EscapeMarkup()}[/]");
                bool messageUpdated = await dbClient.UploadSkippedMessage(@event.EventId, @event.SessionId!, endpoint, @event);
                if (messageUpdated)
                {
                    AnsiConsole.MarkupLine($"[green]Skipped message {@event.EventId.EscapeMarkup()}[/]");
                }
            }
        }
        while (continuationToken != null);
    }

    static async Task RemoveMessageFromCosmosDB(CosmosDbClient dbClient, string endpoint, string eventId)
    {
        AnsiConsole.MarkupLine("[blue]Removing messages in CosmosDB...[/]");

        string? continuationToken = string.Empty;
        int maxSearchItemsCount = 20;
        SearchResponse searchResponse;

        do
        {
            AnsiConsole.MarkupLine("[blue]Retrieving messages[/]");
            searchResponse = await dbClient.GetEventsByFilter(new EventFilter() { EndPointId = endpoint, EventId = eventId }, continuationToken, maxSearchItemsCount);
            continuationToken = searchResponse.ContinuationToken;
            AnsiConsole.MarkupLine($"[blue]Retrieved {searchResponse.Events.Count()} messages[/]");

            foreach (var @event in searchResponse.Events)
            {
                AnsiConsole.MarkupLine($"[dim]Removing message with EventId {@event.EventId.EscapeMarkup()} from DB[/]");
                bool messageRemovedFromDb = await dbClient.RemoveMessage(@event.EventId, @event.SessionId!, endpoint);
                if (messageRemovedFromDb)
                {
                    AnsiConsole.MarkupLine("[green]Removed message from DB[/]");
                }
            }
        }
        while (continuationToken != null);
    }

    static QueryDefinition BuildEventQuery(DateTime? from, DateTime? to, List<string> statuses)
    {
        var conditions = new List<string> { "(NOT IS_DEFINED(c.deleted) OR c.deleted != true)" };

        if (from.HasValue)
            conditions.Add("c.event.EnqueuedTimeUtc >= @from");
        if (to.HasValue)
            conditions.Add("c.event.EnqueuedTimeUtc <= @to");
        if (statuses.Count > 0)
            conditions.Add("ARRAY_CONTAINS(@statuses, c.status)");

        var query = new QueryDefinition("SELECT * FROM c WHERE " + string.Join(" AND ", conditions));

        if (from.HasValue)
            query = query.WithParameter("@from", from.Value.ToString("O"));
        if (to.HasValue)
            query = query.WithParameter("@to", to.Value.ToString("O"));
        if (statuses.Count > 0)
            query = query.WithParameter("@statuses", statuses);

        return query;
    }

    static QueryDefinition BuildMessageQuery(string endpointId, DateTime? from, DateTime? to)
    {
        var conditions = new List<string> { "c.endpointId = @endpointId" };

        if (from.HasValue)
            conditions.Add("c.message.EnqueuedTimeUtc >= @from");
        if (to.HasValue)
            conditions.Add("c.message.EnqueuedTimeUtc <= @to");

        var query = new QueryDefinition("SELECT * FROM c WHERE " + string.Join(" AND ", conditions))
            .WithParameter("@endpointId", endpointId);

        if (from.HasValue)
            query = query.WithParameter("@from", from.Value.ToString("O"));
        if (to.HasValue)
            query = query.WithParameter("@to", to.Value.ToString("O"));

        return query;
    }

    static async Task<int> CopyDocuments(
        Microsoft.Azure.Cosmos.Container source,
        Microsoft.Azure.Cosmos.Container target,
        QueryDefinition query,
        string label,
        Func<JObject, string> getDocId,
        int? batchSize = null,
        HashSet<string>? eventIdFilter = null)
    {
        int count = 0;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Copying {label}s...", async ctx =>
            {
                using var iterator = source.GetItemQueryIterator<JObject>(query);

                while (iterator.HasMoreResults)
                {
                    var batch = await iterator.ReadNextAsync();

                    foreach (var doc in batch)
                    {
                        if (eventIdFilter != null)
                        {
                            var evtId = doc["eventId"]?.ToString();
                            if (evtId == null || !eventIdFilter.Contains(evtId))
                                continue;
                        }

                        doc.Remove("ttl");

                        var id = getDocId(doc);
                        await target.UpsertItemAsync(doc);
                        count++;

                        ctx.Status($"Copying {label}s... ({count} copied)");
                        AnsiConsole.MarkupLine($"[dim]Copied {label} {id.EscapeMarkup()}[/]");

                        if (batchSize.HasValue && count >= batchSize.Value)
                            break;
                    }

                    if (batchSize.HasValue && count >= batchSize.Value)
                        break;
                }
            });

        AnsiConsole.MarkupLine($"[blue]Copied {count} {label}(s)[/]");
        return count;
    }
}
