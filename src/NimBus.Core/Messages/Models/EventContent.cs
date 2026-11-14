namespace NimBus.Core.Messages;

public class EventContent
{
    public string EventTypeId { get; set; } = null!;
    public string? EventJson { get; set; }
}
