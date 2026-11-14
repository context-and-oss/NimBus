#pragma warning disable CA1707, CA2007
using System.Reflection;
using NimBus.Core.Messages;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// Issue #287: <c>nb container skip</c> has to send the endpoint a SkipRequest for every event it
/// skips, as the WebApp's Skip does. Only the endpoint can clear a session's block, so writing
/// Skipped to the store alone left the session blocked and its later messages deferred.
/// </summary>
[Collection("Console output")]
public sealed class ContainerSkipTests
{
    private const string Endpoint = "Crm";
    private static readonly List<string> Failed = new() { nameof(ResolutionStatus.Failed) };

    [Fact]
    public async Task Skip_sends_a_skip_request_for_each_matching_event_on_its_session()
    {
        var store = TrackingStoreReads.Create();
        store.SeedFailed("evt-1");
        store.SeedFailed("evt-2");
        var manager = new RecordingManagerClient();

        // The store fails on any write: the Resolver records Skipped from the endpoint's SkipResponse.
        var sent = await Container.SendSkipRequests(manager, store.Store, Endpoint, Failed, before: null);

        Assert.Equal(2, sent);
        Assert.Equal(
            new[]
            {
                new SkipCall("evt-1", "sess-evt-1", "term-evt-1", Endpoint, "Demo.Type"),
                new SkipCall("evt-2", "sess-evt-2", "term-evt-2", Endpoint, "Demo.Type"),
            },
            manager.Calls.OrderBy(c => c.EventId, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Skip_leaves_events_updated_after_the_cutoff()
    {
        var store = TrackingStoreReads.Create();
        store.SeedFailed("evt-1");
        var manager = new RecordingManagerClient();

        var sent = await Container.SendSkipRequests(manager, store.Store, Endpoint, Failed, before: DateTime.UtcNow.AddHours(-1));

        Assert.Equal(0, sent);
        Assert.Empty(manager.Calls);
    }

    [Fact]
    public async Task Skip_reports_a_failed_send_and_continues()
    {
        var store = TrackingStoreReads.Create();
        store.SeedFailed("evt-1");
        store.SeedFailed("evt-2");
        var manager = new RecordingManagerClient { FailFor = "evt-1" };

        var sent = await Container.SendSkipRequests(manager, store.Store, Endpoint, Failed, before: null);

        Assert.Equal(1, sent);
        Assert.Equal("evt-2", Assert.Single(manager.Calls).EventId);
    }

    // A skip settles none of these: a Deferred event's parked copy replays and runs once its
    // session's block clears, a Pending, TooManyRequests or Published message is still on its way,
    // and Completed is terminal. The command refuses them before connecting to anything.
    [Theory]
    [InlineData("deferred")]
    [InlineData("pending")]
    [InlineData("toomanyrequests")]
    [InlineData("published")]
    [InlineData("completed")]
    public async Task Skip_command_refuses_statuses_a_skip_cannot_settle(string status)
    {
        using var app = CliApplicationFactory.Create();

        var exitCode = await app.ExecuteAsync(new[]
        {
            "container", "skip", Endpoint, "--status", status,
            "--db-connection-string", "AccountEndpoint=https://localhost:8081/;AccountKey=a2V5;",
            "--sb-connection-string", "Endpoint=sb://localhost/;SharedAccessKeyName=k;SharedAccessKey=a2V5",
        });

        Assert.Equal(1, exitCode);
    }

    private sealed record SkipCall(string EventId, string SessionId, string MessageId, string Endpoint, string EventTypeId);

    private sealed class RecordingManagerClient : IManagerClient
    {
        public List<SkipCall> Calls { get; } = new();

        public string? FailFor { get; init; }

        public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson) =>
            throw new InvalidOperationException("Skip must not resubmit.");

        public Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId)
        {
            if (errorResponse.EventId == FailFor)
                throw new InvalidOperationException("Service Bus unavailable");
            Calls.Add(new SkipCall(errorResponse.EventId, errorResponse.SessionId, errorResponse.MessageId, endpoint, eventTypeId));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// An <see cref="IMessageTrackingStore"/> that serves the two reads a skip makes, one page of
    /// rows and their latest messages, and throws on every other call, writes above all.
    /// </summary>
    public class TrackingStoreReads : DispatchProxy
    {
        private readonly List<UnresolvedEvent> _rows = new();
        private readonly Dictionary<(string EventId, string MessageId), MessageEntity> _messages = new();

        public IMessageTrackingStore Store { get; private set; } = null!;

        public static TrackingStoreReads Create()
        {
            var store = Create<IMessageTrackingStore, TrackingStoreReads>();
            var reads = (TrackingStoreReads)(object)store;
            reads.Store = store;
            return reads;
        }

        /// <summary>
        /// Seeds a Failed row and the ErrorResponse it was recorded for, in a session of its own,
        /// as the Resolver leaves them for a message that failed on the endpoint.
        /// </summary>
        public void SeedFailed(string eventId)
        {
            var messageId = "term-" + eventId;
            var sessionId = "sess-" + eventId;
            _messages[(eventId, messageId)] = new MessageEntity
            {
                EventId = eventId,
                MessageId = messageId,
                SessionId = sessionId,
                MessageType = MessageType.ErrorResponse,
                EventTypeId = "Demo.Type",
                From = Endpoint,
                To = "Resolver",
                OriginatingMessageId = "req-" + eventId,
                MessageContent = new MessageContent(),
            };
            _rows.Add(new UnresolvedEvent
            {
                EventId = eventId,
                SessionId = sessionId,
                EndpointId = Endpoint,
                LastMessageId = messageId,
                MessageType = MessageType.ErrorResponse,
                ResolutionStatus = ResolutionStatus.Failed,
                UpdatedAt = DateTime.UtcNow,
                MessageContent = new MessageContent(),
            });
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IMessageTrackingStore.GetEventsByFilter):
                    var filter = (EventFilter)args![0]!;
                    var page = _rows
                        .Where(row => row.EndpointId == filter.EndPointId
                            && filter.ResolutionStatus!.Contains(row.ResolutionStatus.ToString()))
                        .ToList();
                    return Task.FromResult(new SearchResponse { Events = page, ContinuationToken = null });
                case nameof(IMessageTrackingStore.GetMessage):
                    _messages.TryGetValue(((string)args![0]!, (string)args[1]!), out var message);
                    return Task.FromResult(message);
                default:
                    throw new InvalidOperationException($"Skip must not call {targetMethod?.Name} on the store.");
            }
        }
    }
}
