#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Management.ServiceBus;

namespace NimBus.ServiceBus.Tests;

/// <summary>
/// Guards the null-to-empty normalisation of <c>UserMetadata</c> in
/// <see cref="ServiceBusManagement.GetSubscription"/>. The SDK leaves <c>UserMetadata</c> null
/// when Service Bus returns no UserMetadata element, but <see cref="ServiceBusModelFactory"/>
/// rejects null, so the WebApp fakes can never hand the service a null read. This is the only
/// place the real null read is exercised.
/// </summary>
[TestClass]
public class ServiceBusManagementGetSubscriptionTests
{
    private const string Topic = "topic";
    private const string Subscription = "subscription";

    [TestMethod]
    public async Task GetSubscription_ReadsANullUserMetadataBackAsEmpty()
    {
        var properties = CreateProperties("placeholder");
        SetUserMetadataBackingFieldToNull(properties);
        Assert.IsNull(properties.UserMetadata, "precondition: the SDK object must really carry a null UserMetadata");

        var management = new ServiceBusManagement(new SingleSubscriptionAdministrationClient(properties));

        var result = await management.GetSubscription(Topic, Subscription);

        Assert.IsNotNull(result);
        Assert.AreEqual(string.Empty, result.UserMetadata);
    }

    [TestMethod]
    [DataRow("owner: team-a")]
    [DataRow("")]
    public async Task GetSubscription_ReturnsOtherUserMetadataUnchanged(string userMetadata)
    {
        var properties = CreateProperties(userMetadata);
        var management = new ServiceBusManagement(new SingleSubscriptionAdministrationClient(properties));

        var result = await management.GetSubscription(Topic, Subscription);

        Assert.IsNotNull(result);
        Assert.AreEqual(userMetadata, result.UserMetadata);
    }

    private static SubscriptionProperties CreateProperties(string userMetadata) =>
        ServiceBusModelFactory.SubscriptionProperties(
            Topic, Subscription,
            lockDuration: TimeSpan.FromSeconds(30),
            requiresSession: false,
            defaultMessageTimeToLive: TimeSpan.MaxValue,
            autoDeleteOnIdle: TimeSpan.MaxValue,
            deadLetteringOnMessageExpiration: false,
            maxDeliveryCount: 10,
            enableBatchedOperations: true,
            status: EntityStatus.Active,
            forwardTo: string.Empty,
            forwardDeadLetteredMessagesTo: string.Empty,
            userMetadata: userMetadata);

    // The public setter rejects null, so reach the backing field directly. Test code only:
    // this models what the SDK's deserializer produces when the response has no UserMetadata.
    private static void SetUserMetadataBackingFieldToNull(SubscriptionProperties properties)
    {
        var fields = typeof(SubscriptionProperties)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(string)
                && field.Name.Contains("UserMetadata", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.HasCount(1, fields, "expected exactly one UserMetadata backing field on SubscriptionProperties");
        fields[0].SetValue(properties, null);
    }

    private sealed class SingleSubscriptionAdministrationClient(SubscriptionProperties properties)
        : ServiceBusAdministrationClient
    {
        public override Task<Response<SubscriptionProperties>> GetSubscriptionAsync(
            string topicName, string subscriptionName, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response.FromValue(properties, null!));
    }
}
