using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using McMaster.Extensions.CommandLineUtils;
using NimBus.CommandLine.Models;
using NimBus.Core;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.States;
using NimBus.ServiceBus.Provisioning;
using Spectre.Console;

namespace NimBus.CommandLine;

static class Endpoint
{
    public static async Task DeleteSession(ServiceBusClient serviceBusClient, CosmosDbClient dbClient, CommandArgument nameArg, CommandArgument sessionIdArg)
    {
        string sessionId = sessionIdArg.Value!;
        string endpoint = nameArg.Value!;

        Console.WriteLine($"Waiting to accept session {sessionId}");

        var receiver = await serviceBusClient.AcceptSessionAsync(endpoint, endpoint, sessionId);

        Console.WriteLine($"Get service bus session retriever");

        int numberOfMessages = await RemoveActiveMessagesFromServiceBus(receiver);
        numberOfMessages += await RemoveDeferredMessagesFromServiceBus(receiver);

        await RemoveMessagesFromCosmosDB(dbClient, sessionId, endpoint);

        Console.WriteLine($"Clear session state for session {sessionId}");
        await receiver.SetSessionStateAsync(null);
    }

    public static async Task RemoveDeprecated(ServiceBusAdministrationClient sbAdmin, CommandArgument nameArg)
    {
        string endpointName = nameArg.Value!.ToLower();

        var topic = (await sbAdmin.GetTopicAsync(endpointName)).Value;

        var expectedTopic = GetExpectedTopic(endpointName);
        var expectedTree = TopicToTree(expectedTopic);
        AnsiConsole.Write("Expected topics/subscriptions/rules \n");
        AnsiConsole.Write(expectedTree);
        AnsiConsole.WriteLine();

        var actualTopic = await GetActualTopic(sbAdmin, endpointName);

        var isDeprecatedTopic = GetIsDeprecatedTopic(expectedTopic, actualTopic);
        var isDeprecatedTree = TopicToTree(isDeprecatedTopic);
        AnsiConsole.Write("Actual topics/subscriptions/rules (red will be deleted) \n");
        AnsiConsole.Write(isDeprecatedTree);
        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm("Do you want to remove the marked topics and rules?"))
            return;

