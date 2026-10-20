using System;
using System.Collections.Generic;
using System.Text;

namespace NimBus.MessageStore.States;

public class InvalidEvent
{
    public string EventTypeId { get; set; } = null!;
    public string EventId { get; set; } = null!;
    public string EnqueueTime { get; set; } = null!;
}
