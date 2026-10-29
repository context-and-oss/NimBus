#pragma warning disable CA1707, CA2007
using System.Reflection;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// Issue #288: <c>nb container delete</c> and <c>nb container event delete</c> must keep a stored
/// event that blocks its session (a failure, or a pending hand-off). Deleting it left the session
/// blocked by an event no one could see, and nothing left to skip.
/// </summary>
[Collection("Console output")]
public sealed class ContainerDeleteTests
{
    private const string Endpoint = "Crm";

    [Fact]
    public async Task Delete_by_status_keeps_events_that_block_their_session()
    {
        var store = RecordingStore.Create();
        store.Seed("failed-1", ResolutionStatus.Failed);
        store.Seed("handoff-1", ResolutionStatus.Pending, pendingSubStatus: "Handoff");
        store.Seed("dead-1", ResolutionStatus.DeadLettered);

        var kept = await Container.RemoveMessagesFromCosmosDB(
            store.Store, Endpoint, new List<string> { "Failed", "Pending", "DeadLettered" });

        Assert.Equal(2, kept);
        Assert.Equal(new[] { "dead-1" }, store.Removed);
    }

    [Fact]
    public async Task Event_delete_keeps_an_event_that_blocks_its_session()
    {
        var store = RecordingStore.Create();
        store.Seed("failed-1", ResolutionStatus.Failed);

        var kept = await Container.RemoveMessageFromCosmosDB(store.Store, Endpoint, "failed-1");

        Assert.Equal(1, kept);
        Assert.Empty(store.Removed);
    }

    [Fact]
    public async Task Event_delete_removes_an_event_that_does_not_block_its_session()
    {
        var store = RecordingStore.Create();
        store.Seed("dead-1", ResolutionStatus.DeadLettered);

        var kept = await Container.RemoveMessageFromCosmosDB(store.Store, Endpoint, "dead-1");

        Assert.Equal(0, kept);
        Assert.Equal(new[] { "dead-1" }, store.Removed);
    }

    /// <summary>
    /// An <see cref="IMessageTrackingStore"/> that serves one page of rows by status or event id,
    /// records each <c>RemoveMessage</c>, and throws on every other call.
    /// </summary>
    public class RecordingStore : DispatchProxy
    {
        private readonly List<UnresolvedEvent> _rows = new();

        public IMessageTrackingStore Store { get; private set; } = null!;

        public List<string> Removed { get; } = new();

        public static RecordingStore Create()
        {
            var store = Create<IMessageTrackingStore, RecordingStore>();
            var recording = (RecordingStore)(object)store;
            recording.Store = store;
            return recording;
        }

        public void Seed(string eventId, ResolutionStatus status, string? pendingSubStatus = null) =>
            _rows.Add(new UnresolvedEvent
            {
                EventId = eventId,
                SessionId = "sess-" + eventId,
                EndpointId = Endpoint,
                ResolutionStatus = status,
                PendingSubStatus = pendingSubStatus,
                UpdatedAt = DateTime.UtcNow,
                MessageContent = new MessageContent(),
            });

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IMessageTrackingStore.GetEventsByFilter):
                    var filter = (EventFilter)args![0]!;
                    var page = _rows
                        .Where(row => row.EndpointId == filter.EndPointId
                            && (filter.ResolutionStatus is null || filter.ResolutionStatus.Contains(row.ResolutionStatus.ToString()))
                            && (filter.EventId is null || row.EventId == filter.EventId))
                        .ToList();
                    return Task.FromResult(new SearchResponse { Events = page, ContinuationToken = null });
                case nameof(IMessageTrackingStore.RemoveMessage):
                    Removed.Add((string)args![0]!);
                    return Task.FromResult(true);
                default:
                    throw new InvalidOperationException($"Delete must not call {targetMethod?.Name} on the store.");
            }
        }
    }
}
