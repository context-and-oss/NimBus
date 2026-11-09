#pragma warning disable CA1707, CA2007
using NimBus.CommandLine.Models;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using Xunit;

namespace NimBus.CommandLine.Tests;

// Regression for review finding 3: the `nb` topology audit derives the expected
// topology purely from the compiled event catalog and never consulted
// platform.DynamicForwards. The CLI provisioner creates an "AgentDyn-{target}"
// forward subscription (rule "dyn-{eventTypeId}") on the SOURCE topic for each
// declared DynamicForward, so the audit flagged those subs as deprecated and
// would have deleted them — silently dropping every dynamically-typed event.
public sealed class DynamicForwardTopologyTests
{
    private const string SourceEndpoint = "AgentZoneEndpoint";
    private const string TargetEndpoint = "DataPlatformEndpoint";
    private const string DynamicEventTypeId = "crm.contact.enriched.v1";

    [Fact]
    public void GetExpectedTopic_WithDynamicForward_IncludesAgentDynSubscriptionAndRule()
    {
        var platform = new DynamicForwardPlatform(
            new[] { new DynamicForward(SourceEndpoint, DynamicEventTypeId, TargetEndpoint) },
            new TestEndpoint(SourceEndpoint),
            new TestEndpoint(TargetEndpoint));

        var expected = Endpoint.GetExpectedTopic(SourceEndpoint.ToLowerInvariant(), platform);

        var dynSub = expected.Subscriptions
            .FirstOrDefault(s => s.Name == $"agentdyn-{TargetEndpoint.ToLowerInvariant()}");
        Assert.NotNull(dynSub);
        Assert.Contains(dynSub!.Rules, r => r.Name == $"dyn-{DynamicEventTypeId}");
    }

    [Fact]
    public void GetIsDeprecatedTopic_DoesNotFlagAgentDynSubscription_WhenPlatformDeclaresForward()
    {
        var platform = new DynamicForwardPlatform(
            new[] { new DynamicForward(SourceEndpoint, DynamicEventTypeId, TargetEndpoint) },
            new TestEndpoint(SourceEndpoint),
            new TestEndpoint(TargetEndpoint));

        var expected = Endpoint.GetExpectedTopic(SourceEndpoint.ToLowerInvariant(), platform);

        // Model what the broker actually reports (GetActualTopic lowercases everything).
        var subName = $"agentdyn-{TargetEndpoint.ToLowerInvariant()}";
        var actual = new TopicDto
        {
            Name = SourceEndpoint.ToLowerInvariant(),
            Subscriptions = new List<SubscriptionDto>
            {
                new SubscriptionDto
                {
                    Name = subName,
                    TopicName = SourceEndpoint.ToLowerInvariant(),
                    Rules = new List<RuleDto>
                    {
                        new RuleDto { Name = $"dyn-{DynamicEventTypeId}", SubscriptionName = subName }
                    }
                }
            }
        };

        var marked = Endpoint.GetIsDeprecatedTopic(expected, actual);

        var dynSub = marked.Subscriptions.Single(s => s.Name == subName);
        Assert.False(dynSub.IsDeprecated, "AgentDyn subscription must not be marked deprecated when a DynamicForward declares it");
        Assert.All(dynSub.Rules, r => Assert.False(r.IsDeprecated, "AgentDyn rule must not be marked deprecated"));
    }

    private sealed class DynamicForwardPlatform : Platform
    {
        private readonly IReadOnlyList<DynamicForward> _forwards;

        public DynamicForwardPlatform(IReadOnlyList<DynamicForward> forwards, params IEndpoint[] endpoints)
        {
            _forwards = forwards;
            foreach (var endpoint in endpoints)
            {
                AddEndpoint(endpoint);
            }
        }

        public override IReadOnlyList<DynamicForward> DynamicForwards => _forwards;
    }

    private sealed class TestEndpoint : IEndpoint
    {
        public TestEndpoint(string id)
        {
            Id = id;
            Name = id;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description => string.Empty;
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => Array.Empty<IEventType>();
        public IEnumerable<IEventType> EventTypesConsumed => Array.Empty<IEventType>();
        public IEnumerable<IRoleAssignment> RoleAssignments => Array.Empty<IRoleAssignment>();
    }
}
