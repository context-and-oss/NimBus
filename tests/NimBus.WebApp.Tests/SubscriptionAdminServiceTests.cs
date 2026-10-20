#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.WebApp.Services;
using NimBus.WebApp.ManagementApi;
using System.Threading;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The subscription admin's job is to be safe to reach for mid-incident: every action it
/// offers must either be reversible or refuse to run. These pin the refusals and the
/// reversals, since both only show up on a live namespace otherwise.
/// </summary>
[TestClass]
public sealed class SubscriptionAdminServiceTests
{
    private const string Topic = "orders";
    // NimBus.WebApp.Constants is a namespace reachable from this one, so it shadows a
    // `using Constants = NimBus.Core.Messages.Constants` alias. Name the ids directly.
    private const string ResolverSubscription = "Resolver";
    private const string DeferredProcessorSubscription = "DeferredProcessor";

    [TestMethod]
    public async Task Pause_DetachesForwardingInTheSameUpdate()
    {
        // Azure documents the destination-disabled case but not what ReceiveDisabled on the
        // SOURCE does to forwarding, so a pause detaches the destination rather than relying
        // on undocumented behaviour — and both changes must go out as one update, or the
        // pause can half-apply.
        var management = new FakeServiceBusManagement();
        management.SeedSubscription(Topic, ResolverSubscription, forwardTo: ResolverSubscription);
        var service = CreateService(management);

        var result = await service.SetSubscriptionStatusAsync(Topic, ResolverSubscription, enable: false);

        Assert.IsTrue(result.Succeeded);
        var update = management.Updates.Single();
        Assert.AreEqual(EntityStatus.ReceiveDisabled, update.Status);
        Assert.IsTrue(update.ChangeForwardTo);
        Assert.IsNull(update.ForwardTo);
    }

