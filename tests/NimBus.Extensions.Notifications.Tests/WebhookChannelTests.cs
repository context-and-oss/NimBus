#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace NimBus.Extensions.Notifications.Tests;

[TestClass]
public class WebhookChannelTests
{
    [TestMethod]
    public async Task SendAsync_WithTemplate_PostsSubstitutedBody()
    {
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK);
        var options = new WebhookChannelOptions
        {
            Url = "https://example.com/hook",
            Template = "{\"summary\":\"{Title}\",\"severity\":\"{Severity}\",\"event\":\"{EventId}\"}",
        };
        var channel = new WebhookChannel(options, new HttpClient(handler));

        await channel.SendAsync(TestNotifications.Build(
            severity: NotificationSeverity.Critical, title: "Disk full", eventId: "e-42"));

        Assert.AreEqual(1, handler.CallCount);
        Assert.AreEqual(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.AreEqual("https://example.com/hook", handler.LastRequest.RequestUri!.ToString());
        var json = JObject.Parse(handler.LastBody!);
        Assert.AreEqual("Disk full", (string?)json["summary"]);
        Assert.AreEqual("Critical", (string?)json["severity"]);
        Assert.AreEqual("e-42", (string?)json["event"]);
    }

    [TestMethod]
    public async Task SendAsync_WithoutTemplate_PostsDefaultJsonSerialization()
    {
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK);
        var options = new WebhookChannelOptions { Url = "https://example.com/hook" };
        var channel = new WebhookChannel(options, new HttpClient(handler));

        await channel.SendAsync(TestNotifications.Build(title: "Default", eventId: "e-7"));

        Assert.AreEqual("application/json", handler.LastRequest!.Content!.Headers.ContentType!.MediaType);
        var json = JObject.Parse(handler.LastBody!);
        Assert.AreEqual("Default", (string?)json["Title"]);
        Assert.AreEqual("e-7", (string?)json["EventId"]);
    }

    [TestMethod]
    public async Task SendAsync_NonSuccessStatus_ThrowsDeliveryException()
    {
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.InternalServerError);
        var options = new WebhookChannelOptions { Url = "https://example.com/hook" };
        var channel = new WebhookChannel(options, new HttpClient(handler));

        await Assert.ThrowsExactlyAsync<NotificationDeliveryException>(
            () => channel.SendAsync(TestNotifications.Build()));
    }
}
