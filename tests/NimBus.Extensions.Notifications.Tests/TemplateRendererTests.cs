#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.Extensions.Notifications.Tests;

[TestClass]
public class TemplateRendererTests
{
    [TestMethod]
    public void Render_SubstitutesAllKnownPlaceholders()
    {
        var notification = TestNotifications.Build(
            severity: NotificationSeverity.Error,
            title: "Boom",
            message: "It failed",
            eventId: "e-1",
            eventTypeId: "OrderPlaced",
            messageId: "m-1",
            correlationId: "c-1",
            errorDetails: "stack trace");

        const string template =
            "{Severity}|{Title}|{Message}|{EventId}|{EventTypeId}|{MessageId}|{CorrelationId}|{ErrorDetails}";

        var result = TemplateRenderer.Render(template, notification);

        Assert.AreEqual("Error|Boom|It failed|e-1|OrderPlaced|m-1|c-1|stack trace", result);
    }

    [TestMethod]
    public void Render_LeavesUnknownPlaceholdersLiteral()
    {
        var notification = TestNotifications.Build(title: "Hi");

        var result = TemplateRenderer.Render("{Title} {Unknown}", notification);

        Assert.AreEqual("Hi {Unknown}", result);
    }

    [TestMethod]
    public void Render_MissingValuesResolveToEmptyString()
    {
        var notification = TestNotifications.Build(errorDetails: null);

        var result = TemplateRenderer.Render("[{ErrorDetails}]", notification);

        Assert.AreEqual("[]", result);
    }

    [TestMethod]
    public void Render_JsonEncode_EscapesValuesSoTemplateStaysValidJson()
    {
        var notification = TestNotifications.Build(
            title: "Order \"42\" failed",
            errorDetails: "line1\nline2\tC:\\temp");

        const string template = "{\"summary\":\"{Title}\",\"error\":\"{ErrorDetails}\"}";

        var result = TemplateRenderer.Render(template, notification, jsonEncodeValues: true);

        // The rendered payload must round-trip as valid JSON with the original values intact.
        var parsed = Newtonsoft.Json.Linq.JObject.Parse(result);
        Assert.AreEqual("Order \"42\" failed", (string?)parsed["summary"]);
        Assert.AreEqual("line1\nline2\tC:\\temp", (string?)parsed["error"]);
    }

    [TestMethod]
    public void Render_WithoutJsonEncode_LeavesValuesRaw()
    {
        var notification = TestNotifications.Build(title: "a\"b");

        var result = TemplateRenderer.Render("{Title}", notification);

        Assert.AreEqual("a\"b", result);
    }
}
