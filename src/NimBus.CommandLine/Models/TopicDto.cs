namespace NimBus.CommandLine.Models;

public class TopicDto
{
    public string Name { get; set; } = null!;
    public List<SubscriptionDto> Subscriptions { get; set; } = null!;
}
