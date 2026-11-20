#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.InMemory.Tests;

[TestClass]
public sealed class EndpointAlertWebhookTests
{
    // Computed outside .NET (Node's crypto), so the derivation and the signature are pinned to
    // what a receiver in another language computes.
    private const string SigningKey = "0123456789abcdef0123456789abcdef";
    private const string SubscriptionId = "11111111-2222-3333-4444-555555555555";
    private const string ExpectedSecret = "nbwh_p33IIWD1NRTnQzSh-v_4ZyYlgCun4tBCDAlaUwT6rW4";
    private const string ExpectedSignature = "v1=8412512ba26195f25e006213dfd021afb3ef4dba30a1860b413e4dffa5355a90";

    [TestMethod]
    [DataRow("mail", "mail")]
    [DataRow("MAIL", "mail")]
    [DataRow("webhook", "webhook")]
    [DataRow("teams", "teams")]
    [DataRow("mail;teams", "mail;teams")]
    [DataRow(" Mail , Webhook ", "mail;webhook")]
    [DataRow("mail;mail", "mail")]
    public void TryParse_accepts_known_types_and_normalizes_them(string type, string expected)
    {
        Assert.IsTrue(EndpointSubscriptionTypes.TryParse(type, out var types));
        Assert.AreEqual(expected, string.Join(';', types));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ; ")]
    [DataRow("sms")]
    [DataRow("mail;sms")]
    public void TryParse_rejects_an_empty_or_unknown_type(string? type)
    {
        Assert.IsFalse(EndpointSubscriptionTypes.TryParse(type, out var types));
        Assert.AreEqual(0, types.Count);
    }

    [TestMethod]
    [DataRow("webhook", "webhook", true)]
    [DataRow("mail;WEBHOOK", "webhook", true)]
    [DataRow("mail, teams", "teams", true)]
    [DataRow("mail", "webhook", false)]
    [DataRow("sms;webhook", "webhook", true)]
    [DataRow(null, "mail", false)]
    public void Includes_finds_a_type_among_the_stored_parts(string? type, string wanted, bool expected)
        => Assert.AreEqual(expected, EndpointSubscriptionTypes.Includes(type, wanted));

    [TestMethod]
    [DataRow("https://agent.example.com/hooks/nimbus")]
    [DataRow("https://agent.example.com:8443/hooks?x=1")]
    public void IsValidUrl_accepts_an_absolute_https_url(string url)
        => Assert.IsTrue(EndpointAlertWebhook.IsValidUrl(url));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("agent.example.com/hooks")]
    [DataRow("/hooks/nimbus")]
    [DataRow("http://agent.example.com/hooks")]
    [DataRow("ftp://agent.example.com/hooks")]
    [DataRow("file:///c:/temp/hook")]
    [DataRow("https://user:secret@agent.example.com/hooks")]
    [DataRow("https://user@agent.example.com/hooks")]
    public void IsValidUrl_rejects_everything_else(string? url)
        => Assert.IsFalse(EndpointAlertWebhook.IsValidUrl(url));

    [TestMethod]
    public void IsValidUrl_rejects_a_url_longer_than_the_stored_column()
    {
        var url = "https://agent.example.com/" + new string('a', EndpointAlertWebhook.MaxUrlLength);

        Assert.IsFalse(EndpointAlertWebhook.IsValidUrl(url));
    }

    [TestMethod]
    public void IsValidUrl_accepts_http_only_when_asked_to()
    {
        Assert.IsTrue(EndpointAlertWebhook.IsValidUrl("http://localhost:5090/hooks", allowHttp: true));
        Assert.IsFalse(EndpointAlertWebhook.IsValidUrl("ftp://localhost/hooks", allowHttp: true));
    }

    [TestMethod]
    public void DeriveSigningSecret_matches_the_known_answer()
        => Assert.AreEqual(ExpectedSecret, EndpointAlertWebhook.DeriveSigningSecret(SigningKey, SubscriptionId));

    [TestMethod]
    public void DeriveSigningSecret_differs_per_subscription_and_per_key()
    {
        var secret = EndpointAlertWebhook.DeriveSigningSecret(SigningKey, SubscriptionId);

        Assert.AreNotEqual(secret, EndpointAlertWebhook.DeriveSigningSecret(SigningKey, "another-subscription"));
        Assert.AreNotEqual(secret, EndpointAlertWebhook.DeriveSigningSecret(SigningKey + "x", SubscriptionId));
    }

    [TestMethod]
    [DataRow(null, "sub")]
    [DataRow("", "sub")]
    [DataRow("key", null)]
    [DataRow("key", "")]
    public void DeriveSigningSecret_needs_a_key_and_a_subscription(string? signingKey, string? subscriptionId)
        => Assert.Throws<ArgumentException>(() => EndpointAlertWebhook.DeriveSigningSecret(signingKey!, subscriptionId!));

    [TestMethod]
    public void Sign_matches_the_known_answer()
        => Assert.AreEqual(ExpectedSignature, EndpointAlertWebhook.Sign(ExpectedSecret, 1790000000, "{\"version\":1}"));

    [TestMethod]
    public void Sign_covers_the_timestamp_and_the_body()
    {
        var signature = EndpointAlertWebhook.Sign(ExpectedSecret, 1790000000, "{\"version\":1}");

        Assert.AreNotEqual(signature, EndpointAlertWebhook.Sign(ExpectedSecret, 1790000001, "{\"version\":1}"));
        Assert.AreNotEqual(signature, EndpointAlertWebhook.Sign(ExpectedSecret, 1790000000, "{\"version\":2}"));
        Assert.IsTrue(signature.Skip(3).All(Uri.IsHexDigit));
    }
}
