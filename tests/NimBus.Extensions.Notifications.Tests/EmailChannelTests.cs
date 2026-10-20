#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Extensions.Notifications.Tests;

[TestClass]
public class EmailChannelTests
{
    [TestMethod]
    public async Task SendAsync_SendGrid_PostsMailSendWithBearerAndAddresses()
    {
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.Accepted);
        var options = new EmailChannelOptions
        {
            Provider = EmailProvider.SendGrid,
            ApiKey = "SG.secret",
            From = "alerts@example.com",
            To = ["oncall@example.com", "backup@example.com"],
        };
        var channel = new EmailChannel(options, new HttpClient(handler));

        await channel.SendAsync(TestNotifications.Build(
            severity: NotificationSeverity.Critical, title: "Down"));

        Assert.AreEqual(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.AreEqual("https://api.sendgrid.com/v3/mail/send", handler.LastRequest.RequestUri!.ToString());
        Assert.AreEqual("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.AreEqual("SG.secret", handler.LastRequest.Headers.Authorization.Parameter);

        var json = JObject.Parse(handler.LastBody!);
        Assert.AreEqual("alerts@example.com", (string?)json["from"]!["email"]);
        Assert.AreEqual("[Critical] Down", (string?)json["subject"]);
        var to = json["personalizations"]![0]!["to"]!.Select(t => (string?)t["email"]).ToList();
        CollectionAssert.AreEquivalent(new[] { "oncall@example.com", "backup@example.com" }, to);
    }

    [TestMethod]
    public async Task SendAsync_SendGrid_NonSuccess_ThrowsDeliveryException()
    {
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.Unauthorized);
        var options = new EmailChannelOptions
        {
            Provider = EmailProvider.SendGrid,
            ApiKey = "SG.secret",
            From = "alerts@example.com",
            To = ["oncall@example.com"],
        };
        var channel = new EmailChannel(options, new HttpClient(handler));

        await Assert.ThrowsExactlyAsync<NotificationDeliveryException>(
            () => channel.SendAsync(TestNotifications.Build()));
    }

    [TestMethod]
    public async Task SendAsync_Smtp_SendsMessageToEachRecipient()
    {
        var options = new EmailChannelOptions
        {
            Provider = EmailProvider.Smtp,
            SmtpHost = "smtp.example.com",
            SmtpPort = 25,
            From = "alerts@example.com",
            To = ["oncall@example.com", "backup@example.com"],
        };
        var channel = new CapturingSmtpEmailChannel(options);

        await channel.SendAsync(TestNotifications.Build(
            severity: NotificationSeverity.Error, title: "Smtp test", message: "body text"));

        Assert.AreEqual("alerts@example.com", channel.CapturedFrom);
        Assert.AreEqual("[Error] Smtp test", channel.CapturedSubject);
        StringAssert.Contains(channel.CapturedBody, "body text");
        CollectionAssert.AreEquivalent(
            new[] { "oncall@example.com", "backup@example.com" }, channel.CapturedTo);
    }

    /// <summary>Subclass that captures the SMTP message instead of connecting to a server.</summary>
    private sealed class CapturingSmtpEmailChannel : EmailChannel
    {
        public CapturingSmtpEmailChannel(EmailChannelOptions options)
            : base(options, new HttpClient(new CapturingHttpMessageHandler()))
        {
        }

        public string? CapturedFrom { get; private set; }
        public string? CapturedSubject { get; private set; }
        public string? CapturedBody { get; private set; }
        public List<string> CapturedTo { get; } = [];

        protected override Task SendViaSmtpAsync(MailMessage message, CancellationToken cancellationToken)
        {
            CapturedFrom = message.From!.Address;
            CapturedSubject = message.Subject;
            CapturedBody = message.Body;
            CapturedTo.AddRange(message.To.Select(a => a.Address));
            return Task.CompletedTask;
        }
    }
}
