#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Management.ServiceBus;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Messaging.ServiceBus.Administration;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class ServiceBusFilterValidatorTests
{
    [TestMethod]
    [DataRow(50)]
    [DataRow(51)]
    [DataRow(260)]
    public async Task TopicListings_AcceptTopicNamesUpToTheBrokerLimit(int length)
    {
        var topicName = new string('a', length);
        var client = new ListingAdministrationClient();
        var management = new ServiceBusManagement(client);

        await foreach (var _ in management.ListSubscriptionRuntimePropertiesAsync(topicName)) { }
        await foreach (var _ in management.ListSubscriptionsAsync(topicName)) { }
        await foreach (var _ in management.ListRulesAsync(topicName, "subscription")) { }

        Assert.AreEqual(3, client.ListingCalls);
        Assert.AreEqual(topicName, client.LastTopicName);
    }

    [TestMethod]
    [DataRow(261)]
    public async Task TopicListings_RejectNamesAboveTheBrokerLimit(int length)
    {
        var client = new ListingAdministrationClient();
        var management = new ServiceBusManagement(client);

        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in management.ListSubscriptionRuntimePropertiesAsync(new string('a', length))) { }
        });

        Assert.AreEqual("topicName", exception.ParamName);
        Assert.AreEqual(0, client.ListingCalls);
    }

    [TestMethod]
    [DataRow("a' OR 1=1 OR 'b")]
    [DataRow("foo;bar")]
    [DataRow("foo\nbar")]
    public async Task TopicListings_RejectFilterMetacharacters(string topicName)
    {
        var client = new ListingAdministrationClient();
        var management = new ServiceBusManagement(client);

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in management.ListSubscriptionRuntimePropertiesAsync(topicName)) { }
        });

        Assert.AreEqual(0, client.ListingCalls);
    }

    // The values that flow through ServiceBusFilterValidator in production:
    // NimBus endpoint ids, NimBus.Core.Messages.Constants ids, composed
    // subscription/rule names (e.g. "to-Crm", "from-Crm"), and Service Bus's
    // own $Default rule name. All must be accepted.
    [TestMethod]
    [DataRow("Alice")]
    [DataRow("CrmEndpoint")]
    [DataRow("ErpEndpoint")]
    [DataRow("Resolver")]
    [DataRow("Manager")]
    [DataRow("Continuation")]
    [DataRow("Retry")]
    [DataRow("Deferred")]
    [DataRow("DeferredProcessor")]
    [DataRow("$Default")]
    [DataRow("to-CrmEndpoint")]   // composed rule name
    [DataRow("from-CrmEndpoint")] // composed rule name
    [DataRow("a")]                // single char
    [DataRow("a.b_c-d$1")]        // every allowed char class
    [DataRow("crm.contact.enriched.v1")] // spec 022 agent-defined dynamic EventTypeId (namespaced)
    public void ValidateName_AcceptsValidValue(string value)
    {
        // Must not throw.
        ServiceBusFilterValidator.ValidateName(value, "param");
    }

    // Values containing characters that would terminate the surrounding
    // single-quoted SQL filter or inject filter syntax. Each must be
    // rejected before reaching SqlRuleFilter.
    [TestMethod]
    [DataRow("a' OR 1=1 OR 'b")] // quote injection
    [DataRow("foo'")]
    [DataRow("foo\"bar")]
    [DataRow("foo bar")] // space
    [DataRow("foo;bar")] // statement terminator
    [DataRow("foo(bar)")]
    [DataRow("foo,bar")]
    [DataRow("foo/bar")]
    [DataRow("foo\\bar")]
    [DataRow("foo\tbar")]
    [DataRow("foo\nbar")]
    public void ValidateName_RejectsValueWithFilterMetacharacters(string value)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => ServiceBusFilterValidator.ValidateName(value, "param"));
        Assert.AreEqual("param", ex.ParamName);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void ValidateName_RejectsNullOrEmpty(string value)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => ServiceBusFilterValidator.ValidateName(value, "param"));
        Assert.AreEqual("param", ex.ParamName);
    }

    [TestMethod]
    public void ValidateName_RejectsOverlongValue()
    {
        var value = new string('a', ServiceBusFilterValidator.MaxNameLength + 1);
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => ServiceBusFilterValidator.ValidateName(value, "param"));
        Assert.AreEqual("param", ex.ParamName);
        StringAssert.Contains(ex.Message, ServiceBusFilterValidator.MaxNameLength.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [TestMethod]
    public void ValidateName_AcceptsValueAtMaxLength()
    {
        var value = new string('a', ServiceBusFilterValidator.MaxNameLength);
        ServiceBusFilterValidator.ValidateName(value, "param");
    }

    private sealed class ListingAdministrationClient : ServiceBusAdministrationClient
    {
        public int ListingCalls { get; private set; }

        public string? LastTopicName { get; private set; }

        public override AsyncPageable<SubscriptionRuntimeProperties> GetSubscriptionsRuntimePropertiesAsync(
            string topicName, CancellationToken cancellationToken = default) => List<SubscriptionRuntimeProperties>(topicName);

        public override AsyncPageable<SubscriptionProperties> GetSubscriptionsAsync(
            string topicName, CancellationToken cancellationToken = default) => List<SubscriptionProperties>(topicName);

        public override AsyncPageable<RuleProperties> GetRulesAsync(
            string topicName, string subscriptionName, CancellationToken cancellationToken = default) => List<RuleProperties>(topicName);

        private AsyncPageable<T> List<T>(string topicName) where T : notnull
        {
            ListingCalls++;
            LastTopicName = topicName;
            return AsyncPageable<T>.FromPages(new[] { Page<T>.FromValues(Array.Empty<T>(), null, null!) });
        }
    }
}
