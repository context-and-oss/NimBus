#pragma warning disable CA1707, CA1515, CA2007
using System.Net.Mime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore;
using NimBus.MessageStore.States;
using NimBus.Resolver.Alerts;

namespace NimBus.Resolver.Tests;

/// <summary>What an endpoint alert mail says, how it is built for SMTP, and which settings are valid.</summary>
[TestClass]
public class AlertMailTests
{
    private const string Endpoint = "AnalyticsEndpoint";

    private static AlertMailOptions Options(string? webAppUrl = null, bool includeErrorText = true) => new()
    {
        From = "nimbus@example.com",
        WebAppUrl = webAppUrl,
        IncludeErrorText = includeErrorText,
        Smtp = { Host = "smtp.example.com" },
    };

    private static EndpointSubscription Subscription(int frequency = 86400, List<string>? eventTypes = null) => new()
    {
        Id = "sub-1",
        EndpointId = Endpoint,
        Mail = "ops@example.com",
        Type = "mail",
        AuthorId = "alice",
        Frequency = frequency,
        EventTypes = eventTypes ?? new List<string>(),
    };

    private static EndpointAlertContext Context(
        ResolutionStatus status = ResolutionStatus.Failed,
        string? errorText = "Handler threw",
        string? eventTypeId = "OrderPlaced",
        string eventId = "event-1") => new()
        {
            EndpointId = Endpoint,
            EventId = eventId,
            EventTypeId = eventTypeId,
            SessionId = "session-1",
            Status = status,
            ErrorText = errorText,
            OccurredAtUtc = new DateTimeOffset(2026, 11, 5, 10, 15, 30, TimeSpan.Zero),
        };

    private static AlertMail Compose(AlertMailOptions? options = null, EndpointSubscription? subscription = null, EndpointAlertContext? context = null)
        => new AlertMailComposer(options ?? Options()).Compose(subscription ?? Subscription(), context ?? Context());

    // ── Composer ───────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void The_mail_goes_to_the_subscriber()
        => Assert.AreEqual("ops@example.com", Compose().To);

    [TestMethod]
    [DataRow(ResolutionStatus.Failed, "[NimBus] AnalyticsEndpoint: OrderPlaced failed")]
    [DataRow(ResolutionStatus.DeadLettered, "[NimBus] AnalyticsEndpoint: OrderPlaced was dead-lettered")]
    public void The_subject_names_the_endpoint_event_type_and_outcome(ResolutionStatus status, string subject)
        => Assert.AreEqual(subject, Compose(context: Context(status)).Subject);

    [TestMethod]
    public void A_missing_event_type_is_named_as_unknown()
        => StringAssert.Contains(Compose(context: Context(eventTypeId: null)).Subject, "unknown event type", StringComparison.Ordinal);

    [TestMethod]
    public void Line_breaks_never_reach_the_subject()
    {
        var subject = Compose(context: Context(eventTypeId: "Order\r\nBcc: someone@example.com")).Subject;

        Assert.IsFalse(subject.Contains('\r', StringComparison.Ordinal) || subject.Contains('\n', StringComparison.Ordinal), subject);
    }

