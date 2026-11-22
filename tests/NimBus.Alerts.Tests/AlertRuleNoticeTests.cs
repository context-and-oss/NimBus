#pragma warning disable CA1707, CA1515, CA2007
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.States;

namespace NimBus.Alerts.Tests;

/// <summary>Spec 053: alert rule notices by webhook and mail, and the words and links they carry.</summary>
[TestClass]
public class AlertRuleNoticeTests
{
    private const string SigningKey = "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 30, 0, TimeSpan.Zero);

    private static AlertRuleCondition Condition() => new()
    {
        Threshold = 5,
        EventTypes = ["OrderPlaced"],
        Statuses = ["Failed", "DeadLettered"],
        ErrorTextContains = "timeout",
    };

    private static AlertRuleNotice Notice(AlertRuleStatus status = AlertRuleStatus.Firing, string? reason = null) => new(
        NoticeId: "rule1:7:t2",
        RuleId: "rule1",
        RuleName: "ERP timeouts",
        EndpointId: "ErpEndpoint",
        Status: status,
        Reason: reason,
        Value: 5,
        Condition: Condition(),
        SinceUtc: new DateTime(2026, 11, 23, 9, 14, 0, DateTimeKind.Utc),
        WebUiUrl: "https://nimbus.example.com/Failed?endpointId=ErpEndpoint");

    private static AlertRuleTarget WebhookTarget(string url = "https://hooks.example.com/nimbus") =>
        new() { TargetId = "t2", Type = AlertRuleTargetType.Webhook, Url = url };

    private static AlertWebhookSender Sender(AlertWebhookSenderTests.CapturingHandler handler, Action<AlertWebhookOptions>? configure = null, Azure.Core.TokenCredential? credential = null)
    {
        var options = new AlertWebhookOptions { SigningKey = SigningKey };
        configure?.Invoke(options);
        return new AlertWebhookSender(options, new HttpClient(handler), credential, new ManualTimeProvider(Now));
    }

