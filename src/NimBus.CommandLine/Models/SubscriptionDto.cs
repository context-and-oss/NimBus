namespace NimBus.CommandLine.Models;

public class SubscriptionDto
{
    public string Name { get; set; } = null!;
    public string TopicName { get; set; } = null!;
    public List<RuleDto> Rules { get; set; } = null!;
    public bool IsDeprecated { get; set; }

    public class SubscriptionDtoComparer : IEqualityComparer<SubscriptionDto>
    {
        public int GetHashCode(SubscriptionDto co)
        {
            if (co == null) return 0;
            return co.Name.GetHashCode();
        }

        public bool Equals(SubscriptionDto? x1, SubscriptionDto? x2)
        {
            if (ReferenceEquals(x1, x2)) return true;
            if (x1 is null || x2 is null) return false;
            return x1.Name == x2.Name && x1.TopicName == x2.TopicName;
        }
    }
}
