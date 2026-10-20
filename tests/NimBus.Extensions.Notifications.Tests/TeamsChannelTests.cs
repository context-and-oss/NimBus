#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace NimBus.Extensions.Notifications.Tests;

[TestClass]
public class TeamsChannelTests
{
    [TestMethod]
    public async Task SendAsync_PostsAdaptiveCardWithFactsAndSeverityColour()
    {
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK);
        var options = new TeamsChannelOptions { ConnectorUrl = "https://outlook.office.com/webhook/abc" };
        var channel = new TeamsChannel(options, new HttpClient(handler));

        await channel.SendAsync(TestNotifications.Build(
            severity: NotificationSeverity.Critical,
            title: "Session blocked",
            message: "Order session is blocked",
            eventId: "e-9",
            correlationId: "c-9"));

        var root = JObject.Parse(handler.LastBody!);
        var card = root["attachments"]![0]!["content"]!;
        Assert.AreEqual("AdaptiveCard", (string?)card["type"]);
        Assert.AreEqual("1.4", (string?)card["version"]);

        var body = (JArray)card["body"]!;
        var titleBlock = body[0];
        Assert.AreEqual("Session blocked", (string?)titleBlock["text"]);
        Assert.AreEqual("attention", (string?)titleBlock["color"]);

        var facts = (JArray)body[2]["facts"]!;
        var factTitles = facts.Select(f => (string?)f["title"]).ToList();
        CollectionAssert.Contains(factTitles, "Severity");
        CollectionAssert.Contains(factTitles, "Event Id");
        CollectionAssert.Contains(factTitles, "Correlation Id");
    }

    [TestMethod]
    public void BuildAdaptiveCard_MapsSeverityToDistinctColours()
    {
        var critical = JObject.Parse(TeamsChannel.BuildAdaptiveCard(
            TestNotifications.Build(severity: NotificationSeverity.Critical)));
        var warning = JObject.Parse(TeamsChannel.BuildAdaptiveCard(
            TestNotifications.Build(severity: NotificationSeverity.Warning)));

        var criticalColor = (string?)critical["attachments"]![0]!["content"]!["body"]![0]!["color"];
        var warningColor = (string?)warning["attachments"]![0]!["content"]!["body"]![0]!["color"];

        Assert.AreEqual("attention", criticalColor);
        Assert.AreEqual("accent", warningColor);
        Assert.AreNotEqual(criticalColor, warningColor);
    }

    [TestMethod]
    public void BuildAdaptiveCard_TruncatesOversizedErrorDetails()
    {
        var huge = new string('x', 60 * 1024);
        var json = TeamsChannel.BuildAdaptiveCard(
            TestNotifications.Build(severity: NotificationSeverity.Error, errorDetails: huge));

        var facts = (JArray)JObject.Parse(json)["attachments"]![0]!["content"]!["body"]![2]!["facts"]!;
        var error = facts.First(f => (string?)f["title"] == "Error");
        var value = (string)error["value"]!;
        Assert.IsTrue(value.Length < huge.Length, "Error detail should be truncated.");
        StringAssert.Contains(value, "truncated");
    }
}
