using Azure;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.ServiceBus.Provisioning;
using Xunit;

namespace NimBus.CommandLine.Tests;

public sealed class ServiceBusTopologyProvisionerTests
{
    [Fact]
    public async Task ApplyAsync_CreatesSessionEnabledDeferredSubscriptionsAndExpectedRules()
    {
        var client = new RecordingAdministrationClient();
        var sut = CreateProvisioner(client, new TestPlatform(new TestEndpoint("orders")));

        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        Assert.Contains(client.CreatedSubscriptions, x => x.TopicName == "orders" && x.SubscriptionName == "Deferred" && x.RequiresSession);
        Assert.Contains(client.CreatedSubscriptions, x => x.TopicName == "orders" && x.SubscriptionName == "DeferredProcessor" && !x.RequiresSession);

        Assert.Contains(client.DeletedRules, x => x.TopicName == "orders" && x.SubscriptionName == "orders" && x.RuleName == "$Default");
        Assert.Contains(client.DeletedRules, x => x.TopicName == "orders" && x.SubscriptionName == Constants.ResolverId && x.RuleName == "$Default");
        Assert.Contains(client.DeletedRules, x => x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredSubscriptionName && x.RuleName == "$Default");
        Assert.Contains(client.DeletedRules, x => x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredProcessorId && x.RuleName == "$Default");

        Assert.Contains(client.CreatedRules, x => x.TopicName == "orders" && x.SubscriptionName == "orders" && x.Rule.Name == "to-orders");
        Assert.Contains(client.CreatedRules, x => x.TopicName == "orders" && x.SubscriptionName == Constants.ResolverId && x.Rule.Name == "from-orders");
        Assert.Contains(client.CreatedRules, x => x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredSubscriptionName && x.Rule.Name == "DeferredFilter");
        Assert.Contains(client.CreatedRules, x => x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredProcessorId && x.Rule.Name == "DeferredProcessorFilter");
    }

    [Fact]
    public async Task ApplyAsync_RecreatesDeferredSubscriptionsWhenSessionSupportMismatch()
    {
        var client = new RecordingAdministrationClient();
        // Deferred subscription seeded WITHOUT sessions — should be recreated WITH sessions
        client.SeedSubscription("orders", MakeSubscriptionProperties("orders", Constants.DeferredSubscriptionName,
            requiresSession: false));
        // DeferredProcessor seeded WITH sessions — should be recreated WITHOUT sessions
        client.SeedSubscription("orders", MakeSubscriptionProperties("orders", Constants.DeferredProcessorId,
            requiresSession: true));

        var sut = CreateProvisioner(client, new TestPlatform(new TestEndpoint("orders")));

        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        Assert.Contains(client.DeletedSubscriptions, x => x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredSubscriptionName);
        Assert.Contains(client.DeletedSubscriptions, x => x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredProcessorId);

        var recreatedDeferred = Assert.Single(client.CreatedSubscriptions, x =>
            x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredSubscriptionName);
        var recreatedProcessor = Assert.Single(client.CreatedSubscriptions, x =>
            x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredProcessorId);

        Assert.True(recreatedDeferred.RequiresSession);
        Assert.False(recreatedProcessor.RequiresSession);
    }