    [TestMethod]
    public async Task Pause_RefusesWhenTheDestinationCouldNotBePutBack()
    {
        // A pause that can't be undone is not a pause.
        var management = new FakeServiceBusManagement();
        management.SeedSubscription(Topic, "hand-made", forwardTo: "somewhere-else");
        var service = CreateService(management);

        var result = await service.SetSubscriptionStatusAsync(Topic, "hand-made", enable: false);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, management.Updates.Count);
        StringAssert.Contains(result.Message, "doesn't describe it", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Resume_RestoresTheForwardDestinationFromTheTopology()
    {
        var management = new FakeServiceBusManagement();
        management.SeedSubscription(Topic, ResolverSubscription, forwardTo: null, status: EntityStatus.ReceiveDisabled);
        var service = CreateService(management);

        var result = await service.SetSubscriptionStatusAsync(Topic, ResolverSubscription, enable: true);

        Assert.IsTrue(result.Succeeded);
        var update = management.Updates.Single();
        Assert.AreEqual(EntityStatus.Active, update.Status);
        Assert.IsTrue(update.ChangeForwardTo);
        Assert.AreEqual(ResolverSubscription, update.ForwardTo);
    }

    [TestMethod]
    public async Task Purge_RefusesAnAutoForwardingSubscription()
    {
        // Service Bus rejects receive on a forwarding entity, so a drain loop would remove
        // nothing while reporting success.
        var management = new FakeServiceBusManagement();
        management.SeedSubscription(Topic, ResolverSubscription, forwardTo: ResolverSubscription);
        var service = CreateService(management);

        var exception = await Assert.ThrowsExactlyAsync<SubscriptionPurgeNotSupportedException>(
            () => service.PurgeSubscriptionAsync(Topic, ResolverSubscription));

        StringAssert.Contains(exception.Message, "Delete & recreate", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Purge_MakesAPausedSubscriptionReceivableAndPausesItAgain()
    {
        // Service Bus also rejects receive on a ReceiveDisabled entity, so the advertised
        // Pause → Purge workflow silently removes nothing unless the drain re-enables it.
        var management = new FakeServiceBusManagement();
        management.SeedSubscription(Topic, DeferredProcessorSubscription, status: EntityStatus.ReceiveDisabled);
        var service = CreateService(management);

        await service.PurgeSubscriptionAsync(Topic, DeferredProcessorSubscription);

        CollectionAssert.AreEqual(
            new[] { EntityStatus.Active, EntityStatus.ReceiveDisabled },
            management.Updates.Select(update => update.Status).ToArray());
    }

    [TestMethod]
    public async Task Purge_ReportsWhenThePauseDidNotSurvive()
    {
        // Leaving it Active is recoverable, but an operator who thinks it is still paused
        // will not go looking.
        var management = new FakeServiceBusManagement { FailUpdateToStatus = EntityStatus.ReceiveDisabled };
        management.SeedSubscription(Topic, DeferredProcessorSubscription, status: EntityStatus.ReceiveDisabled);
        var service = CreateService(management);

        var result = await service.PurgeSubscriptionAsync(Topic, DeferredProcessorSubscription);

        // The drain itself needs a live namespace, so it fails here too — what matters is
        // that the failed restore is reported and not swallowed by the drain's own error.
        Assert.IsTrue(result.Errors.Any(error => error.Contains("is now Active", StringComparison.Ordinal)),
            $"Expected a restore-failure error, got: {string.Join(" | ", result.Errors)}");
    }

    [TestMethod]
    public async Task DetachRule_RefusesARuleTheTopologyCouldNotRestore()
    {
        // $Default on a hand-made subscription is that subscription's entire routing:
        // detaching it silences the subscription with no way back from this page.
        var management = new FakeServiceBusManagement();
        management.SeedSubscription(Topic, "hand-made", rules: ("$Default", "1=1", null));
        var service = CreateService(management);

        var result = await service.DeleteRuleAsync(Topic, "hand-made", "$Default");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, management.DeletedRules.Count);
    }

    [TestMethod]
    public async Task DetachRule_RemovesARuleTheTopologyCanPutBack()
    {
        var management = new FakeServiceBusManagement();
        management.SeedSubscription(Topic, Topic, requiresSession: true, rules: ($"to-{Topic}", $"user.To = '{Topic}'", null));
        var service = CreateService(management);

        var result = await service.DeleteRuleAsync(Topic, Topic, $"to-{Topic}");

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual((Topic, Topic, $"to-{Topic}"), management.DeletedRules.Single());
    }

    [TestMethod]
    public async Task RestoreRules_ReattachesOnlyWhatIsMissing()
    {
        var management = new FakeServiceBusManagement();
        management.SeedSubscription(Topic, Topic, requiresSession: true, rules: ($"to-{Topic}", $"user.To = '{Topic}'", null));
        var service = CreateService(management);

        var result = await service.RestoreRulesAsync(Topic, Topic);

        Assert.IsTrue(result.Succeeded);
        // The endpoint subscription also carries continuation and retry; to-{topic} is
        // already attached and must not be recreated.
        CollectionAssert.AreEquivalent(
            new[] { "continuation", "retry" },
            management.CreatedRules.Select(rule => rule.Rule).ToArray());
    }

    [TestMethod]
    public async Task Recreate_RefusesWhatThePlatformCannotDescribe()
    {
        // Offering a recreate here would delete the entity and leave it missing.
        var management = new FakeServiceBusManagement();
        management.SeedSubscription(Topic, "hand-made");
        var service = CreateService(management);

        await Assert.ThrowsExactlyAsync<SubscriptionNotDescribableException>(
            () => service.RecreateSubscriptionAsync(Topic, "hand-made"));

        Assert.AreEqual(0, management.DeletedSubscriptions.Count);
    }

    [TestMethod]
    public async Task Recreate_RebuildsFromTheDescribedTopology()
    {
        var management = new FakeServiceBusManagement();
        management.SeedSubscription(Topic, ResolverSubscription, forwardTo: ResolverSubscription);
        var rebuilder = new FakeTopologyRebuilder();
        var service = CreateService(management, rebuilder);

        var result = await service.RecreateSubscriptionAsync(Topic, ResolverSubscription);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual((Topic, ResolverSubscription), management.DeletedSubscriptions.Single());

        var rebuilt = rebuilder.Rebuilt.Single();
        Assert.AreEqual(Topic, rebuilt.Topic);
        Assert.AreEqual(ResolverSubscription, rebuilt.Expected.ForwardTo);
        CollectionAssert.AreEquivalent(
            new[] { $"from-{Topic}", $"to-{Topic}" },
            rebuilt.Expected.Rules.Select(rule => rule.Name).ToArray());
    }

    [TestMethod]
    public async Task Recreate_SaysSoLoudlyWhenTheEntityIsLeftMissing()
    {
        // The delete already happened, so a quiet failure leaves an operator believing a
        // button didn't work when in fact the subscription is gone.
        var management = new FakeServiceBusManagement();
        management.SeedSubscription(Topic, ResolverSubscription, forwardTo: ResolverSubscription);
        var service = CreateService(management, new FakeTopologyRebuilder { Fail = true });

        var result = await service.RecreateSubscriptionAsync(Topic, ResolverSubscription);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Message, "could not be recreated", StringComparison.Ordinal);
        Assert.AreEqual(1, result.Errors.Count);
    }

    [TestMethod]
    public async Task GetSubscriptions_MarksWhatCanBeRebuiltAndWhatIsMissing()
    {
        var management = new FakeServiceBusManagement();
        management.SeedTopic(Topic);
        management.SeedSubscription(Topic, Topic, requiresSession: true,
            rules: ($"to-{Topic}", $"user.To = '{Topic}'", null));
        management.SeedSubscription(Topic, "hand-made", rules: ("$Default", "1=1", null));
        var service = CreateService(management);

        var subscriptions = (await service.GetSubscriptionsAsync(Topic)).ToList();

        var endpoint = subscriptions.Single(subscription => subscription.Name == Topic);
        Assert.IsTrue(endpoint.CanRecreate);
        CollectionAssert.AreEquivalent(new[] { "continuation", "retry" }, endpoint.MissingRuleNames.ToArray());
        CollectionAssert.AreEqual(new[] { $"to-{Topic}" }, endpoint.DetachableRuleNames.ToArray());

        var handMade = subscriptions.Single(subscription => subscription.Name == "hand-made");
        Assert.IsFalse(handMade.CanRecreate);
        // Nothing on it is detachable, so the UI renders its rules as non-clickable chips.
        Assert.AreEqual(0, handMade.DetachableRuleNames.Count);
    }

    [TestMethod]
    public async Task GetTopicOverview_CountsBothDeadLetterQueuesSeparately()
    {
        // A failed auto-forward strands messages in the transfer DLQ, not the regular one.
        // Folding them together would hide the split; dropping them would report "zero dead
        // letters" in exactly the incident this page diagnoses.
        var management = new FakeServiceBusManagement();
        management.SeedTopic(Topic, active: 12, deadLetter: 3, transfer: 4, transferDeadLetter: 5);
        var service = CreateService(management);

        var overview = (await service.GetTopicOverviewAsync()).Single();

        Assert.AreEqual(12, overview.ActiveMessageCount);
        Assert.AreEqual(3, overview.DeadLetterMessageCount);
        Assert.AreEqual(4, overview.TransferMessageCount);
        Assert.AreEqual(5, overview.TransferDeadLetterMessageCount);
        Assert.IsTrue(overview.IsKnownToPlatform);
    }

    [TestMethod]
    public async Task GetTopicOverview_FlagsATopicThePlatformDoesNotOwn()
    {
        var management = new FakeServiceBusManagement();
        management.SeedTopic("someone-elses-topic");
        var service = CreateService(management);

        var overview = (await service.GetTopicOverviewAsync()).Single();

        Assert.IsFalse(overview.IsKnownToPlatform);
        Assert.IsFalse(overview.IsSystemTopic);
    }

    [TestMethod]
    public async Task ResolverDeadLetters_ValidatesTerminalSessionTargetBeforeDelegating()
    {
        var management = new FakeServiceBusManagement();
        management.SeedSubscription("Resolver", "Resolver", requiresSession: true);
        var replay = new RecordingResolverDeadLetterClient();
        var service = CreateService(management, replayClient: replay);

        await service.GetResolverDeadLettersAsync("Resolver");
        await service.ResubmitResolverDeadLettersAsync("Resolver", all: false, reason: "CosmosDbThrottled");

        CollectionAssert.AreEqual(
            new[] { "inspect:Resolver/Resolver", "replay:Resolver/Resolver:False:CosmosDbThrottled" },
            replay.Calls);
    }

    [TestMethod]
    public async Task ResolverDeadLetters_RejectsForwardingOrNonSessionTargetsWithoutDataPlaneCall()
    {
        var management = new FakeServiceBusManagement();
        management.SeedSubscription("Resolver", "Resolver", requiresSession: false, forwardTo: "elsewhere");
        var replay = new RecordingResolverDeadLetterClient();
        var service = CreateService(management, replayClient: replay);

        await Assert.ThrowsExactlyAsync<ResolverDeadLetterTargetNotSupportedException>(
            () => service.GetResolverDeadLettersAsync("Resolver"));

        Assert.AreEqual(0, replay.Calls.Count);
    }

    private static SubscriptionAdminService CreateService(
        FakeServiceBusManagement management,
        FakeTopologyRebuilder? rebuilder = null,
        IResolverDeadLetterClient? replayClient = null) =>
        new(
            new TestPlatform(new TestEndpoint(Topic)),
            management,
            rebuilder ?? new FakeTopologyRebuilder(),
            sbClient: null!,
            NullLogger<SubscriptionAdminService>.Instance,
            resolverDeadLetterClient: replayClient);

    private sealed class RecordingResolverDeadLetterClient : IResolverDeadLetterClient
    {
        public List<string> Calls { get; } = [];

        public Task<DeadLetterOverview> GetOverviewAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default)
        {
            Calls.Add($"inspect:{topicName}/{subscriptionName}");
            return Task.FromResult(new DeadLetterOverview());
        }

        public Task<BulkOperationResult> ResubmitAsync(string topicName, string subscriptionName, bool all, string? reason, CancellationToken cancellationToken = default)
        {
            Calls.Add($"replay:{topicName}/{subscriptionName}:{all}:{reason}");
            return Task.FromResult(new BulkOperationResult());
        }
    }

    private sealed class TestPlatform : Platform
    {
        public TestPlatform(params IEndpoint[] endpoints)
        {
            foreach (var endpoint in endpoints) AddEndpoint(endpoint);
        }
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
