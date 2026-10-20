#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.CosmosDb.Tests;

[TestClass]
public sealed class CosmosDbSubscriptionStoreUnitTests
{
    [TestMethod]
    public async Task SubscribeToEndpointNotification_UnderTurkishCulture_StoresInvariantLowercaseType()
    {
        var container = new RecordingCosmosContainerAdapter { UpsertReturnsResponse = true };
        var store = CreateStore(container);

        var subscription = await WithCulture(new CultureInfo("tr-TR"), () =>
            store.SubscribeToEndpointNotification(
                "endpoint", "ops@example.com", "MAIL", "author", "https://example.com", new List<string>(), "payload", 0));

        Assert.AreEqual("mail", subscription!.Type);
    }

    [TestMethod]
    public async Task SubscribeToEndpointNotification_InvalidType_ThrowsArgumentExceptionForType()
    {
        var store = CreateStore(new RecordingCosmosContainerAdapter());

        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            store.SubscribeToEndpointNotification(
                "endpoint", "ops@example.com", "sms", "author", "https://example.com", new List<string>(), "payload", 0));

        Assert.AreEqual("type", exception.ParamName);
    }

    [TestMethod]
    public async Task SubscribeToEndpointNotification_InvalidMail_ThrowsArgumentExceptionForMail()
    {
        var store = CreateStore(new RecordingCosmosContainerAdapter());

        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            store.SubscribeToEndpointNotification(
                "endpoint", "not-an-address", "mail", "author", "https://example.com", new List<string>(), "payload", 0));

        Assert.AreEqual("mail", exception.ParamName);
    }

    [TestMethod]
    public async Task UpdateSubscription_CultureWithDotTimeSeparator_WritesInvariantNotifiedAt()
    {
        var container = new RecordingCosmosContainerAdapter();
        var store = CreateStore(container);
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.DateTimeFormat.TimeSeparator = ".";
        var subscription = new EndpointSubscription { Id = "sub-1", EndpointId = "endpoint" };

        var updated = await WithCulture(culture, () => store.UpdateSubscription(subscription));

        Assert.IsTrue(updated);
        StringAssert.Matches(subscription.NotifiedAt, new System.Text.RegularExpressions.Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$"));
    }

    private static CosmosDbSubscriptionStore CreateStore(RecordingCosmosContainerAdapter container) =>
        new(
            () => Task.FromResult<ICosmosContainerAdapter>(container),
            _ => Task.FromResult(string.Empty),
            logger: null!);

    private static async Task<T> WithCulture<T>(CultureInfo culture, Func<Task<T>> action)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            return await action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