    [TestMethod]
    public void Both_bodies_carry_the_event_facts()
    {
        var mail = Compose();

        foreach (var body in new[] { mail.HtmlBody, mail.TextBody })
        {
            StringAssert.Contains(body, Endpoint, StringComparison.Ordinal);
            StringAssert.Contains(body, "OrderPlaced", StringComparison.Ordinal);
            StringAssert.Contains(body, "event-1", StringComparison.Ordinal);
            StringAssert.Contains(body, "session-1", StringComparison.Ordinal);
            StringAssert.Contains(body, "2026-11-05 10:15:30 UTC", StringComparison.Ordinal);
            StringAssert.Contains(body, "Handler threw", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public void The_html_body_encodes_every_value()
    {
        var mail = Compose(context: Context(errorText: "<script>alert('x')</script>", eventTypeId: "Order<b>"));

        Assert.IsFalse(mail.HtmlBody.Contains("<script>", StringComparison.Ordinal), mail.HtmlBody);
        Assert.IsFalse(mail.HtmlBody.Contains("Order<b>", StringComparison.Ordinal), mail.HtmlBody);
        StringAssert.Contains(mail.HtmlBody, "&lt;script&gt;", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Long_error_text_is_truncated()
    {
        var mail = Compose(context: Context(errorText: new string('x', 600)));

        foreach (var body in new[] { mail.HtmlBody, mail.TextBody })
        {
            StringAssert.Contains(body, new string('x', 500) + "…", StringComparison.Ordinal);
            Assert.IsFalse(body.Contains(new string('x', 501), StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void Error_text_can_be_left_out()
    {
        var mail = Compose(Options(includeErrorText: false));

        Assert.IsFalse(mail.HtmlBody.Contains("Handler threw", StringComparison.Ordinal));
        Assert.IsFalse(mail.TextBody.Contains("Handler threw", StringComparison.Ordinal));
    }

    [TestMethod]
    public void The_alert_context_cannot_carry_the_event_payload()
        => Assert.IsFalse(
            typeof(EndpointAlertContext).GetProperties().Any(p =>
                p.Name.Contains("Payload", StringComparison.Ordinal) || p.Name.Contains("Json", StringComparison.Ordinal)),
            "the payload may hold personal data, so it must never reach a mail");

    [TestMethod]
    public void With_a_WebApp_url_the_mail_links_to_the_event_and_the_endpoint()
    {
        var mail = Compose(Options(webAppUrl: "https://nimbus.example.com/"), context: Context(eventId: "event 1"));

        foreach (var body in new[] { mail.HtmlBody, mail.TextBody })
        {
            StringAssert.Contains(body, "https://nimbus.example.com/Message/Index/AnalyticsEndpoint/event%201", StringComparison.Ordinal);
            StringAssert.Contains(body, "https://nimbus.example.com/Endpoints/Details/AnalyticsEndpoint", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public void Without_a_WebApp_url_the_mail_has_no_links()
    {
        var mail = Compose();

        Assert.IsFalse(mail.HtmlBody.Contains("href", StringComparison.Ordinal));
        Assert.IsFalse(mail.TextBody.Contains("http", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(0, "every failure")]
    [DataRow(3600, "at most one alert per hour")]
    [DataRow(7200, "at most one alert per 2 hours")]
    [DataRow(86400, "at most one alert per day")]
    [DataRow(604800, "at most one alert per week")]
    [DataRow(90, "at most one alert per 90 seconds")]
    public void The_footer_explains_the_frequency(int frequency, string text)
    {
        var mail = Compose(subscription: Subscription(frequency));

        StringAssert.Contains(mail.HtmlBody, text, StringComparison.Ordinal);
        StringAssert.Contains(mail.TextBody, text, StringComparison.Ordinal);
    }

    [TestMethod]
    public void The_footer_names_the_event_type_filter()
    {
        var mail = Compose(subscription: Subscription(eventTypes: new List<string> { "OrderPlaced", "OrderShipped" }));

        StringAssert.Contains(mail.TextBody, "OrderPlaced, OrderShipped", StringComparison.Ordinal);
    }

    // ── SMTP message ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void The_smtp_message_has_a_text_body_and_an_html_alternative()
    {
        var options = Options();
        options.FromName = "NimBus Alerts";
        var mail = Compose(options);

        using var message = new SmtpAlertMailSender(options).BuildMessage(mail);

        Assert.AreEqual("nimbus@example.com", message.From!.Address);
        Assert.AreEqual("NimBus Alerts", message.From.DisplayName);
        Assert.AreEqual("ops@example.com", message.To.Single().Address);
        Assert.AreEqual(mail.Subject, message.Subject);
        Assert.AreEqual(mail.TextBody, message.Body);
        Assert.IsFalse(message.IsBodyHtml);
        Assert.AreEqual(MediaTypeNames.Text.Html, message.AlternateViews.Single().ContentType.MediaType);
    }

    [TestMethod]
    public async Task The_smtp_sender_delivers_the_built_message()
    {
        var sender = new CapturingSmtpSender(Options());

        await sender.SendAsync(Compose());

        Assert.AreEqual("ops@example.com", sender.Delivered.Single());
    }

    // ── Options ────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Mail_is_configured_only_with_both_a_host_and_a_sender()
    {
        Assert.IsTrue(Options().IsConfigured);
        Assert.IsFalse(new AlertMailOptions { From = "nimbus@example.com" }.IsConfigured);
        Assert.IsFalse(new AlertMailOptions { Smtp = { Host = "smtp.example.com" } }.IsConfigured);
    }

    [TestMethod]
    public void Valid_options_have_no_errors()
        => Assert.AreEqual(0, Options(webAppUrl: "https://nimbus.example.com").Validate().Count);

    [TestMethod]
    public void Invalid_options_are_reported()
    {
        var options = Options(webAppUrl: "/relative");
        options.From = "not an address";
        options.Smtp.Port = 0;
        options.Timeout = TimeSpan.Zero;

        var errors = options.Validate();

        Assert.AreEqual(4, errors.Count, string.Join(Environment.NewLine, errors));
    }

    private sealed class CapturingSmtpSender(AlertMailOptions options) : SmtpAlertMailSender(options)
    {
        public List<string> Delivered { get; } = new();

        protected override Task DeliverAsync(System.Net.Mail.MailMessage message, CancellationToken cancellationToken)
        {
            Delivered.Add(message.To.Single().Address);
            return Task.CompletedTask;
        }
    }
}
