#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

// Regression for review finding 3: AdminService.BuildExpectedTopology derived the
// expected Service Bus topology purely from the compiled event catalog and never
// consulted platform.DynamicForwards. The CLI provisioner creates an
// "AgentDyn-{target}" forward subscription (rule "dyn-{eventTypeId}") on the SOURCE
// topic for each declared DynamicForward, so the audit marked those subs deprecated
// and RemoveDeprecatedTopologyAsync deleted them — silently dropping every
// dynamically-typed event on that path.
[TestClass]
public sealed class TopologyAuditDynamicForwardTests
{
    private const string SourceEndpoint = "AgentZoneEndpoint";
    private const string TargetEndpoint = "DataPlatformEndpoint";
    private const string DynamicEventTypeId = "crm.contact.enriched.v1";

    [TestMethod]
    public async Task AuditTopology_DoesNotDeprecateAgentDynSubscription_WhenPlatformDeclaresForward()
    {
        var platform = new DynamicForwardPlatform(
            new[] { new DynamicForward(SourceEndpoint, DynamicEventTypeId, TargetEndpoint) },
            new TestEndpoint(SourceEndpoint),
            new TestEndpoint(TargetEndpoint));

        // What the broker actually reports on the SOURCE topic: the provisioner's
        // AgentDyn forward subscription, plus a genuinely-stray subscription as a
        // control that the audit must still flag.
        var broker = new FakeAdminClient(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [$"AgentDyn-{TargetEndpoint}"] = new List<string> { $"dyn-{DynamicEventTypeId}" },
            ["orphan-sub"] = new List<string> { "orphan-rule" },
        });

        var admin = BuildAdminService(platform, broker);

        var result = await admin.AuditTopologyAsync(SourceEndpoint);

        var dynSub = result.Subscriptions
            .Single(s => string.Equals(s.Name, $"agentdyn-{TargetEndpoint.ToLowerInvariant()}", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(dynSub.IsDeprecated, "AgentDyn subscription must not be marked deprecated when a DynamicForward declares it");
        Assert.IsTrue(dynSub.Rules.All(r => !r.IsDeprecated), "AgentDyn rule must not be marked deprecated");

        // Control: an undeclared subscription is still flagged, so the audit hasn't gone blind.
        var orphan = result.Subscriptions.Single(s => string.Equals(s.Name, "orphan-sub", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(orphan.IsDeprecated, "A subscription with no expected counterpart must still be flagged deprecated");
    }

    [TestMethod]
    public async Task AuditTopology_DoesNotDeprecateTheEndpointsReplySubscription()
    {
        // Regression: the expected topology was derived a second time from the compiled
        // event catalog and never mentioned "{endpoint}-reply", which the provisioner
        // creates for request/reply. The audit therefore reported it deprecated and
        // RemoveDeprecatedTopologyAsync deleted it — silently breaking request/reply for
        // that endpoint until the next topology apply.
        var platform = new DynamicForwardPlatform(
            Array.Empty<DynamicForward>(),
            new TestEndpoint(SourceEndpoint));

        var broker = new FakeAdminClient(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{SourceEndpoint}-reply"] = new List<string> { "ReplyFilter" },
            ["orphan-sub"] = new List<string> { "orphan-rule" },
        });

        var result = await BuildAdminService(platform, broker).AuditTopologyAsync(SourceEndpoint);

        var reply = result.Subscriptions
            .Single(s => string.Equals(s.Name, $"{SourceEndpoint}-reply", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(reply.IsDeprecated, "The endpoint's reply subscription must not be marked deprecated");
        Assert.IsTrue(reply.Rules.All(r => !r.IsDeprecated), "ReplyFilter must not be marked deprecated");

        // Control: the audit still flags something genuinely undeclared.
        var orphan = result.Subscriptions.Single(s => string.Equals(s.Name, "orphan-sub", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(orphan.IsDeprecated);
    }

    private static AdminService BuildAdminService(IPlatform platform, ServiceBusAdministrationClient admin) =>
        new AdminService(
            platform,
            messageStore: null!,
            capabilities: null!,
            sbAdmin: admin,
            sbClient: null!,
            managerClient: null!,
            subscriptionAdmin: null!,
            logger: NullLogger<AdminService>.Instance,
            rawCosmosClient: null);

    // ── Fake ServiceBusAdministrationClient (pageable read surface only) ──────

    private sealed class FakeAdminClient : ServiceBusAdministrationClient
    {
        // Subscription name -> rule names. Case-insensitive because GetActualTopology
        // lowercases subscription names before requesting their rules.
        private readonly Dictionary<string, List<string>> _subscriptions;

        public FakeAdminClient(Dictionary<string, List<string>> subscriptions) => _subscriptions = subscriptions;

        public override AsyncPageable<SubscriptionProperties> GetSubscriptionsAsync(string topicName, CancellationToken cancellationToken = default)
        {
            var values = _subscriptions.Keys
                .Select(name => MakeSubscription(topicName, name))
                .ToList();
            return AsyncPageable<SubscriptionProperties>.FromPages(
                new[] { Page<SubscriptionProperties>.FromValues(values, continuationToken: null, FakeResponse.Instance) });
        }

        public override AsyncPageable<RuleProperties> GetRulesAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default)
        {
            var ruleNames = _subscriptions.TryGetValue(subscriptionName, out var rules) ? rules : new List<string>();
            var values = ruleNames
                .Select(name => ServiceBusModelFactory.RuleProperties(name, new SqlRuleFilter("1=1")))
                .ToList();
            return AsyncPageable<RuleProperties>.FromPages(
                new[] { Page<RuleProperties>.FromValues(values, continuationToken: null, FakeResponse.Instance) });
        }

        private static SubscriptionProperties MakeSubscription(string topicName, string subscriptionName) =>
            ServiceBusModelFactory.SubscriptionProperties(
                topicName, subscriptionName,
                lockDuration: TimeSpan.FromMinutes(1),
                requiresSession: false,
                defaultMessageTimeToLive: TimeSpan.MaxValue,
                autoDeleteOnIdle: TimeSpan.MaxValue,
                deadLetteringOnMessageExpiration: false,
                maxDeliveryCount: 10,
                enableBatchedOperations: true,
                status: EntityStatus.Active,
                forwardTo: null,
                forwardDeadLetteredMessagesTo: string.Empty,
                userMetadata: string.Empty);
    }

    private sealed class FakeResponse : Response
    {
        public static FakeResponse Instance { get; } = new();

        public override int Status => 200;
        public override string ReasonPhrase => "OK";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = string.Empty;

        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name) => false;

        protected override IEnumerable<HttpHeader> EnumerateHeaders()
        {
            yield break;
        }

        protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
        {
            value = null;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }
    }

    // ── Minimal platform + endpoint fakes ────────────────────────────────────

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