        await DeleteDeprecated(sbAdmin, endpointName, isDeprecatedTopic);
    }

    public static async Task PurgeSubscription(ServiceBusClient client, string topicName, string subscriptionName, List<string> stateFilters, DateTime? before)
    {
        bool purgeActive = stateFilters.Count == 0 || stateFilters.Contains("active");
        bool purgeDeferred = stateFilters.Count == 0 || stateFilters.Contains("deferred");

        var filterParts = new List<string>();
        if (stateFilters.Count > 0) filterParts.Add($"state: {string.Join(", ", stateFilters)}");
        if (before.HasValue) filterParts.Add($"enqueued before {before.Value:u}");
        var filterDesc = filterParts.Count > 0 ? string.Join("; ", filterParts) : "[red]no filter (ALL messages)[/]";

        AnsiConsole.MarkupLine($"[blue]Topic:[/] {topicName.EscapeMarkup()}");
        AnsiConsole.MarkupLine($"[blue]Subscription:[/] {subscriptionName.EscapeMarkup()}");
        AnsiConsole.MarkupLine($"[blue]Filter:[/] {filterDesc}");

        AnsiConsole.MarkupLine("[blue]Scanning messages...[/]");

        await using var peekReceiver = client.CreateReceiver(topicName, subscriptionName);
        var sessionMessages = new Dictionary<string, List<(long SequenceNumber, ServiceBusMessageState State)>>();
        long fromSequenceNumber = 0;
        int totalScanned = 0;

        while (true)
        {
            var peeked = await peekReceiver.PeekMessagesAsync(100, fromSequenceNumber);
            if (peeked.Count == 0) break;

            foreach (var msg in peeked)
            {
                totalScanned++;
                bool stateMatch = (purgeActive && msg.State == ServiceBusMessageState.Active)
                               || (purgeDeferred && msg.State == ServiceBusMessageState.Deferred);

                if (stateMatch && (!before.HasValue || msg.EnqueuedTime.UtcDateTime < before.Value))
                {
                    var sid = msg.SessionId ?? "";
                    if (!sessionMessages.ContainsKey(sid))
                        sessionMessages[sid] = new();
                    sessionMessages[sid].Add((msg.SequenceNumber, msg.State));
                }
            }

            fromSequenceNumber = peeked[peeked.Count - 1].SequenceNumber + 1;
        }

        int totalMatching = sessionMessages.Values.Sum(v => v.Count);
        AnsiConsole.MarkupLine($"[blue]Scanned {totalScanned} messages, {totalMatching} match the filter across {sessionMessages.Count} sessions[/]");

        if (totalMatching == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No messages match the filter[/]");
            return;
        }

        if (!AnsiConsole.Confirm($"Purge {totalMatching} messages?"))
            return;

        int totalCompleted = 0;

        foreach (var (sid, messages) in sessionMessages)
        {
            ServiceBusSessionReceiver sessionReceiver;
            try
            {
                sessionReceiver = await client.AcceptSessionAsync(topicName, subscriptionName, sid);
            }
            catch (ServiceBusException ex)
            {
                AnsiConsole.MarkupLine($"  [red]Failed to accept session '{sid.EscapeMarkup()}': {ex.Message.EscapeMarkup()}[/]");
                continue;
            }

            try
            {
                AnsiConsole.MarkupLine($"\n[blue]Session '{sid.EscapeMarkup()}'[/]");
                int sessionCompleted = 0;

                var deferredSeqNums = messages
                    .Where(m => m.State == ServiceBusMessageState.Deferred)
                    .Select(m => m.SequenceNumber)
                    .ToList();

                foreach (var seqNum in deferredSeqNums)
                {
                    try
                    {
                        var msg = await sessionReceiver.ReceiveDeferredMessageAsync(seqNum);
                        if (msg != null)
                        {
                            await sessionReceiver.CompleteMessageAsync(msg);
                            sessionCompleted++;
                            AnsiConsole.MarkupLine($"  [dim]Completed deferred message (seq: {seqNum})[/]");
                        }
                    }
                    catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessageNotFound)
                    {
                        // Already completed or expired
                    }
                }

                if (messages.Any(m => m.State == ServiceBusMessageState.Active))
                {
                    while (true)
                    {
                        var received = await sessionReceiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(5));
                        if (received.Count == 0) break;

                        bool anyCompleted = false;
                        foreach (var msg in received)
                        {
                            if (!before.HasValue || msg.EnqueuedTime.UtcDateTime < before.Value)
                            {
                                await sessionReceiver.CompleteMessageAsync(msg);
                                sessionCompleted++;
                                anyCompleted = true;
                                AnsiConsole.MarkupLine($"  [dim]Completed active message (id: {msg.MessageId.EscapeMarkup()})[/]");
                            }
                            else
                            {
                                await sessionReceiver.AbandonMessageAsync(msg);
                            }
                        }

                        if (!anyCompleted) break;
                    }
                }

                totalCompleted += sessionCompleted;

                if (sessionCompleted > 0)
                    AnsiConsole.MarkupLine($"  [green]Completed {sessionCompleted} messages[/]");
                else
                    AnsiConsole.MarkupLine($"  [dim]No messages completed[/]");
            }
            finally
            {
                await sessionReceiver.DisposeAsync();
            }
        }

        AnsiConsole.MarkupLine($"\n[green]Purge complete. Total messages removed: {totalCompleted}[/]");
    }

    internal static TopicDto GetIsDeprecatedTopic(TopicDto expectedTopic, TopicDto actualTopic)
    {
        var expectedRules = expectedTopic.Subscriptions.SelectMany(x => x.Rules);

        foreach (var subscription in actualTopic.Subscriptions)
        {
            subscription.IsDeprecated = !expectedTopic.Subscriptions.Contains(subscription, comparer: new SubscriptionDto.SubscriptionDtoComparer());

            foreach (var rule in subscription.Rules)
            {
                rule.IsDeprecated = !expectedRules.Contains(rule, comparer: new RuleDto.RuleDtoComparer());
            }
        }
        return actualTopic;
    }

    static async Task DeleteDeprecated(ServiceBusAdministrationClient sbAdmin, string topicName, TopicDto topic)
    {
        var deprecatedRules = topic.Subscriptions.SelectMany(x => x.Rules).Where(x => x.IsDeprecated);
        var deprecatedSubscriptions = topic.Subscriptions.Where(x => x.IsDeprecated);

        await AnsiConsole.Progress()
            .AutoRefresh(true)
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn(),
            })
            .StartAsync(async ctx =>
            {
                var deleteRulesTask = ctx.AddTask("[green]Deleting rules[/]");
                var deleteSubscriptionsTask = ctx.AddTask("[green]Deleting subscriptions[/]");

                while (!ctx.IsFinished)
                {
                    if (deprecatedRules.Any())
                    {
                        var ruleIncrements = (double)(100M / deprecatedRules.Count());
                        foreach (var rule in deprecatedRules)
                        {
                            await sbAdmin.DeleteRuleAsync(topicName, rule.SubscriptionName, rule.Name);
                            deleteRulesTask.Increment(ruleIncrements);
                        }
                    }
                    else
                    {
                        deleteRulesTask.Increment(100);
                    }

                    if (deprecatedSubscriptions.Any())
                    {
                        var subscriptionIncrements = (double)(100M / deprecatedSubscriptions.Count());
                        foreach (var sub in deprecatedSubscriptions)
                        {
                            await sbAdmin.DeleteSubscriptionAsync(topicName, sub.Name);
                            deleteSubscriptionsTask.Increment(subscriptionIncrements);
                        }
                    }
                    else
                    {
                        deleteSubscriptionsTask.Increment(100);
                    }
                }
            });
    }

    static Tree TopicToTree(TopicDto topic)
    {
        var root = new Tree(topic.Name);

        var subscriptionNodes = topic.Subscriptions.Select(x =>
        {
            var subscriptionText = x.IsDeprecated ? $"[red]{x.Name}[/]" : x.Name;
            var subscriptionNode = new TreeNode(new Markup(subscriptionText));

            var ruleNodes = x.Rules.Select(y =>
            {
                var ruleText = y.IsDeprecated ? $"[red]{y.Name}[/]" : y.Name;
                return new TreeNode(new Markup(ruleText));
            });

            subscriptionNode.AddNodes(ruleNodes);
            return subscriptionNode;
        });
        root.AddNodes(subscriptionNodes);

        return root;
    }

    static TopicDto GetExpectedTopic(string endpointName) =>
        GetExpectedTopic(endpointName, new PlatformConfiguration());

    internal static TopicDto GetExpectedTopic(string endpointName, IPlatform platform)
    {
        // The expected shape comes from TopologyDescriptor — the same declaration the
        // provisioner lays down and the WebApp rebuilds from. Deriving it a second time
        // here is how the "{endpoint}-reply" subscription came to be reported deprecated:
        // the provisioner creates it for request/reply, this list never mentioned it, and
        // `topology remove-deprecated` deleted it.
        //
        // GetActualTopic lowercases everything it reads back from the broker, so the
        // descriptor's names are lowercased to match.
        var endpoint = platform.Endpoints.First(x => x.Name.ToLower() == endpointName);
        var topicName = endpointName.ToLowerInvariant();

        return new TopicDto
        {
            Name = topicName,
            Subscriptions = TopologyDescriptor.ForEndpointTopic(endpoint, platform)
                .Select(subscription =>
                {
                    var subscriptionName = subscription.Name.ToLowerInvariant();
                    return new SubscriptionDto
                    {
                        Name = subscriptionName,
                        TopicName = topicName,
                        Rules = subscription.Rules
                            .Select(rule => new RuleDto
                            {
                                Name = rule.Name.ToLowerInvariant(),
                                SubscriptionName = subscriptionName,
                            })
                            .ToList(),
                    };
                })
                .ToList(),
        };
    }

    static async Task<TopicDto> GetActualTopic(ServiceBusAdministrationClient sbAdmin, string endpointName)
    {
        var subscriptionsAsync = sbAdmin.GetSubscriptionsAsync(endpointName);
        var topic = new TopicDto { Name = endpointName, Subscriptions = new List<SubscriptionDto>() };

        await foreach (var page in subscriptionsAsync.AsPages())
        {
            var subscriptions = page.Values
                .Select(x => new SubscriptionDto
                {
                    Name = x.SubscriptionName.ToLower(),
                    TopicName = x.TopicName.ToLower(),
                    Rules = new List<RuleDto>()
                })
                .ToList();
            topic.Subscriptions.AddRange(subscriptions);
        }

        foreach (var subscription in topic.Subscriptions)
        {
            var rulesAsync = sbAdmin.GetRulesAsync(endpointName, subscription.Name);

            await foreach (var page in rulesAsync.AsPages())
            {
                var rules = page.Values
                    .Select(x => new RuleDto { Name = x.Name.ToLower(), SubscriptionName = subscription.Name.ToLower() })
                    .ToArray();
                subscription.Rules.AddRange(rules);
            }
        }
        return topic;
    }

    static async Task<int> RemoveDeferredMessagesFromServiceBus(ServiceBusSessionReceiver receiver)
    {
        Console.WriteLine($"Removing deferred messages...");

        int numberOfMessages;
        do
        {
            var peekMessages = await receiver.PeekMessagesAsync(100);
            numberOfMessages = peekMessages.Count;
            foreach (var message in peekMessages)
            {
                if (message.State == ServiceBusMessageState.Deferred)
                {
                    Console.WriteLine($"Receiving deferred message {message.MessageId} with sequence number {message.SequenceNumber}");
                    var deferredMessage = await receiver.ReceiveDeferredMessageAsync(message.SequenceNumber);
                    if (deferredMessage != null)
                    {
                        var sbMessage = new NimBus.ServiceBus.ServiceBusMessage(deferredMessage);
                        string? eventId = sbMessage.GetUserProperty(UserPropertyName.EventId);

                        Console.WriteLine($"Received deferred message {deferredMessage.MessageId} EventId: {eventId}");
                        await receiver.CompleteMessageAsync(deferredMessage);
                        Console.WriteLine($"Completed message {deferredMessage.MessageId}");
                    }
                }
            }
        } while (numberOfMessages > 0);
        return numberOfMessages;
    }

    static async Task<int> RemoveActiveMessagesFromServiceBus(ServiceBusSessionReceiver receiver)
    {
        Console.WriteLine($"Removing active messages...");

        int numberOfMessages;
        do
        {
            var messages = await receiver.ReceiveMessagesAsync(100);
            numberOfMessages = messages.Count;

            foreach (var message in messages)
            {
                var sbMessage = new NimBus.ServiceBus.ServiceBusMessage(message);
                string? eventId = sbMessage.GetUserProperty(UserPropertyName.EventId);

                Console.WriteLine($"MessageId: {message.MessageId} EventId: {eventId}");
                await receiver.CompleteMessageAsync(message);
                Console.WriteLine($"Completed message {message.MessageId}");
            }
        } while (numberOfMessages > 0);
        return numberOfMessages;
    }

    static async Task RemoveMessagesFromCosmosDB(CosmosDbClient dbClient, string sessionId, string endpoint)
    {
        Console.WriteLine($"Removing messages in CosmosDB...");

        string? continuationToken = string.Empty;
        int maxSearchItemsCount = 20;
        SearchResponse searchResponse;

        do
        {
            searchResponse = await dbClient.GetEventsByFilter(new EventFilter() { EndPointId = endpoint, SessionId = sessionId }, continuationToken, maxSearchItemsCount);
            continuationToken = searchResponse.ContinuationToken;

            foreach (var @event in searchResponse.Events)
            {
                Console.WriteLine($"Removing message with EventId {@event.EventId} from DB");
                bool messageRemovedFromDb = await dbClient.RemoveMessage(@event.EventId, sessionId, endpoint);
                if (messageRemovedFromDb)
                {
                    Console.WriteLine($"Removed message from DB");
                }
            }
        }
        while (continuationToken != null);
    }
}
