#pragma warning disable CA1707, CA2007
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
        // "user.From IS NULL" keeps the rule off already-forwarded copies (where the
        // action SETs From=<endpoint>). "user.MessageType = 'EventRequest'" keeps it off
        // the endpoint's own control traffic about the event (responses to the Resolver,
        // RetryRequest, ProcessDeferredRequest, resubmissions), which carries the same
        // EventTypeId and no From and would otherwise ping-pong between the endpoints.
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

        Assert.Equal(NewContactCreatedFilter, crmFilter, StringComparer.Ordinal);
        Assert.Equal(NewContactCreatedFilter, erpFilter, StringComparer.Ordinal);
    }

    private const string NewContactCreatedFilter =
        "user.EventTypeId = 'ContactCreated' AND user.MessageType = 'EventRequest' AND user.From IS NULL";

    // The forwarder filter provisioned before the MessageType guard: a migration fixture.
    private const string PreviousContactCreatedFilter =
        "user.EventTypeId = 'ContactCreated' AND user.From IS NULL";

    private static TestPlatform CrmErpPlatform() => new(
        new[] { new DynamicForward("CrmEndpoint", "LegacyOrderPlaced", "ErpEndpoint") },
        new EventEndpoint("CrmEndpoint", produces: new[] { "ContactCreated" }, consumes: new[] { "ContactCreated" }),
        new EventEndpoint("ErpEndpoint", produces: new[] { "ContactCreated" }, consumes: new[] { "ContactCreated" }));

    /// <summary>
    /// Provisions the CRM/ERP topology, then puts the CrmEndpoint→ErpEndpoint forwarder rule
    /// back to the filter older releases laid down, as an existing namespace would have it.
    /// </summary>
    private static async Task<(RecordingAdministrationClient Client, ServiceBusTopologyProvisioner Sut, string Action)> SeedPreviousForwarderRuleAsync(string connectionString)
    {
        var client = new RecordingAdministrationClient();
        var sut = CreateProvisioner(client, CrmErpPlatform(), connectionString);
        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        var action = TopologyDescriptor.ForwardAction("CrmEndpoint", "ErpEndpoint");
        client.SeedRule("CrmEndpoint", "ErpEndpoint", ServiceBusModelFactory.RuleProperties(
            "ContactCreated", new SqlRuleFilter(PreviousContactCreatedFilter), new SqlRuleAction(action)));
        client.ResetRecords();
        return (client, sut, action);
    }

    [Fact]
    public async Task ApplyAsync_RealNamespace_UpdatesMismatchedForwarderRuleInPlace()
    {
        // Deleting and recreating the rule would leave a window in which publishes of the
        // type are not forwarded; a real namespace replaces the rule in one call.
        var (client, sut, action) = await SeedPreviousForwarderRuleAsync(RealNamespaceConnectionString);

        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        var updated = Assert.Single(client.UpdatedRules);
        Assert.Equal(("CrmEndpoint", "ErpEndpoint", "ContactCreated"), (updated.TopicName, updated.SubscriptionName, updated.RuleName));
        Assert.Equal(NewContactCreatedFilter, updated.Filter, StringComparer.Ordinal);
        Assert.Equal(action, updated.Action, StringComparer.Ordinal);
        Assert.DoesNotContain(client.DeletedRules, rule => rule.RuleName == "ContactCreated");
        Assert.DoesNotContain(client.CreatedRules, rule => rule.Rule.Name == "ContactCreated");
    }

    [Fact]
    public async Task ApplyAsync_Emulator_RecreatesMismatchedRule()
    {
        // The emulator's admin endpoint rejects a PUT for a rule that exists, so it keeps
        // delete-then-create.
        var (client, sut, action) = await SeedPreviousForwarderRuleAsync(EmulatorConnectionString);

        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        Assert.Empty(client.UpdatedRules);
        Assert.Equal(
            new[] { "delete CrmEndpoint/ErpEndpoint/ContactCreated", "create CrmEndpoint/ErpEndpoint/ContactCreated" },
            client.Operations);
        var created = Assert.Single(client.CreatedRules).Rule;
        Assert.Equal(NewContactCreatedFilter, ((SqlRuleFilter)created.Filter).SqlExpression, StringComparer.Ordinal);
        Assert.Equal(action, ((SqlRuleAction)created.Action).SqlExpression, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(RealNamespaceConnectionString)]
    [InlineData(EmulatorConnectionString)]
    public async Task ApplyAsync_WhenEveryRuleMatches_WritesNoRule(string connectionString)
    {
        var client = new RecordingAdministrationClient();
        var sut = CreateProvisioner(client, CrmErpPlatform(), connectionString);
        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);
        // The first apply removes every $Default it should; nothing is left to clean up.
        Assert.All(client.DeletedRules, rule => Assert.Equal(TopologyDescriptor.DefaultRuleName, rule.RuleName));
        client.ResetRecords();

        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        Assert.Empty(client.UpdatedRules);
        Assert.Empty(client.CreatedRules);
        Assert.Empty(client.DeletedRules);
    }

    [Fact]
    public async Task Rebuilder_LegacyConstructor_UpdatesMismatchedForwarderRuleInPlace()
    {
        var (client, expected) = SeedSubscriptionWithPreviousForwarderRule();
        var sut = new ServiceBusTopologyRebuilder(client);

        await sut.EnsureSubscriptionAsync("CrmEndpoint", expected);

        Assert.False(sut.IsEmulator);
        var updated = Assert.Single(client.UpdatedRules);
        Assert.Equal("ContactCreated", updated.RuleName);
        Assert.Equal(NewContactCreatedFilter, updated.Filter, StringComparer.Ordinal);
        Assert.Equal(expected.Rules.Single().Action, updated.Action, StringComparer.Ordinal);
        Assert.Empty(client.CreatedRules);
        Assert.DoesNotContain(client.DeletedRules, rule => rule.RuleName == "ContactCreated");
    }

    [Fact]
    public async Task Rebuilder_OnEmulator_RecreatesMismatchedForwarderRule()
    {
        var (client, expected) = SeedSubscriptionWithPreviousForwarderRule();
        var sut = new ServiceBusTopologyRebuilder(client, isEmulator: true);

        await sut.EnsureSubscriptionAsync("CrmEndpoint", expected);

        Assert.True(sut.IsEmulator);
        Assert.Empty(client.UpdatedRules);
        Assert.Equal(
            new[] { "delete CrmEndpoint/ErpEndpoint/ContactCreated", "create CrmEndpoint/ErpEndpoint/ContactCreated" },
            client.Operations);
        var created = Assert.Single(client.CreatedRules).Rule;
        Assert.Equal(NewContactCreatedFilter, ((SqlRuleFilter)created.Filter).SqlExpression, StringComparer.Ordinal);
    }

    /// <summary>
    /// An existing, Active CrmEndpoint→ErpEndpoint forwarder whose ContactCreated rule still
    /// carries the previous filter — a mismatch that survives into a WebApp rebuild.
    /// </summary>
    private static (RecordingAdministrationClient Client, ExpectedSubscription Expected) SeedSubscriptionWithPreviousForwarderRule()
    {
        var expected = TopologyDescriptor.ConsumerForwarderSubscription("CrmEndpoint", "ErpEndpoint", new[] { "ContactCreated" });
        var client = new RecordingAdministrationClient();
        client.SeedTopic("CrmEndpoint");
        client.SeedSubscription("CrmEndpoint", MakeSubscriptionProperties("CrmEndpoint", "ErpEndpoint",
            requiresSession: false, forwardTo: "ErpEndpoint"));
        client.SeedRule("CrmEndpoint", "ErpEndpoint", ServiceBusModelFactory.RuleProperties(
            "ContactCreated",
            new SqlRuleFilter(PreviousContactCreatedFilter),
            new SqlRuleAction(TopologyDescriptor.ForwardAction("CrmEndpoint", "ErpEndpoint"))));
        return (client, expected);
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
    public async Task ApplyAsync_CreatesEverySubscriptionWithAClosedInitialRuleUnlessItKeepsDefault()
    {
        // Service Bus seeds a rule-less subscription with a true-filter $Default, so between
        // the create call and the $Default delete it would match everything published to the
        // topic. Creating it with a false-filter $Default keeps it closed until its real rules
        // are in place.
        var client = new RecordingAdministrationClient();

        var crm = new EventEndpoint("CrmEndpoint", produces: new[] { "AccountCreated", "ContactCreated" }, consumes: new[] { "ContactCreated" });
        var erp = new EventEndpoint("ErpEndpoint", produces: Array.Empty<string>(), consumes: new[] { "AccountCreated", "ContactCreated" });
        var platform = new TestPlatform(new[] { new DynamicForward("CrmEndpoint", "LegacyOrderPlaced", "ErpEndpoint") }, crm, erp);

        var sut = CreateProvisioner(client, platform);
        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        var checkedClosed = 0;
        foreach (var topicName in new[] { Constants.ResolverId, "CrmEndpoint", "ErpEndpoint" })
        {
            foreach (var expected in TopologyDescriptor.ForTopic(topicName, platform))
            {
                var initialRules = client.CreatedSubscriptionInitialRules
                    .Where(created => created.TopicName == topicName && created.SubscriptionName == expected.Name)
                    .ToList();

                if (expected.KeepDefaultRule)
                {
                    Assert.Empty(initialRules);
                    continue;
                }

                var initial = Assert.Single(initialRules).Rule;
                Assert.Equal(TopologyDescriptor.DefaultRuleName, initial.Name);
                Assert.IsNotType<TrueRuleFilter>(initial.Filter);
                var filter = Assert.IsType<FalseRuleFilter>(initial.Filter);
                Assert.Equal("1=0", filter.SqlExpression);
                checkedClosed++;
            }
        }

        Assert.True(checkedClosed > 0);
        Assert.Contains(client.CreatedSubscriptions, created => created.TopicName == Constants.ResolverId && created.SubscriptionName == Constants.ResolverId);
    }

    [Fact]
    public async Task Rebuilder_RecreatesASubscriptionWithAClosedInitialRule()
    {
        // The WebApp's "Delete & recreate" deletes the subscription and rebuilds it through
        // the rebuilder on a live topic.
        var platform = new TestPlatform(new TestEndpoint("orders"));
        var expected = TopologyDescriptor.FindSubscription("orders", "orders", platform)!;
        Assert.NotNull(expected);
        var client = new RecordingAdministrationClient();
        client.SeedTopic("orders");

        await new ServiceBusTopologyRebuilder(client).EnsureSubscriptionAsync("orders", expected);

        var initial = Assert.Single(client.CreatedSubscriptionInitialRules, created =>
            created.TopicName == "orders" && created.SubscriptionName == "orders").Rule;
        Assert.Equal(TopologyDescriptor.DefaultRuleName, initial.Name);
        Assert.Equal("1=0", Assert.IsType<FalseRuleFilter>(initial.Filter).SqlExpression);

        Assert.Contains(client.DeletedRules, rule =>
            rule.TopicName == "orders" && rule.SubscriptionName == "orders" && rule.RuleName == TopologyDescriptor.DefaultRuleName);
        Assert.Equal(
            expected.Rules.Select(rule => rule.Name).OrderBy(name => name, StringComparer.Ordinal),
            client.CreatedRules
                .Where(rule => rule.TopicName == "orders" && rule.SubscriptionName == "orders")
                .Select(rule => rule.Rule.Name)
                .OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(
            new[] { $"delete orders/orders/{TopologyDescriptor.DefaultRuleName}" },
            client.Operations.Where(operation => operation.StartsWith("delete", StringComparison.Ordinal)));
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
    public void DeferredSubscription_KeepsParkedMessagesUntilReplayedAndDeadLettersAnyThatExpire()
    {
        // A parked message waits for its blocking event to be resolved, which can take an
        // operator weeks. A capped TTL silently dropped it, leaving the row Deferred forever
        // with nothing to replay.
        var deferred = TopologyDescriptor.DeferredSubscription();
        Assert.Equal(TimeSpan.MaxValue, deferred.DefaultMessageTimeToLive);
        Assert.True(deferred.DeadLetterOnMessageExpiration);

        Assert.Equal(TimeSpan.FromHours(1), TopologyDescriptor.DeferredSubscription(isEmulator: true).DefaultMessageTimeToLive);
    }

    [Fact]
    public async Task ApplyAsync_CreatesDeferredSubscriptionWithUnboundedTtlAndDeadLetteringOnExpiration()
    {
        var client = new RecordingAdministrationClient();
        var sut = CreateProvisioner(client, new TestPlatform(new TestEndpoint("orders")));

        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        var deferred = Assert.Single(client.CreatedSubscriptions, x =>
            x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredSubscriptionName);
        Assert.Equal(TimeSpan.MaxValue, deferred.DefaultMessageTimeToLive);
        Assert.True(deferred.DeadLetteringOnMessageExpiration);
    }

    [Fact]
    public async Task ApplyAsync_UpdatesAnExistingDeferredSubscriptionInPlaceWithoutRecreatingIt()
    {
        // Existing namespaces were provisioned with a 14-day TTL. Recreating the subscription
        // would delete every parked message, so the settings must change in place.
        var client = new RecordingAdministrationClient();
        client.SeedTopic("orders");
        client.SeedSubscription("orders", MakeSubscriptionProperties("orders", Constants.DeferredSubscriptionName,
            requiresSession: true, defaultMessageTimeToLive: TimeSpan.FromDays(14)));

        var sut = CreateProvisioner(client, new TestPlatform(new TestEndpoint("orders")));

        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        Assert.DoesNotContain(client.DeletedSubscriptions, x => x.SubscriptionName == Constants.DeferredSubscriptionName);
        Assert.DoesNotContain(client.CreatedSubscriptions, x => x.SubscriptionName == Constants.DeferredSubscriptionName);
        var updated = Assert.Single(client.UpdatedSubscriptions);
        Assert.Equal(("orders", Constants.DeferredSubscriptionName), (updated.TopicName, updated.SubscriptionName));
        Assert.Equal(TimeSpan.MaxValue, updated.DefaultMessageTimeToLive);
        Assert.True(updated.DeadLetteringOnMessageExpiration);
    }

    [Fact]
    public async Task ApplyAsync_LeavesSubscriptionsThatAlreadyMatchUntouched()
    {
        var client = new RecordingAdministrationClient();
        var sut = CreateProvisioner(client, new TestPlatform(new TestEndpoint("orders")));

        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);
        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        Assert.Empty(client.UpdatedSubscriptions);
    }

    [Fact]
    public async Task ApplyAsync_LeavesAPausedForwardingSubscriptionAndItsBacklogInPlace()
    {
        // The WebApp's Pause on a forwarding subscription sets ReceiveDisabled and detaches
        // ForwardTo so messages collect in it. Deleting and recreating it over the missing
        // forward target would discard that backlog and silently undo the pause.
        var client = new RecordingAdministrationClient();
        client.SeedTopic("CrmEndpoint");
        client.SeedTopic("ErpEndpoint");
        client.SeedSubscription("CrmEndpoint", MakeSubscriptionProperties("CrmEndpoint", "ErpEndpoint",
            requiresSession: false, forwardTo: string.Empty, status: EntityStatus.ReceiveDisabled));

        var crm = new EventEndpoint("CrmEndpoint", produces: new[] { "ContactCreated" }, consumes: Array.Empty<string>());
        var erp = new EventEndpoint("ErpEndpoint", produces: Array.Empty<string>(), consumes: new[] { "ContactCreated" });
        var sut = CreateProvisioner(client, new TestPlatform(crm, erp));

        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        Assert.DoesNotContain(client.DeletedSubscriptions, x => x.TopicName == "CrmEndpoint" && x.SubscriptionName == "ErpEndpoint");
        Assert.DoesNotContain(client.CreatedSubscriptions, x => x.TopicName == "CrmEndpoint" && x.SubscriptionName == "ErpEndpoint");
        Assert.DoesNotContain(client.UpdatedSubscriptions, x => x.TopicName == "CrmEndpoint" && x.SubscriptionName == "ErpEndpoint");

        var stillPaused = client.GetSeeded("CrmEndpoint", "ErpEndpoint");
        Assert.Equal(EntityStatus.ReceiveDisabled, stillPaused.Status);
        Assert.True(string.IsNullOrEmpty(stillPaused.ForwardTo));

        // Rules are still ensured, so resuming forwards exactly what the topology describes.
        Assert.Contains(client.CreatedRules, r => r.TopicName == "CrmEndpoint" && r.SubscriptionName == "ErpEndpoint" && r.Rule.Name == "ContactCreated");
    }

    [Fact]
    public async Task ApplyAsync_CorrectsTheForwardTargetOfAnActiveSubscriptionInPlace()
    {
        // A forward target that is missing or wrong on an Active subscription can change in
        // place; recreating would delete whatever the subscription holds.
        var client = new RecordingAdministrationClient();
        client.SeedTopic("CrmEndpoint");
        client.SeedTopic("ErpEndpoint");
        client.SeedSubscription("CrmEndpoint", MakeSubscriptionProperties("CrmEndpoint", "ErpEndpoint",
            requiresSession: false, forwardTo: "SomewhereElse"));

        var crm = new EventEndpoint("CrmEndpoint", produces: new[] { "ContactCreated" }, consumes: Array.Empty<string>());
        var erp = new EventEndpoint("ErpEndpoint", produces: Array.Empty<string>(), consumes: new[] { "ContactCreated" });
        var sut = CreateProvisioner(client, new TestPlatform(crm, erp));

        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        Assert.DoesNotContain(client.DeletedSubscriptions, x => x.TopicName == "CrmEndpoint" && x.SubscriptionName == "ErpEndpoint");
        Assert.DoesNotContain(client.CreatedSubscriptions, x => x.TopicName == "CrmEndpoint" && x.SubscriptionName == "ErpEndpoint");
        var updated = Assert.Single(client.UpdatedSubscriptions, x => x.TopicName == "CrmEndpoint" && x.SubscriptionName == "ErpEndpoint");
        Assert.Equal("ErpEndpoint", updated.ForwardTo);
        Assert.Equal(EntityStatus.Active, updated.Status);
    }

    [Fact]
    public async Task ApplyAsync_LeavesAForwarderTheWebAppIsPurgingUntouched()
    {
        // A WebApp Purge of a paused forwarder makes it Active for the drain, with ForwardTo
        // still detached. Without the purge marker, apply would re-attach forwarding mid-drain
        // and hand the backlog the operator chose to discard to the destination.
        var client = new RecordingAdministrationClient();
        SeedDetachedActiveForwarder(client, SubscriptionMarkers.PurgeInProgress);

        await CreateProvisioner(client, ContactForwardPlatform()).ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        Assert.DoesNotContain(client.UpdatedSubscriptions, x => x.TopicName == "CrmEndpoint" && x.SubscriptionName == "ErpEndpoint");
        Assert.DoesNotContain(client.DeletedSubscriptions, x => x.TopicName == "CrmEndpoint" && x.SubscriptionName == "ErpEndpoint");
        Assert.DoesNotContain(client.CreatedSubscriptions, x => x.TopicName == "CrmEndpoint" && x.SubscriptionName == "ErpEndpoint");

        var stillDraining = client.GetSeeded("CrmEndpoint", "ErpEndpoint");
        Assert.Equal(EntityStatus.Active, stillDraining.Status);
        Assert.True(string.IsNullOrEmpty(stillDraining.ForwardTo));

        Assert.Contains(client.CreatedRules, r => r.TopicName == "CrmEndpoint" && r.SubscriptionName == "ErpEndpoint" && r.Rule.Name == "ContactCreated");
    }

    [Fact]
    public async Task ApplyAsync_SaysWhyItSkippedAMarkedForwarderAndHowToClearTheMarker()
    {
        var client = new RecordingAdministrationClient();
        SeedDetachedActiveForwarder(client, SubscriptionMarkers.PurgeInProgress);
        var log = new List<string>();
        var platform = ContactForwardPlatform();
        var sut = new NimBus.ServiceBus.Provisioning.ServiceBusTopologyProvisioner(client, () => platform, log.Add);

        await sut.ApplyAsync(CancellationToken.None);

        var line = Assert.Single(log, l => l.Contains("'ErpEndpoint' on topic 'CrmEndpoint'", StringComparison.Ordinal));
        Assert.Contains("purge marker", line, StringComparison.Ordinal);
        Assert.Contains("Pause", line, StringComparison.Ordinal);
        Assert.DoesNotContain("until the purge", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("owner: team-a")]
    public async Task ApplyAsync_StillReattachesAnUnmarkedDetachedActiveForwarder(string userMetadata)
    {
        // The marker, not "Active and detached", is what makes apply hold back: an unmarked
        // forwarder that lost its target is still corrected in place.
        var client = new RecordingAdministrationClient();
        SeedDetachedActiveForwarder(client, userMetadata);

        await CreateProvisioner(client, ContactForwardPlatform()).ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        Assert.DoesNotContain(client.DeletedSubscriptions, x => x.TopicName == "CrmEndpoint" && x.SubscriptionName == "ErpEndpoint");
        var updated = Assert.Single(client.UpdatedSubscriptions, x => x.TopicName == "CrmEndpoint" && x.SubscriptionName == "ErpEndpoint");
        Assert.Equal("ErpEndpoint", updated.ForwardTo);
    }

    [Fact]
    public async Task ApplyAsync_StillRecreatesAMarkedForwarderWhoseSessionFlagIsWrong()
    {
        var client = new RecordingAdministrationClient();
        client.SeedTopic("CrmEndpoint");
        client.SeedTopic("ErpEndpoint");
        client.SeedSubscription("CrmEndpoint", MakeSubscriptionProperties("CrmEndpoint", "ErpEndpoint",
            requiresSession: true, forwardTo: string.Empty, userMetadata: SubscriptionMarkers.PurgeInProgress));

        await CreateProvisioner(client, ContactForwardPlatform()).ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        Assert.Contains(client.DeletedSubscriptions, x => x.TopicName == "CrmEndpoint" && x.SubscriptionName == "ErpEndpoint");
        Assert.Contains(client.CreatedSubscriptions, x => x.TopicName == "CrmEndpoint" && x.SubscriptionName == "ErpEndpoint");
    }

    private static void SeedDetachedActiveForwarder(RecordingAdministrationClient client, string userMetadata)
    {
        client.SeedTopic("CrmEndpoint");
        client.SeedTopic("ErpEndpoint");
        client.SeedSubscription("CrmEndpoint", MakeSubscriptionProperties("CrmEndpoint", "ErpEndpoint",
            requiresSession: false, forwardTo: string.Empty, status: EntityStatus.Active, userMetadata: userMetadata));
    }

    /// <summary>CrmEndpoint publishes ContactCreated, ErpEndpoint consumes it: one forwarder.</summary>
    private static TestPlatform ContactForwardPlatform() => new(
        new EventEndpoint("CrmEndpoint", produces: new[] { "ContactCreated" }, consumes: Array.Empty<string>()),
        new EventEndpoint("ErpEndpoint", produces: Array.Empty<string>(), consumes: new[] { "ContactCreated" }));

    private static SubscriptionProperties MakeSubscriptionProperties(
        string topicName, string subscriptionName,
        bool requiresSession = false, string? forwardTo = null,
        TimeSpan? defaultMessageTimeToLive = null, bool deadLetteringOnMessageExpiration = false,
        EntityStatus? status = null,
        // The model factory rejects null UserMetadata, so "none" is string.Empty here.
        string userMetadata = "") =>
        ServiceBusModelFactory.SubscriptionProperties(
            topicName, subscriptionName,
            lockDuration: TimeSpan.FromMinutes(1),
            requiresSession: requiresSession,
            defaultMessageTimeToLive: defaultMessageTimeToLive ?? TimeSpan.MaxValue,
            autoDeleteOnIdle: TimeSpan.MaxValue,
            deadLetteringOnMessageExpiration: deadLetteringOnMessageExpiration,
            maxDeliveryCount: 10,
            enableBatchedOperations: true,
            status: status ?? EntityStatus.Active,
            forwardTo: forwardTo,
            forwardDeadLetteredMessagesTo: string.Empty,
            userMetadata: userMetadata);

    // ───────────────────────── Routing keys (Spec 045) ─────────────────────────

    [RoutingKey(nameof(Instance))]
    public sealed class RoutedCustomerCreated : Event
    {
        public string Instance { get; set; } = "";
    }

    private static readonly IEventType Routed = new EventType<RoutedCustomerCreated>();
    private static readonly TopologyOptions Options = new("nimbus", "dev", "rg-test");
    private static string Plain => TopologyDescriptor.ForwardFilter(nameof(RoutedCustomerCreated));

    private static TestPlatform RoutedPlatform(
        IReadOnlyDictionary<string, string[]> keyed,
        string? unmatched = "ErpUnrouted",
        bool reporting = false)
    {
        var endpoints = new List<IEndpoint> { new KeyedEndpoint("CrmEndpoint", produces: true) };
        endpoints.AddRange(keyed.Select(pair => new KeyedEndpoint(pair.Key, keys: pair.Value)));
        if (unmatched is not null)
            endpoints.Add(new KeyedEndpoint(unmatched, unmatched: true));
        if (reporting)
            endpoints.Add(new KeyedEndpoint("Reporting"));
        return new TestPlatform(endpoints.ToArray());
    }

    private static string RuleFilter(RecordingAdministrationClient client, string subscription) =>
        ((SqlRuleFilter)client.GetSeededRule("CrmEndpoint", subscription, nameof(RoutedCustomerCreated)).Filter).SqlExpression;

    private static async Task ApplyAsync(RecordingAdministrationClient client, IPlatform platform, string connectionString = RealNamespaceConnectionString) =>
        await CreateProvisioner(client, platform, connectionString).ApplyAsync(Options, CancellationToken.None);

    [Fact]
    public async Task ApplyAsync_RoutingKeys_WritesTheSpecFilters()
    {
        var client = new RecordingAdministrationClient();

        await ApplyAsync(client, RoutedPlatform(
            new Dictionary<string, string[]> { ["Erp01"] = ["01"], ["Erp02"] = ["03", "02"] },
            reporting: true));

        Assert.Equal($"{Plain} AND user.RoutingKey = '01'", RuleFilter(client, "Erp01"), StringComparer.Ordinal);
        Assert.Equal($"{Plain} AND user.RoutingKey IN ('02', '03')", RuleFilter(client, "Erp02"), StringComparer.Ordinal);
        Assert.Equal(
            $"{Plain} AND (user.RoutingKey IS NULL OR user.RoutingKey NOT IN ('01', '02', '03'))",
            RuleFilter(client, "ErpUnrouted"),
            StringComparer.Ordinal);
        Assert.Equal(Plain, RuleFilter(client, "Reporting"), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ApplyAsync_UnmatchedConsumerWithoutKeyedConsumers_KeepsThePlainFilter()
    {
        // Keyed events must keep flowing between "publishers stamp keys" and "the first
        // instance endpoint exists" (Spec 045 §4, last row of the filter table).
        var client = new RecordingAdministrationClient();

        await ApplyAsync(client, RoutedPlatform(new Dictionary<string, string[]>()));

        Assert.Equal(Plain, RuleFilter(client, "ErpUnrouted"), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("AUnrouted")]
    [InlineData("ZUnrouted")]
    public async Task ApplyAsync_AddingAKey_WidensTheInstanceBeforeNarrowingTheUnmatchedRule(string unmatched)
    {
        var client = new RecordingAdministrationClient();
        await ApplyAsync(client, RoutedPlatform(new Dictionary<string, string[]> { ["Erp01"] = ["01"], ["Erp02"] = ["02"] }, unmatched));
        client.ResetRecords();

        await ApplyAsync(client, RoutedPlatform(new Dictionary<string, string[]> { ["Erp01"] = ["01"], ["Erp02"] = ["02", "03"] }, unmatched));

        var widen = client.Operations.IndexOf($"update CrmEndpoint/Erp02/{nameof(RoutedCustomerCreated)}");
        var narrow = client.Operations.IndexOf($"update CrmEndpoint/{unmatched}/{nameof(RoutedCustomerCreated)}");
        Assert.True(widen >= 0 && narrow >= 0, string.Join(", ", client.Operations));
        Assert.True(widen < narrow, string.Join(", ", client.Operations));
        Assert.Equal(2, client.UpdatedRules.Count);
        Assert.Equal($"{Plain} AND user.RoutingKey IN ('02', '03')", RuleFilter(client, "Erp02"), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ApplyAsync_MovingAKey_WidensTheNewOwnerBeforeNarrowingTheOldOne()
    {
        // Erp01 sorts before Erp02, so an ordinary single pass would narrow Erp01 first and
        // leave key 05 matched by nobody until Erp02 caught up.
        var client = new RecordingAdministrationClient();
        await ApplyAsync(client, RoutedPlatform(new Dictionary<string, string[]> { ["Erp01"] = ["01", "05"], ["Erp02"] = ["02"] }));
        client.ResetRecords();

        await ApplyAsync(client, RoutedPlatform(new Dictionary<string, string[]> { ["Erp01"] = ["01"], ["Erp02"] = ["02", "05"] }));

        Assert.Equal(
            new[]
            {
                $"update CrmEndpoint/Erp02/{nameof(RoutedCustomerCreated)}",
                $"update CrmEndpoint/Erp01/{nameof(RoutedCustomerCreated)}",
            },
            client.Operations);
        Assert.Equal($"{Plain} AND user.RoutingKey = '01'", RuleFilter(client, "Erp01"), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ApplyAsync_RemovingAKey_WidensTheUnmatchedRuleBeforeNarrowingTheInstance()
    {
        var client = new RecordingAdministrationClient();
        await ApplyAsync(client, RoutedPlatform(new Dictionary<string, string[]> { ["Erp01"] = ["01"], ["Erp02"] = ["02", "03"] }, "AUnrouted"));
        client.ResetRecords();

        await ApplyAsync(client, RoutedPlatform(new Dictionary<string, string[]> { ["Erp01"] = ["01"], ["Erp02"] = ["02"] }, "AUnrouted"));

        Assert.Equal(
            new[]
            {
                $"update CrmEndpoint/AUnrouted/{nameof(RoutedCustomerCreated)}",
                $"update CrmEndpoint/Erp02/{nameof(RoutedCustomerCreated)}",
            },
            client.Operations);
    }

    [Fact]
    public async Task ApplyAsync_NewInstanceEndpoint_IsCreatedBeforeTheUnmatchedRuleNarrows()
    {
        var client = new RecordingAdministrationClient();
        await ApplyAsync(client, RoutedPlatform(new Dictionary<string, string[]> { ["Erp01"] = ["01"] }, "AUnrouted"));
        client.ResetRecords();

        await ApplyAsync(client, RoutedPlatform(new Dictionary<string, string[]> { ["Erp01"] = ["01"], ["Erp02"] = ["02"] }, "AUnrouted"));

        var created = client.Operations.FindIndex(operation => operation.StartsWith("create CrmEndpoint/Erp02/", StringComparison.Ordinal));
        var narrow = client.Operations.IndexOf($"update CrmEndpoint/AUnrouted/{nameof(RoutedCustomerCreated)}");
        Assert.True(created >= 0 && narrow > created, string.Join(", ", client.Operations));
    }

    [Theory]
    [InlineData(RealNamespaceConnectionString)]
    [InlineData(EmulatorConnectionString)]
    public async Task ApplyAsync_RoutingKeys_SecondApplyWritesNoRule(string connectionString)
    {
        var client = new RecordingAdministrationClient();
        var platform = RoutedPlatform(new Dictionary<string, string[]> { ["Erp01"] = ["01"], ["Erp02"] = ["02"] }, reporting: true);
        await ApplyAsync(client, platform, connectionString);
        client.ResetRecords();

        await ApplyAsync(client, platform, connectionString);

        Assert.Empty(client.Operations);
    }

    [Fact]
    public async Task ApplyAsync_Emulator_MovesAKeyByRecreatingRules()
    {
        var client = new RecordingAdministrationClient();
        await ApplyAsync(client, RoutedPlatform(new Dictionary<string, string[]> { ["Erp01"] = ["01", "05"], ["Erp02"] = ["02"] }), EmulatorConnectionString);
        client.ResetRecords();

        await ApplyAsync(client, RoutedPlatform(new Dictionary<string, string[]> { ["Erp01"] = ["01"], ["Erp02"] = ["02", "05"] }), EmulatorConnectionString);

        Assert.Empty(client.UpdatedRules);
        Assert.Equal($"{Plain} AND user.RoutingKey IN ('02', '05')", RuleFilter(client, "Erp02"), StringComparer.Ordinal);
        Assert.Equal($"{Plain} AND user.RoutingKey = '01'", RuleFilter(client, "Erp01"), StringComparer.Ordinal);
        Assert.True(
            client.Operations.IndexOf($"create CrmEndpoint/Erp02/{nameof(RoutedCustomerCreated)}")
                < client.Operations.IndexOf($"delete CrmEndpoint/Erp01/{nameof(RoutedCustomerCreated)}"),
            string.Join(", ", client.Operations));
    }

    [Fact]
    public async Task ApplyAsync_PausedForwarder_StillGetsItsRoutingRules()
    {
        var client = new RecordingAdministrationClient();
        await ApplyAsync(client, RoutedPlatform(new Dictionary<string, string[]> { ["Erp01"] = ["01"] }));
        client.SeedSubscription("CrmEndpoint", MakeSubscriptionProperties("CrmEndpoint", "Erp01", forwardTo: null, status: EntityStatus.ReceiveDisabled));
        client.ResetRecords();

        await ApplyAsync(client, RoutedPlatform(new Dictionary<string, string[]> { ["Erp01"] = ["01", "04"] }));

        Assert.Equal($"{Plain} AND user.RoutingKey IN ('01', '04')", RuleFilter(client, "Erp01"), StringComparer.Ordinal);
        Assert.Empty(client.UpdatedSubscriptions);
    }

    [Fact]
    public async Task ApplyAsync_KeyedConsumersWithoutUnmatchedConsumer_IsRejected()
    {
        var client = new RecordingAdministrationClient();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApplyAsync(client, RoutedPlatform(new Dictionary<string, string[]> { ["Erp01"] = ["01"] }, unmatched: null)));

        Assert.Contains("no unmatched consumer", error.Message, StringComparison.Ordinal);
        Assert.Empty(client.CreatedTopics);
    }

    private sealed class KeyedEndpoint : IEndpoint
    {
        private readonly string[] _keys;
        private readonly bool _unmatched;

        public KeyedEndpoint(string id, bool produces = false, string[]? keys = null, bool unmatched = false)
        {
            Id = id;
            _keys = keys ?? [];
            _unmatched = unmatched;
            EventTypesProduced = produces ? [Routed] : [];
            EventTypesConsumed = produces ? [] : [Routed];
        }

        public string Id { get; }
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced { get; }
        public IEnumerable<IEventType> EventTypesConsumed { get; }
        public IEnumerable<IRoleAssignment> RoleAssignments => Array.Empty<IRoleAssignment>();

        public IReadOnlyCollection<string> GetRoutingKeys(IEventType eventType) =>
            eventType.Id == Routed.Id ? RoutingKeys.Canonical(_keys) : [];

        public bool IsUnmatchedConsumer(IEventType eventType) => _unmatched && eventType.Id == Routed.Id;
    }

    private const string RealNamespaceConnectionString =
        "Endpoint=sb://example/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=test";

    private const string EmulatorConnectionString =
        "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    // The CLI wrapper derives isEmulator from the connection string, exactly as `nb topology apply` does.
    private static ServiceBusTopologyProvisioner CreateProvisioner(
        RecordingAdministrationClient client,
        IPlatform platform,
        string connectionString = RealNamespaceConnectionString) =>
        new(
            new AzureCliRunner(),
            (options, cancellationToken, runner) => Task.FromResult(connectionString),
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

        /// <summary>Forgets what was recorded so far, keeping the entities in place.</summary>
        public void ResetRecords()
        {
            CreatedSubscriptions.Clear();
            CreatedSubscriptionInitialRules.Clear();
            DeletedSubscriptions.Clear();
            UpdatedSubscriptions.Clear();
            CreatedRules.Clear();
            DeletedRules.Clear();
            UpdatedRules.Clear();
            CreatedTopics.Clear();
            Operations.Clear();
        }

        public void SeedTopic(string topicName) => _topics.Add(topicName);

        public SubscriptionProperties GetSeeded(string topicName, string subscriptionName) =>
            _subscriptions[(topicName, subscriptionName)];

        public void SeedSubscription(string topicName, SubscriptionProperties properties) =>
            _subscriptions[(topicName, properties.SubscriptionName)] = properties;

        public void SeedRule(string topicName, string subscriptionName, RuleProperties properties) =>
            _rules[(topicName, subscriptionName, properties.Name)] = properties;

        public RuleProperties GetSeededRule(string topicName, string subscriptionName, string ruleName) =>
            _rules[(topicName, subscriptionName, ruleName)];

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
                forwardTo: _forwardToNormalizer(options.ForwardTo),
                defaultMessageTimeToLive: options.DefaultMessageTimeToLive,
                deadLetteringOnMessageExpiration: options.DeadLetteringOnMessageExpiration);
            _subscriptions[(options.TopicName, options.SubscriptionName)] = subscription;
            // Azure Service Bus auto-creates a $Default rule on new subscriptions
            _rules[(options.TopicName, options.SubscriptionName, "$Default")] =
                ServiceBusModelFactory.RuleProperties("$Default", new TrueRuleFilter());
            return Task.FromResult(Response.FromValue(subscription, FakeResponse.Instance));
        }

        /// <summary>Subscriptions created with an initial rule, and the rule they were created with.</summary>
        public List<(string TopicName, string SubscriptionName, CreateRuleOptions Rule)> CreatedSubscriptionInitialRules { get; } = new();

        public override Task<Response<SubscriptionProperties>> CreateSubscriptionAsync(CreateSubscriptionOptions options, CreateRuleOptions rule, CancellationToken cancellationToken = default)
        {
            CreatedSubscriptionInitialRules.Add((options.TopicName, options.SubscriptionName, rule));
            CreatedSubscriptions.Add(options);
            var subscription = MakeSubscriptionProperties(
                options.TopicName,
                options.SubscriptionName,
                requiresSession: options.RequiresSession,
                forwardTo: _forwardToNormalizer(options.ForwardTo),
                defaultMessageTimeToLive: options.DefaultMessageTimeToLive,
                deadLetteringOnMessageExpiration: options.DeadLetteringOnMessageExpiration);
            _subscriptions[(options.TopicName, options.SubscriptionName)] = subscription;
            // Created with an explicit rule, Service Bus seeds that rule instead of $Default.
            _rules[(options.TopicName, options.SubscriptionName, rule.Name)] =
                ServiceBusModelFactory.RuleProperties(rule.Name, rule.Filter, rule.Action);
            return Task.FromResult(Response.FromValue(subscription, FakeResponse.Instance));
        }

        public List<SubscriptionProperties> UpdatedSubscriptions { get; } = new();

        public override Task<Response<SubscriptionProperties>> UpdateSubscriptionAsync(SubscriptionProperties subscription, CancellationToken cancellationToken = default)
        {
            UpdatedSubscriptions.Add(subscription);
            _subscriptions[(subscription.TopicName, subscription.SubscriptionName)] = subscription;
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
            Operations.Add($"create {topicName}/{subscriptionName}/{ruleOptions.Name}");
            var rule = ServiceBusModelFactory.RuleProperties(ruleOptions.Name, ruleOptions.Filter, ruleOptions.Action);
            _rules[(topicName, subscriptionName, ruleOptions.Name)] = rule;
            return Task.FromResult(Response.FromValue(rule, FakeResponse.Instance));
        }

        public override Task<Response> DeleteRuleAsync(string topicName, string subscriptionName, string ruleName, CancellationToken cancellationToken = default)
        {
            DeletedRules.Add((topicName, subscriptionName, ruleName));
            Operations.Add($"delete {topicName}/{subscriptionName}/{ruleName}");
            _rules.Remove((topicName, subscriptionName, ruleName));
            return Task.FromResult<Response>(FakeResponse.Instance);
        }

        /// <summary>Rule updates, with the filter and action as they were sent.</summary>
        public List<(string TopicName, string SubscriptionName, string RuleName, string Filter, string Action)> UpdatedRules { get; } = new();

        /// <summary>Rule writes in the order they happened, for delete-then-create assertions.</summary>
        public List<string> Operations { get; } = new();

        public override Task<Response<RuleProperties>> UpdateRuleAsync(string topicName, string subscriptionName, RuleProperties rule, CancellationToken cancellationToken = default)
        {
            UpdatedRules.Add((
                topicName,
                subscriptionName,
                rule.Name,
                (rule.Filter as SqlRuleFilter)?.SqlExpression ?? string.Empty,
                (rule.Action as SqlRuleAction)?.SqlExpression ?? string.Empty));
            Operations.Add($"update {topicName}/{subscriptionName}/{rule.Name}");
            _rules[(topicName, subscriptionName, rule.Name)] = rule;
            return Task.FromResult(Response.FromValue(rule, FakeResponse.Instance));
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
        public static string SessionKeyProperty => string.Empty;
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