    [TestMethod]
    public void A_rule_target_secret_is_the_documented_derivation_and_differs_from_a_subscription_secret()
    {
        var expected = "nbwh_" + Convert.ToBase64String(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(SigningKey), Encoding.UTF8.GetBytes("nimbus-alert-rule-webhook/v1/rule1/t2")))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.AreEqual(expected, EndpointAlertWebhook.DeriveRuleTargetSigningSecret(SigningKey, "rule1", "t2"));
        Assert.AreNotEqual(
            EndpointAlertWebhook.DeriveSigningSecret(SigningKey, "rule1/t2"),
            EndpointAlertWebhook.DeriveRuleTargetSigningSecret(SigningKey, "rule1", "t2"));
    }

    [TestMethod]
    public async Task A_rule_notice_is_a_signed_version_2_body_with_the_notice_id()
    {
        var handler = new AlertWebhookSenderTests.CapturingHandler();
        using var sender = Sender(handler);

        await sender.SendRuleNoticeAsync(Notice(), WebhookTarget());

        Assert.AreEqual("rule1:7:t2", handler.Header(EndpointAlertWebhook.NoticeHeader));
        Assert.IsNull(handler.Header(EndpointAlertWebhook.SubscriptionHeader), "A rule notice is not a subscription alert.");
        var timestamp = long.Parse(handler.Header(EndpointAlertWebhook.TimestampHeader)!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.AreEqual(Now.ToUnixTimeSeconds(), timestamp);
        var secret = EndpointAlertWebhook.DeriveRuleTargetSigningSecret(SigningKey, "rule1", "t2");
        Assert.AreEqual(EndpointAlertWebhook.Sign(secret, timestamp, handler.Body!), handler.Header(EndpointAlertWebhook.SignatureHeader));

        using var json = JsonDocument.Parse(handler.Body!);
        var body = json.RootElement;
        Assert.AreEqual(2, body.GetProperty("version").GetInt32());
        Assert.AreEqual("alert_rule", body.GetProperty("kind").GetString());
        Assert.AreEqual("rule1:7:t2", body.GetProperty("noticeId").GetString());
        Assert.AreEqual("ERP timeouts", body.GetProperty("ruleName").GetString());
        Assert.AreEqual("ErpEndpoint", body.GetProperty("endpointId").GetString());
        Assert.AreEqual("Firing", body.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, body.GetProperty("reason").ValueKind);
        Assert.AreEqual(5, body.GetProperty("value").GetInt32());
        Assert.AreEqual(5, body.GetProperty("threshold").GetInt32());
        Assert.AreEqual("2026-11-23T09:14:00.000Z", body.GetProperty("sinceUtc").GetString());
        Assert.AreEqual("https://nimbus.example.com/Failed?endpointId=ErpEndpoint", body.GetProperty("webUiUrl").GetString());
        Assert.IsFalse(body.TryGetProperty("condition", out _) || body.TryGetProperty("errorText", out _), "The body carries no condition or error text.");
    }

    [TestMethod]
    public async Task A_resolved_notice_names_its_reason()
    {
        var handler = new AlertWebhookSenderTests.CapturingHandler();
        using var sender = Sender(handler);

        await sender.SendRuleNoticeAsync(Notice(AlertRuleStatus.Ok, AlertRuleResolveReasons.RuleDisabled), WebhookTarget());

        using var json = JsonDocument.Parse(handler.Body!);
        Assert.AreEqual("Resolved", json.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("rule_disabled", json.RootElement.GetProperty("reason").GetString());
    }

    [TestMethod]
    public async Task A_rule_notice_carries_a_bearer_token_only_under_a_listed_prefix()
    {
        var handler = new AlertWebhookSenderTests.CapturingHandler();
        var credential = new AlertWebhookSenderTests.RecordingCredential();
        using var sender = Sender(handler, o => o.BearerTokens.Add(new AlertWebhookBearerToken
        {
            UrlPrefix = "https://agent.example.com/api/",
            Audience = "https://azuresre.dev",
        }), credential);

        await sender.SendRuleNoticeAsync(Notice(), WebhookTarget("https://agent.example.com/api/threads"));
        Assert.AreEqual("Bearer token-for-https://azuresre.dev/.default", handler.Authorization);

        await sender.SendRuleNoticeAsync(Notice(), WebhookTarget("https://hooks.example.com/nimbus"));
        Assert.IsNull(handler.Authorization);
    }

    [TestMethod]
    public async Task A_receiver_that_refuses_the_notice_fails_the_delivery()
    {
        using var sender = Sender(new AlertWebhookSenderTests.CapturingHandler(HttpStatusCode.ServiceUnavailable));

        var error = await Assert.ThrowsExactlyAsync<AlertWebhookDeliveryException>(
            () => sender.SendRuleNoticeAsync(Notice(), WebhookTarget()));
        StringAssert.Contains(error.Message, "hooks.example.com");
        Assert.IsFalse(error.Message.Contains("/nimbus", StringComparison.Ordinal), "The path stays out of the error.");
    }

    [TestMethod]
    public async Task A_mail_target_cannot_be_sent_as_a_webhook()
    {
        using var sender = Sender(new AlertWebhookSenderTests.CapturingHandler());

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => sender.SendRuleNoticeAsync(
            Notice(), new AlertRuleTarget { TargetId = "t1", Type = AlertRuleTargetType.Mail, Address = "team@example.com" }));
    }

    [TestMethod]
    public void The_firing_mail_names_the_rule_the_condition_and_the_count()
    {
        var mail = new AlertMailComposer(new AlertMailOptions()).ComposeRuleNotice(Notice(), "team@example.com");

        Assert.AreEqual("team@example.com", mail.To);
        Assert.AreEqual("[NimBus] Firing: ERP timeouts on ErpEndpoint", mail.Subject);
        StringAssert.Contains(mail.TextBody, "ERP timeouts is firing on ErpEndpoint");
        StringAssert.Contains(mail.TextBody, "5 or more failed events (Failed, DeadLettered) of type OrderPlaced with an error containing \"timeout\"");
        StringAssert.Contains(mail.TextBody, "at least 5");
        StringAssert.Contains(mail.TextBody, "2026-11-23 09:14:00 UTC");
        StringAssert.Contains(mail.TextBody, "https://nimbus.example.com/Failed?endpointId=ErpEndpoint");
        Assert.IsFalse(mail.TextBody.Contains("Reason", StringComparison.Ordinal), "A firing notice has no reason.");
    }

    [TestMethod]
    public void The_resolved_mail_says_why()
    {
        var mail = new AlertMailComposer(new AlertMailOptions())
            .ComposeRuleNotice(Notice(AlertRuleStatus.Ok, AlertRuleResolveReasons.Cleared), "team@example.com");

        Assert.AreEqual("[NimBus] Resolved: ERP timeouts on ErpEndpoint", mail.Subject);
        StringAssert.Contains(mail.TextBody, "ERP timeouts resolved on ErpEndpoint");
        StringAssert.Contains(mail.TextBody, "The failed events were cleared.");
    }

    [TestMethod]
    public void The_rule_mail_encodes_every_value_in_its_html()
    {
        var notice = Notice() with { RuleName = "<script>alert(1)</script>" };

        var mail = new AlertMailComposer(new AlertMailOptions()).ComposeRuleNotice(notice, "team@example.com");

        Assert.IsFalse(mail.HtmlBody.Contains("<script>", StringComparison.Ordinal));
        StringAssert.Contains(mail.HtmlBody, "&lt;script&gt;");
    }

    [TestMethod]
    public void A_condition_reads_as_a_sentence()
    {
        Assert.AreEqual("1 or more failed event", AlertRuleText.DescribeCondition(new AlertRuleCondition { Threshold = 1 }));
        Assert.AreEqual(
            "3 or more failed events of type A or B",
            AlertRuleText.DescribeCondition(new AlertRuleCondition { Threshold = 3, EventTypes = ["A", "B"] }));
    }

    [TestMethod]
    public void The_failed_messages_link_carries_the_rule_filters_encoded()
    {
        var url = AlertRuleText.FailedMessagesUrl("https://nimbus.example.com/", "Erp Endpoint", Condition());

        Assert.AreEqual(
            "https://nimbus.example.com/Failed?endpointId=Erp%20Endpoint&period=30d&status=Failed&status=DeadLettered&eventTypeId=OrderPlaced&errorText=timeout",
            url);
        Assert.IsNull(AlertRuleText.FailedMessagesUrl(null, "ErpEndpoint", Condition()));
    }

    [TestMethod]
    public void A_notice_id_joins_the_rule_the_transition_and_the_target()
        => Assert.AreEqual("rule1:7:t2", AlertRuleText.NoticeId("rule1", 7, "t2"));
}
