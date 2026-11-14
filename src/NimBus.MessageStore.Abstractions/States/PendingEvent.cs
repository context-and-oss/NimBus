using System;
using System.Collections.Generic;
using System.Text;

namespace NimBus.MessageStore.States;

public class PendingMessageEvent
{
    public string OriginatingId { get; set; } = null!;
    public string EventId { get; set; } = null!;
    public string Status { get; set; } = null!;
}
