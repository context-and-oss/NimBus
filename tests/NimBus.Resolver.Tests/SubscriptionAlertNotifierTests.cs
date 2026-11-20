#pragma warning disable CA1707, CA1515, CA2007
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Resolver.Alerts;
using NimBus.Testing.Conformance;

namespace NimBus.Resolver.Tests;

/// <summary>
/// Which Alerts-tab subscriptions get a mail for a failure, and how the frequency window
/// throttles them, against the in-memory store's real compare-and-set.
/// </summary>
[TestClass]
public class SubscriptionAlertNotifierTests
{
    private const string Endpoint = "AnalyticsEndpoint";
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private readonly InMemoryMessageStore _store = new();
    private readonly RecordingMailSender _sender = new();
    private readonly RecordingWebhookSender _webhooks = new();
    private readonly ManualTimeProvider _time = new(Start);

    private SubscriptionAlertNotifier CreateNotifier(
        ISubscriptionStore? subscriptions = null, bool mail = true, bool webhooks = false) => new(
        subscriptions ?? _store,
        _store,
        mail ? _sender : null,
        mail ? new AlertMailComposer(new AlertMailOptions { From = "nimbus@example.com" }) : null,
        webhooks ? _webhooks : null,
        _time,
        NullLogger<SubscriptionAlertNotifier>.Instance);

    private async Task<EndpointSubscription> SubscribeAsync(
        string mail = "ops@example.com",
        string type = "mail",
        List<string>? eventTypes = null,
        string? payload = null,
        int frequency = 3600,
        string endpoint = Endpoint,
        string? url = null)
        => (await _store.SubscribeToEndpointNotification(
            endpoint, mail, type, "alice", url!, eventTypes ?? new List<string>(), payload!, frequency))!;

    private Task<EndpointSubscription> SubscribeWebhookAsync(
        string url = "https://agent.example.com/hook", string type = "webhook", int frequency = 300, string? mail = null)
        => SubscribeAsync(mail: mail!, type: type, frequency: frequency, url: url);

    private static EndpointAlertContext Failure(string eventTypeId = "OrderPlaced") => new()
    {
        EndpointId = Endpoint,
        EventId = "event-1",
        EventTypeId = eventTypeId,
        SessionId = "session-1",
        Status = ResolutionStatus.Failed,
        ErrorText = "Handler threw",
        OccurredAtUtc = Start,
    };

    private async Task<string?> NotifiedAtAsync(EndpointSubscription subscription)
        => (await _store.GetSubscriptionsOnEndpoint(subscription.EndpointId)).Single(s => s.Id == subscription.Id).NotifiedAt;

