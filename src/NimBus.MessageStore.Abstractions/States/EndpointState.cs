using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NimBus.MessageStore;

public class EndpointState
{
    public EndpointState()
    {
        PendingEvents = new List<string>();
        FailedEvents = new List<string>();
        DeferredEvents = new List<string>();
        DeadletteredEvents = new List<string>();
        EnrichedUnresolvedEvents = new List<UnresolvedEvent>();
        UnsupportedEvents = new List<string>();
    }

    public string EndpointId { get; set; } = null!;
    public string? ContinuationToken { get; set; }
    public IEnumerable<UnresolvedEvent> EnrichedUnresolvedEvents { get; set; }
    public IEnumerable<string> PendingEvents { get; set; }
    public IEnumerable<string> FailedEvents { get; set; }
    public IEnumerable<string> DeferredEvents { get; set; }
    public IEnumerable<string> DeadletteredEvents { get; set; }
    public IEnumerable<string> UnsupportedEvents { get; set; }
    public IEnumerable<string> GetAllUnresolvedEvents
    {
        get => PendingEvents
            .Concat(FailedEvents)
            .Concat(DeferredEvents)
            .Concat(DeadletteredEvents)
            .Concat(UnsupportedEvents);
    }
    public DateTime EventTime { get; set; }
}