    [Fact]
    public async Task ApplyAsync_CrossTopicForwardRule_OnlyMatchesOriginalPublishes()
    {
        // Regression for the forwarding-loop bug: when an event type is produced AND
        // consumed by both endpoints (e.g. ContactCreated in CrmErpDemo where CRM and
        // ERP both publish and subscribe), a forward rule that filters only on
        // EventTypeId triggers on its own forwarded output:
        //   CRM publishes -> forwarded to ERP -> ERP's forward sub re-matches the
        //   same EventTypeId -> forwarded back to CRM -> ...
        // Service Bus's MaxHopCount eventually dead-letters the message
        // ("Maximum transfer hop count is exceeded").
        // The fix: filter must include "AND user.From IS NULL" so the rule only fires
        // on original publishes (where the publisher never sets From), not on
        // already-forwarded copies (where the action SETs From=<endpoint>).
        var client = new RecordingAdministrationClient();

        var crm = new EventEndpoint(
            "CrmEndpoint",
            produces: new[] { "ContactCreated" },
            consumes: new[] { "ContactCreated" });
        var erp = new EventEndpoint(
            "ErpEndpoint",
            produces: new[] { "ContactCreated" },
            consumes: new[] { "ContactCreated" });

        var sut = CreateProvisioner(client, new TestPlatform(crm, erp));
        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        var crmToErpRule = Assert.Single(client.CreatedRules, r =>
            r.TopicName == "CrmEndpoint" && r.SubscriptionName == "ErpEndpoint" && r.Rule.Name == "ContactCreated");
        var erpToCrmRule = Assert.Single(client.CreatedRules, r =>
            r.TopicName == "ErpEndpoint" && r.SubscriptionName == "CrmEndpoint" && r.Rule.Name == "ContactCreated");

        var crmFilter = ((SqlRuleFilter)crmToErpRule.Rule.Filter).SqlExpression;
        var erpFilter = ((SqlRuleFilter)erpToCrmRule.Rule.Filter).SqlExpression;

        Assert.Contains("user.From IS NULL", crmFilter, StringComparison.Ordinal);
        Assert.Contains("user.From IS NULL", erpFilter, StringComparison.Ordinal);
        Assert.Contains("user.EventTypeId = 'ContactCreated'", crmFilter, StringComparison.Ordinal);
        Assert.Contains("user.EventTypeId = 'ContactCreated'", erpFilter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyAsync_WithMultipleEventsToSameConsumer_KeepsAllForwardingRules()
    {
        // Regression for the ForwardTo-comparison bug: when a producer endpoint emits
        // multiple events all consumed by the same other endpoint, the provisioner used
        // to call EnsureForwardSubscriptionAsync once per event. Each call after the
        // first detected a "ForwardTo mismatch" (because Azure normalises ForwardTo to
        // a lowercased entity name or full URL while the code passes the PascalCase
        // bare name) and deleted+recreated the subscription, wiping every rule added
        // by previous iterations. End state: only the LAST rule (alphabetically) survived.
        var client = new RecordingAdministrationClient(forwardToNormalizer: forwardTo => forwardTo?.ToLowerInvariant());

        var producer = new EventEndpoint(
            "CrmEndpoint",
            produces: new[] { "AccountCreated", "AccountUpdated", "ContactCreated", "ContactUpdated" },
            consumes: Array.Empty<string>());
        var consumer = new EventEndpoint(
            "ErpEndpoint",
            produces: Array.Empty<string>(),
            consumes: new[] { "AccountCreated", "AccountUpdated", "ContactCreated", "ContactUpdated" });

        var sut = CreateProvisioner(client, new TestPlatform(producer, consumer));
        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        // The cross-topic forward subscription should be created exactly once and never deleted.
        Assert.Single(client.CreatedSubscriptions, s =>
            s.TopicName == "CrmEndpoint" && s.SubscriptionName == "ErpEndpoint");
        Assert.DoesNotContain(client.DeletedSubscriptions, x =>
            x.TopicName == "CrmEndpoint" && x.SubscriptionName == "ErpEndpoint");

        // All four forwarding rules must coexist — not just the alphabetically-last one.
        foreach (var eventName in new[] { "AccountCreated", "AccountUpdated", "ContactCreated", "ContactUpdated" })
        {
            Assert.Contains(client.CreatedRules, r =>
                r.TopicName == "CrmEndpoint" &&
                r.SubscriptionName == "ErpEndpoint" &&
                r.Rule.Name == eventName);
        }
    }

    [Fact]
    public async Task ApplyAsync_WithMatchingTopology_DoesNotRecreateSubscriptionsOrRules()
    {
        var client = new RecordingAdministrationClient();
        client.SeedTopic(Constants.ResolverId);
        client.SeedTopic("orders");

        client.SeedSubscription(Constants.ResolverId, MakeSubscriptionProperties(Constants.ResolverId, Constants.ResolverId,
            requiresSession: true));
        client.SeedSubscription("orders", MakeSubscriptionProperties("orders", "orders",
            requiresSession: true));
        client.SeedSubscription("orders", MakeSubscriptionProperties("orders", Constants.ResolverId,
            requiresSession: false, forwardTo: Constants.ResolverId));
        client.SeedSubscription("orders", MakeSubscriptionProperties("orders", Constants.DeferredSubscriptionName,
            requiresSession: true));
        client.SeedSubscription("orders", MakeSubscriptionProperties("orders", Constants.DeferredProcessorId,
            requiresSession: false));
        client.SeedSubscription("orders", MakeSubscriptionProperties("orders", "orders-reply",
            requiresSession: true));

        client.SeedRule("orders", "orders", ServiceBusModelFactory.RuleProperties("to-orders", new SqlRuleFilter("user.To = 'orders'")));
        client.SeedRule("orders", "orders-reply", ServiceBusModelFactory.RuleProperties("ReplyFilter", new SqlRuleFilter("user.To = 'orders-reply'")));
        client.SeedRule("orders", Constants.ResolverId, ServiceBusModelFactory.RuleProperties("from-orders", new SqlRuleFilter($"user.To = '{Constants.ResolverId}'"),
            new SqlRuleAction("SET user.From = 'orders'")));
        client.SeedRule("orders", Constants.ResolverId, ServiceBusModelFactory.RuleProperties("to-orders", new SqlRuleFilter("user.To = 'orders'")));
        client.SeedRule("orders", "orders", ServiceBusModelFactory.RuleProperties("continuation", new SqlRuleFilter($"user.To = '{Constants.ContinuationId}'"),
            new SqlRuleAction($"SET user.To = 'orders'; SET user.From = '{Constants.ContinuationId}'")));
        client.SeedRule("orders", "orders", ServiceBusModelFactory.RuleProperties("retry", new SqlRuleFilter($"user.To = '{Constants.RetryId}'"),
            new SqlRuleAction($"SET user.To = 'orders'; SET user.From = '{Constants.RetryId}'")));
        client.SeedRule("orders", Constants.DeferredSubscriptionName, ServiceBusModelFactory.RuleProperties("DeferredFilter", new SqlRuleFilter("user.To = 'Deferred' AND user.OriginalSessionId IS NOT NULL")));
        client.SeedRule("orders", Constants.DeferredProcessorId, ServiceBusModelFactory.RuleProperties("DeferredProcessorFilter", new SqlRuleFilter("user.To = 'DeferredProcessor'")));

        var sut = CreateProvisioner(client, new TestPlatform(new TestEndpoint("orders")));

        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        Assert.Empty(client.CreatedTopics);
        Assert.Empty(client.CreatedSubscriptions);
        Assert.Empty(client.DeletedSubscriptions);
        Assert.Empty(client.CreatedRules);
        Assert.Empty(client.DeletedRules);
    }

    [Fact]
    public async Task ApplyAsync_CreatesExactlyWhatTheDescriptorDescribes()
    {
        // The WebApp's subscription admin rebuilds a deleted subscription from
        // TopologyDescriptor. That is only safe while what the descriptor says and what
        // the provisioner lays down cannot drift, so pin the two against each other on a
        // platform exercising every subscription kind: a producer, a consumer, a
        // self-consumed event type, and a dynamic forward.
        var client = new RecordingAdministrationClient();

        var crm = new EventEndpoint("CrmEndpoint", produces: new[] { "AccountCreated", "ContactCreated" }, consumes: new[] { "ContactCreated" });
        var erp = new EventEndpoint("ErpEndpoint", produces: Array.Empty<string>(), consumes: new[] { "AccountCreated", "ContactCreated" });
        var platform = new TestPlatform(new[] { new DynamicForward("CrmEndpoint", "LegacyOrderPlaced", "ErpEndpoint") }, crm, erp);

        var sut = CreateProvisioner(client, platform);
        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        foreach (var topicName in new[] { Constants.ResolverId, "CrmEndpoint", "ErpEndpoint" })
        {
            var described = TopologyDescriptor.ForTopic(topicName, platform);

            Assert.Equal(
                described.Select(subscription => subscription.Name).OrderBy(name => name, StringComparer.Ordinal),
                client.CreatedSubscriptions
                    .Where(subscription => subscription.TopicName == topicName)
                    .Select(subscription => subscription.SubscriptionName)
                    .OrderBy(name => name, StringComparer.Ordinal));

            foreach (var expected in described)
            {
                var created = Assert.Single(client.CreatedSubscriptions, subscription =>
                    subscription.TopicName == topicName && subscription.SubscriptionName == expected.Name);

                Assert.Equal(expected.RequiresSession, created.RequiresSession);
                Assert.Equal(expected.ForwardTo ?? string.Empty, created.ForwardTo ?? string.Empty);

                // $Default is dropped everywhere the descriptor doesn't ask to keep it —
                // a true-filter left in place hands the subscription the whole topic.
                Assert.Equal(
                    !expected.KeepDefaultRule,
                    client.DeletedRules.Any(rule =>
                        rule.TopicName == topicName &&
                        rule.SubscriptionName == expected.Name &&
                        rule.RuleName == TopologyDescriptor.DefaultRuleName));

                Assert.Equal(
                    expected.Rules.Select(rule => rule.Name).OrderBy(name => name, StringComparer.Ordinal),
                    client.CreatedRules
                        .Where(rule => rule.TopicName == topicName && rule.SubscriptionName == expected.Name)
                        .Select(rule => rule.Rule.Name)
                        .OrderBy(name => name, StringComparer.Ordinal));

                foreach (var expectedRule in expected.Rules)
                {
                    var createdRule = Assert.Single(client.CreatedRules, rule =>
                        rule.TopicName == topicName &&
                        rule.SubscriptionName == expected.Name &&
                        rule.Rule.Name == expectedRule.Name).Rule;

                    // Ordinal: RuleMatches compares ordinally, so anything short of
                    // byte-identical churns the rule on the next apply.
                    Assert.Equal(expectedRule.Filter, ((SqlRuleFilter)createdRule.Filter).SqlExpression, StringComparer.Ordinal);
                    Assert.Equal(
                        expectedRule.Action ?? string.Empty,
                        (createdRule.Action as SqlRuleAction)?.SqlExpression ?? string.Empty,
                        StringComparer.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void Descriptor_DescribesTheEndpointsOwnReplySubscription()
    {
        // The reply subscription is the one an endpoint's request/reply traffic lands on.
        // Omitting it from the expected topology makes every consumer of that topology —
        // the admin topology audit included — read it as deprecated.
        var described = TopologyDescriptor.ForTopic("orders", new TestPlatform(new TestEndpoint("orders")));

        var reply = Assert.Single(described, subscription => subscription.Name == "orders-reply");
        Assert.True(reply.RequiresSession);
        Assert.Null(reply.ForwardTo);
        Assert.Equal("user.To = 'orders-reply'", Assert.Single(reply.Rules).Filter, StringComparer.Ordinal);
    }

    [Fact]
    public void Descriptor_OmitsAForwarderForAnEndpointConsumingItsOwnEvent()
    {
        // Such a forwarder would collide with the endpoint's own terminal subscription of
        // the same name, and Service Bus rejects a subscription forwarding to its own topic.
        var endpoint = new EventEndpoint("CrmEndpoint", produces: new[] { "ContactCreated" }, consumes: new[] { "ContactCreated" });
        var described = TopologyDescriptor.ForTopic("CrmEndpoint", new TestPlatform(endpoint));

        var own = Assert.Single(described, subscription => subscription.Name == "CrmEndpoint");
        Assert.Null(own.ForwardTo);
        Assert.True(own.RequiresSession);
    }

    [Fact]
    public void FindSubscription_ReturnsNullForSomethingThePlatformCannotRebuild()
    {
        var platform = new TestPlatform(new TestEndpoint("orders"));

        Assert.Null(TopologyDescriptor.FindSubscription("orders", "hand-made-by-an-operator", platform));
        Assert.Null(TopologyDescriptor.FindSubscription("a-topic-nobody-declared", "orders", platform));
        Assert.NotNull(TopologyDescriptor.FindSubscription("orders", Constants.DeferredSubscriptionName, platform));
        // Case-insensitively, because Service Bus reports entity names as stored.
        Assert.NotNull(TopologyDescriptor.FindSubscription("ORDERS", "deferredprocessor", platform));
    }

    [Fact]
    public void ResolverSubscription_KeepsItsDefaultRule()
    {
        // The Resolver consumes everything forwarded to its topic, so $Default is its
        // routing — dropping it would silence the resolver entirely.
        var resolver = Assert.Single(TopologyDescriptor.ForSystemTopic(Constants.ResolverId));

        Assert.True(resolver.KeepDefaultRule);
        Assert.True(resolver.RequiresSession);
        Assert.Empty(resolver.Rules);
    }

    [Fact]
    public void DeferredSubscription_DropsToAnEmulatorSafeTimeToLive()
    {
        Assert.Equal(TimeSpan.FromDays(14), TopologyDescriptor.DeferredSubscription().DefaultMessageTimeToLive);
        Assert.Equal(TimeSpan.FromHours(1), TopologyDescriptor.DeferredSubscription(isEmulator: true).DefaultMessageTimeToLive);
    }

    private static SubscriptionProperties MakeSubscriptionProperties(
        string topicName, string subscriptionName,
        bool requiresSession = false, string? forwardTo = null) =>
        ServiceBusModelFactory.SubscriptionProperties(
            topicName, subscriptionName,
            lockDuration: TimeSpan.FromMinutes(1),
            requiresSession: requiresSession,
            defaultMessageTimeToLive: TimeSpan.MaxValue,
            autoDeleteOnIdle: TimeSpan.MaxValue,
            deadLetteringOnMessageExpiration: false,
            maxDeliveryCount: 10,
            enableBatchedOperations: true,
            status: EntityStatus.Active,
            forwardTo: forwardTo,
            forwardDeadLetteredMessagesTo: string.Empty,
            userMetadata: string.Empty);

    private static ServiceBusTopologyProvisioner CreateProvisioner(RecordingAdministrationClient client, IPlatform platform) =>
        new(
            new AzureCliRunner(),
            static (options, cancellationToken, runner) => Task.FromResult("Endpoint=sb://example/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=test"),
            _ => client,
            () => platform);

    private sealed class RecordingAdministrationClient : ServiceBusAdministrationClient
    {
        private readonly Dictionary<(string TopicName, string SubscriptionName), SubscriptionProperties> _subscriptions = new();
        private readonly Dictionary<(string TopicName, string SubscriptionName, string RuleName), RuleProperties> _rules = new();
        private readonly HashSet<string> _topics = new(StringComparer.Ordinal);
        private readonly Func<string?, string?> _forwardToNormalizer;

        public RecordingAdministrationClient(Func<string?, string?>? forwardToNormalizer = null)
        {
            // Azure Service Bus normalises ForwardTo on read (e.g. lowercases entity
            // names, returns full URLs). The default test client preserves what was
            // sent so most tests don't have to care; specific tests opt in to a
            // normalisation function to model Azure's behaviour.
            _forwardToNormalizer = forwardToNormalizer ?? (forwardTo => forwardTo);
        }

        public List<CreateSubscriptionOptions> CreatedSubscriptions { get; } = new();
        public List<(string TopicName, string SubscriptionName)> DeletedSubscriptions { get; } = new();
        public List<(string TopicName, string SubscriptionName, CreateRuleOptions Rule)> CreatedRules { get; } = new();
        public List<(string TopicName, string SubscriptionName, string RuleName)> DeletedRules { get; } = new();
        public List<string> CreatedTopics { get; } = new();

        public void SeedTopic(string topicName) => _topics.Add(topicName);

        public void SeedSubscription(string topicName, SubscriptionProperties properties) =>
            _subscriptions[(topicName, properties.SubscriptionName)] = properties;

        public void SeedRule(string topicName, string subscriptionName, RuleProperties properties) =>
            _rules[(topicName, subscriptionName, properties.Name)] = properties;

        public override Task<Response<bool>> TopicExistsAsync(string topicName, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response.FromValue(_topics.Contains(topicName), FakeResponse.Instance));

        public override Task<Response<TopicProperties>> CreateTopicAsync(CreateTopicOptions options, CancellationToken cancellationToken = default)
        {
            _topics.Add(options.Name);
            CreatedTopics.Add(options.Name);
            var topic = ServiceBusModelFactory.TopicProperties(options.Name,
                defaultMessageTimeToLive: options.DefaultMessageTimeToLive,
                autoDeleteOnIdle: options.AutoDeleteOnIdle,
                duplicateDetectionHistoryTimeWindow: options.DuplicateDetectionHistoryTimeWindow,
                maxSizeInMegabytes: options.MaxSizeInMegabytes);
            return Task.FromResult(Response.FromValue(topic, FakeResponse.Instance));
        }

        public override Task<Response<SubscriptionProperties>> GetSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default)
        {
            if (_subscriptions.TryGetValue((topicName, subscriptionName), out var subscription))
            {
                return Task.FromResult(Response.FromValue(subscription, FakeResponse.Instance));
            }

            throw new RequestFailedException(404, "Not found");
        }

        public override Task<Response<SubscriptionProperties>> CreateSubscriptionAsync(CreateSubscriptionOptions options, CancellationToken cancellationToken = default)
        {
            CreatedSubscriptions.Add(options);
            var subscription = MakeSubscriptionProperties(
                options.TopicName,
                options.SubscriptionName,
                requiresSession: options.RequiresSession,
                forwardTo: _forwardToNormalizer(options.ForwardTo));
            _subscriptions[(options.TopicName, options.SubscriptionName)] = subscription;
            // Azure Service Bus auto-creates a $Default rule on new subscriptions
            _rules[(options.TopicName, options.SubscriptionName, "$Default")] =
                ServiceBusModelFactory.RuleProperties("$Default", new TrueRuleFilter());
            return Task.FromResult(Response.FromValue(subscription, FakeResponse.Instance));
        }

        public override Task<Response> DeleteSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default)
        {
            DeletedSubscriptions.Add((topicName, subscriptionName));
            _subscriptions.Remove((topicName, subscriptionName));
            return Task.FromResult<Response>(FakeResponse.Instance);
        }

        public override Task<Response<RuleProperties>> GetRuleAsync(string topicName, string subscriptionName, string ruleName, CancellationToken cancellationToken = default)
        {
            if (_rules.TryGetValue((topicName, subscriptionName, ruleName), out var rule))
            {
                return Task.FromResult(Response.FromValue(rule, FakeResponse.Instance));
            }

            throw new RequestFailedException(404, "Not found");
        }

        public override Task<Response<RuleProperties>> CreateRuleAsync(string topicName, string subscriptionName, CreateRuleOptions ruleOptions, CancellationToken cancellationToken = default)
        {
            CreatedRules.Add((topicName, subscriptionName, ruleOptions));
            var rule = ServiceBusModelFactory.RuleProperties(ruleOptions.Name, ruleOptions.Filter, ruleOptions.Action);
            _rules[(topicName, subscriptionName, ruleOptions.Name)] = rule;
            return Task.FromResult(Response.FromValue(rule, FakeResponse.Instance));
        }

        public override Task<Response> DeleteRuleAsync(string topicName, string subscriptionName, string ruleName, CancellationToken cancellationToken = default)
        {
            DeletedRules.Add((topicName, subscriptionName, ruleName));
            _rules.Remove((topicName, subscriptionName, ruleName));
            return Task.FromResult<Response>(FakeResponse.Instance);
        }
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

        protected override bool TryGetHeader(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
        {
            value = null;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }
    }

    private sealed class TestPlatform : Platform
    {
        public TestPlatform(params IEndpoint[] endpoints)
            : this(Array.Empty<DynamicForward>(), endpoints)
        {
        }

        public TestPlatform(IReadOnlyList<DynamicForward> dynamicForwards, params IEndpoint[] endpoints)
        {
            DynamicForwards = dynamicForwards;
            foreach (var endpoint in endpoints)
            {
                AddEndpoint(endpoint);
            }
        }

        public override IReadOnlyList<DynamicForward> DynamicForwards { get; }
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

    private sealed class EventEndpoint : IEndpoint
    {
        public EventEndpoint(string id, IEnumerable<string> produces, IEnumerable<string> consumes)
        {
            Id = id;
            Name = id;
            EventTypesProduced = produces.Select(name => (IEventType)new TestEventType(name)).ToList();
            EventTypesConsumed = consumes.Select(name => (IEventType)new TestEventType(name)).ToList();
        }

        public string Id { get; }
        public string Name { get; }
        public string Description => string.Empty;
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced { get; }
        public IEnumerable<IEventType> EventTypesConsumed { get; }
        public IEnumerable<IRoleAssignment> RoleAssignments => Array.Empty<IRoleAssignment>();
    }

    private sealed class TestEventType : IEventType
    {
        public TestEventType(string id)
        {
            Id = id;
            Name = id;
        }

        public string Id { get; }
        public string Name { get; }
        public string Namespace => "Tests";
        public string Description => string.Empty;
        public string SessionKeyProperty => string.Empty;
        public IEnumerable<IProperty> Properties => Array.Empty<IProperty>();
        public Type GetEventClassType() => typeof(TestEventType);
        public IEvent GetEventExample() => null!;

        // Equality keyed on Id so Platform.GetConsumers (which uses
        // EventTypesConsumed.Contains) matches across endpoints that each
        // own their own IEventType instances for the same logical event.
        public override bool Equals(object? obj) =>
            obj is TestEventType other && string.Equals(Id, other.Id, StringComparison.Ordinal);
        public override int GetHashCode() => Id.GetHashCode(StringComparison.Ordinal);
    }
}


