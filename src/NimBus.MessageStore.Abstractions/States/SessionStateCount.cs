using System;
using System.Collections.Generic;
using System.Text;

namespace NimBus.MessageStore.States;

public class SessionStateCount
{
    public string SessionId { get; set; } = null!;
    public IEnumerable<string> PendingEvents { get; set; } = null!;
    public IEnumerable<string> DeferredEvents { get; set; } = null!;
}