    [TestMethod]
    public async Task A_matching_subscriber_gets_one_mail()
    {
        await SubscribeAsync();

        await CreateNotifier().NotifyAsync(Failure());

        var mail = _sender.Sent.Single();
        Assert.AreEqual("ops@example.com", mail.To);
        StringAssert.Contains(mail.Subject, Endpoint, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task The_event_type_filter_selects_the_subscribers()
    {
        await SubscribeAsync(mail: "matching@example.com", eventTypes: new List<string> { "OrderPlaced" });
        await SubscribeAsync(mail: "everything@example.com");
        await SubscribeAsync(mail: "other@example.com", eventTypes: new List<string> { "CustomerChanged" });

        await CreateNotifier().NotifyAsync(Failure());

        CollectionAssert.AreEquivalent(
            new[] { "matching@example.com", "everything@example.com" },
            _sender.Sent.Select(m => m.To).ToArray());
    }

    [TestMethod]
    public async Task The_event_type_filter_is_case_sensitive()
    {
        await SubscribeAsync(eventTypes: new List<string> { "orderplaced" });

        await CreateNotifier().NotifyAsync(Failure());

        Assert.AreEqual(0, _sender.Sent.Count);
    }

    [TestMethod]
    [DataRow("mail", true)]
    [DataRow("MAIL", true)]
    [DataRow("mail;teams", true)]
    [DataRow("teams, mail", true)]
    [DataRow("teams", false)]
    [DataRow("webhook", false)]
    [DataRow("", false)]
    public async Task Only_mail_subscriptions_get_a_mail(string type, bool mailed)
    {
        await SubscribeAsync(type: type);

        await CreateNotifier().NotifyAsync(Failure());

        Assert.AreEqual(mailed ? 1 : 0, _sender.Sent.Count);
    }

    [TestMethod]
    public async Task A_payload_filtered_subscription_is_skipped()
    {
        await SubscribeAsync(payload: "priority");

        await CreateNotifier().NotifyAsync(Failure());

        Assert.AreEqual(0, _sender.Sent.Count);
    }

    [TestMethod]
    public async Task A_subscription_without_an_address_is_skipped()
    {
        await SubscribeAsync(mail: " ");

        await CreateNotifier().NotifyAsync(Failure());

        Assert.AreEqual(0, _sender.Sent.Count);
    }

    [TestMethod]
    public async Task Another_endpoints_subscribers_are_not_mailed()
    {
        await SubscribeAsync(endpoint: "BillingEndpoint");

        await CreateNotifier().NotifyAsync(Failure());

        Assert.AreEqual(0, _sender.Sent.Count);
    }

    [TestMethod]
    public async Task A_mail_records_when_the_subscription_was_notified()
    {
        var subscription = await SubscribeAsync();

        await CreateNotifier().NotifyAsync(Failure());

        Assert.AreEqual("2026-11-05 10:00", await NotifiedAtAsync(subscription));
    }

    [TestMethod]
    public async Task The_frequency_window_holds_back_a_second_mail_until_it_has_passed()
    {
        await SubscribeAsync(frequency: 3600);
        var notifier = CreateNotifier();

        await notifier.NotifyAsync(Failure());
        _time.Advance(TimeSpan.FromMinutes(59));
        await notifier.NotifyAsync(Failure());
        Assert.AreEqual(1, _sender.Sent.Count, "inside the hour");

        _time.Advance(TimeSpan.FromMinutes(1));
        await notifier.NotifyAsync(Failure());
        Assert.AreEqual(2, _sender.Sent.Count, "the hour has passed");
    }

    [TestMethod]
    public async Task A_zero_frequency_mails_every_failure()
    {
        var subscription = await SubscribeAsync(frequency: 0);
        var notifier = CreateNotifier();

        await notifier.NotifyAsync(Failure());
        await notifier.NotifyAsync(Failure());
        await notifier.NotifyAsync(Failure());

        Assert.AreEqual(3, _sender.Sent.Count);
        Assert.AreEqual("2026-11-05 10:00", await NotifiedAtAsync(subscription));
    }

    [TestMethod]
    public async Task An_unreadable_NotifiedAt_counts_as_never_notified()
    {
        var subscription = await SubscribeAsync();
        Assert.IsTrue(await _store.TrySetNotifiedAtAsync(subscription.Id, null, "not a date"));

        await CreateNotifier().NotifyAsync(Failure());

        Assert.AreEqual(1, _sender.Sent.Count);
    }

    [TestMethod]
    public async Task Concurrent_failures_inside_one_window_send_one_mail()
    {
        await SubscribeAsync(frequency: 3600);
        var notifier = CreateNotifier();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => notifier.NotifyAsync(Failure()))));

        Assert.AreEqual(1, _sender.Sent.Count);
    }

    [TestMethod]
    public async Task A_failed_send_releases_the_window_so_the_next_failure_mails()
    {
        var subscription = await SubscribeAsync(frequency: 3600);
        _sender.FailNext = new InvalidOperationException("SMTP down");
        var notifier = CreateNotifier();

        await notifier.NotifyAsync(Failure());
        Assert.IsNull(await NotifiedAtAsync(subscription));

        await notifier.NotifyAsync(Failure());
        Assert.AreEqual(1, _sender.Sent.Count);
    }

    [TestMethod]
    public async Task One_failing_recipient_does_not_stop_the_others()
    {
        await SubscribeAsync(mail: "bad@example.com");
        await SubscribeAsync(mail: "good@example.com");
        _sender.FailFor = "bad@example.com";

        await CreateNotifier().NotifyAsync(Failure());

        Assert.AreEqual("good@example.com", _sender.Sent.Single().To);
    }

    [TestMethod]
    public async Task A_store_read_failure_is_swallowed()
    {
        await CreateNotifier(new ThrowingSubscriptionStore()).NotifyAsync(Failure());

        Assert.AreEqual(0, _sender.Sent.Count);
    }

    // ── Webhooks ───────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task A_webhook_subscription_gets_one_request()
    {
        var subscription = await SubscribeWebhookAsync();

        await CreateNotifier(webhooks: true).NotifyAsync(Failure());

        var webhook = _webhooks.Sent.Single();
        Assert.AreEqual(subscription.Id, webhook.SubscriptionId);
        Assert.AreEqual("https://agent.example.com/hook", webhook.Url.AbsoluteUri);
        Assert.AreEqual("event-1", webhook.Context.EventId);
        Assert.AreEqual(0, _sender.Sent.Count);
        Assert.AreEqual("2026-11-05 10:00", await NotifiedAtAsync(subscription));
    }

    [TestMethod]
    public async Task A_webhook_subscription_is_skipped_while_webhooks_are_off()
    {
        var subscription = await SubscribeWebhookAsync();

        await CreateNotifier(webhooks: false).NotifyAsync(Failure());

        Assert.AreEqual(0, _webhooks.Sent.Count);
        Assert.IsNull(await NotifiedAtAsync(subscription), "a skipped subscription must not use its window");
    }

    [TestMethod]
    public async Task A_mail_subscription_is_skipped_while_mail_is_off()
    {
        var subscription = await SubscribeAsync();
        await SubscribeWebhookAsync();

        await CreateNotifier(mail: false, webhooks: true).NotifyAsync(Failure());

        Assert.AreEqual(0, _sender.Sent.Count);
        Assert.AreEqual(1, _webhooks.Sent.Count);
        Assert.IsNull(await NotifiedAtAsync(subscription));
    }

    [TestMethod]
    public async Task A_url_the_sender_refuses_is_skipped()
    {
        var subscription = await SubscribeWebhookAsync(url: "http://agent.example.com/hook");
        _webhooks.Refuse = url => url!.StartsWith("http://", StringComparison.Ordinal);

        await CreateNotifier(webhooks: true).NotifyAsync(Failure());

        Assert.AreEqual(0, _webhooks.Sent.Count);
        Assert.IsNull(await NotifiedAtAsync(subscription));
    }

    [TestMethod]
    public async Task A_subscription_of_both_types_is_notified_both_ways_in_one_window()
    {
        await SubscribeWebhookAsync(type: "mail;webhook", mail: "ops@example.com");
        var notifier = CreateNotifier(webhooks: true);

        await notifier.NotifyAsync(Failure());
        await notifier.NotifyAsync(Failure());

        Assert.AreEqual(1, _sender.Sent.Count);
        Assert.AreEqual(1, _webhooks.Sent.Count);
    }

    [TestMethod]
    public async Task The_frequency_window_holds_back_a_second_request_until_it_has_passed()
    {
        await SubscribeWebhookAsync(frequency: 300);
        var notifier = CreateNotifier(webhooks: true);

        await notifier.NotifyAsync(Failure());
        _time.Advance(TimeSpan.FromMinutes(4));
        await notifier.NotifyAsync(Failure());
        Assert.AreEqual(1, _webhooks.Sent.Count, "inside the five minutes");

        _time.Advance(TimeSpan.FromMinutes(1));
        await notifier.NotifyAsync(Failure());
        Assert.AreEqual(2, _webhooks.Sent.Count, "the five minutes have passed");
    }

    [TestMethod]
    public async Task Concurrent_failures_inside_one_window_send_one_request()
    {
        await SubscribeWebhookAsync();
        var notifier = CreateNotifier(webhooks: true);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => notifier.NotifyAsync(Failure()))));

        Assert.AreEqual(1, _webhooks.Sent.Count);
    }

    [TestMethod]
    public async Task A_failed_request_releases_the_window_so_the_next_failure_tries_again()
    {
        var subscription = await SubscribeWebhookAsync();
        _webhooks.FailNext = new TimeoutException("receiver down");
        var notifier = CreateNotifier(webhooks: true);

        await notifier.NotifyAsync(Failure());
        Assert.IsNull(await NotifiedAtAsync(subscription));

        await notifier.NotifyAsync(Failure());
        Assert.AreEqual(1, _webhooks.Sent.Count);
    }

    [TestMethod]
    public async Task The_window_is_kept_when_one_of_two_deliveries_succeeded()
    {
        var subscription = await SubscribeWebhookAsync(type: "mail;webhook", mail: "ops@example.com");
        _webhooks.FailNext = new TimeoutException("receiver down");
        var notifier = CreateNotifier(webhooks: true);

        await notifier.NotifyAsync(Failure());
        await notifier.NotifyAsync(Failure());

        Assert.AreEqual(1, _sender.Sent.Count, "the mail went out, so the window is used");
        Assert.AreEqual(0, _webhooks.Sent.Count);
        Assert.AreEqual("2026-11-05 10:00", await NotifiedAtAsync(subscription));
    }

    [TestMethod]
    public async Task A_zero_frequency_sends_a_request_for_every_failure()
    {
        await SubscribeWebhookAsync(frequency: 0);
        var notifier = CreateNotifier(webhooks: true);

        await notifier.NotifyAsync(Failure());
        await notifier.NotifyAsync(Failure());

        Assert.AreEqual(2, _webhooks.Sent.Count);
    }

    [TestMethod]
    public void A_notifier_needs_at_least_one_way_to_deliver()
        => Assert.ThrowsExactly<ArgumentException>(() => CreateNotifier(mail: false, webhooks: false));

    internal sealed class RecordingWebhookSender : IAlertWebhookSender
    {
        private readonly object _lock = new();

        public List<AlertWebhook> Sent { get; } = new();

        public Exception? FailNext { get; set; }

        public Func<string?, bool>? Refuse { get; set; }

        public bool CanSendTo(string? url) => !string.IsNullOrWhiteSpace(url) && Refuse?.Invoke(url) != true;

        public Task SendAsync(AlertWebhook webhook, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (FailNext is { } failure)
                {
                    FailNext = null;
                    return Task.FromException(failure);
                }

                Sent.Add(webhook);
                return Task.CompletedTask;
            }
        }
    }

    internal sealed class RecordingMailSender : IAlertMailSender
    {
        private readonly object _lock = new();

        public List<AlertMail> Sent { get; } = new();

        public Exception? FailNext { get; set; }

        public string? FailFor { get; set; }

        public Task SendAsync(AlertMail mail, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (FailNext is { } failure)
                {
                    FailNext = null;
                    return Task.FromException(failure);
                }

                if (string.Equals(mail.To, FailFor, StringComparison.Ordinal))
                {
                    return Task.FromException(new FormatException("bad address"));
                }

                Sent.Add(mail);
                return Task.CompletedTask;
            }
        }
    }

    internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class ThrowingSubscriptionStore : ISubscriptionStore
    {
        private static Exception Down() => new InvalidOperationException("store down");

        public Task<EndpointSubscription?> SubscribeToEndpointNotification(string endpointId, string mail, string type,
            string author, string url, List<string> eventTypes, string payload, int frequency) => throw Down();

        public Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpoint(string endpointId)
            => Task.FromException<IEnumerable<EndpointSubscription>>(Down());

        public Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpointWithEventtype(string endpoint,
            string eventtypes, string payload, string errorText) => throw Down();

        public Task<bool> UpdateSubscription(EndpointSubscription subscription) => throw Down();

        public Task<bool> UnsubscribeById(string endpointId, string id) => throw Down();

        public Task<bool> UnsubscribeByMail(string endpointId, string mail) => throw Down();

        public Task<bool> DeleteSubscription(string subscriptionId) => throw Down();
    }
}
