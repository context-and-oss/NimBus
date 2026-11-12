#pragma warning disable CA1707, CA2007
extern alias CrmApi;

using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PartnerLeadsState = CrmApi::Crm.Api.PartnerLeadsState;

namespace CrmErpDemo.AppHost.Tests;

/// <summary>
/// The crm-web "Partner leads" switch: crm-api holds the flag, partner-portal asks for it
/// before each lead.
/// </summary>
[TestClass]
public sealed class PartnerLeadSwitchTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task IsEnabledAsync_WithoutCrmApi_ReturnsStartupSetting(bool enabledAtStart)
    {
        var leadSwitch = new PartnerLeadSwitch(crmApi: null, enabledAtStart);

        Assert.IsFalse(leadSwitch.IsRemote);
        Assert.AreEqual(enabledAtStart, await leadSwitch.IsEnabledAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task IsEnabledAsync_FollowsTheCrmApiFlag()
    {
        var handler = new StubHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://crm-api.test") };
        var leadSwitch = new PartnerLeadSwitch(http, enabledAtStart: true);

        handler.Respond(HttpStatusCode.OK, """{"enabled":false,"changedAt":"2026-11-12T08:00:00+00:00"}""");
        Assert.IsFalse(await leadSwitch.IsEnabledAsync(CancellationToken.None));

        handler.Respond(HttpStatusCode.OK, """{"enabled":true,"changedAt":"2026-11-12T08:01:00+00:00"}""");
        Assert.IsTrue(await leadSwitch.IsEnabledAsync(CancellationToken.None));

        Assert.IsTrue(leadSwitch.IsRemote);
        Assert.AreEqual("/api/admin/partner-leads", handler.LastRequestPath);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable, "")]
    [DataRow(HttpStatusCode.Unauthorized, "")]
    [DataRow(HttpStatusCode.OK, "<html>sign in</html>")]
    public async Task IsEnabledAsync_WhenCrmApiCannotAnswer_KeepsTheLastKnownValue(HttpStatusCode status, string body)
    {
        var handler = new StubHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://crm-api.test") };
        var leadSwitch = new PartnerLeadSwitch(http, enabledAtStart: true);

        handler.Respond(status, body);
        Assert.IsTrue(await leadSwitch.IsEnabledAsync(CancellationToken.None), "Falls back to the startup setting.");

        handler.Respond(HttpStatusCode.OK, """{"enabled":false,"changedAt":"2026-11-12T08:00:00+00:00"}""");
        Assert.IsFalse(await leadSwitch.IsEnabledAsync(CancellationToken.None));

        handler.Respond(status, body);
        Assert.IsFalse(await leadSwitch.IsEnabledAsync(CancellationToken.None), "Keeps the last answer crm-api gave.");
    }

    [TestMethod]
    public async Task IsEnabledAsync_WhenCrmApiIsUnreachable_KeepsTheLastKnownValue()
    {
        var handler = new StubHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://crm-api.test") };
        var leadSwitch = new PartnerLeadSwitch(http, enabledAtStart: false);

        handler.Throw(new HttpRequestException("connection refused"));

        Assert.IsFalse(await leadSwitch.IsEnabledAsync(CancellationToken.None));
    }

    [TestMethod]
    public void CreateClient_SendsTheDemoServiceKeyOnlyWhenConfigured()
    {
        Assert.IsNull(PartnerLeadSwitch.CreateClient(crmApiUrl: null, serviceKey: "key"));
        Assert.IsNull(PartnerLeadSwitch.CreateClient(crmApiUrl: "", serviceKey: "key"));

        using var anonymous = PartnerLeadSwitch.CreateClient("http://localhost:5080", serviceKey: null);
        Assert.IsNotNull(anonymous);
        Assert.AreEqual(new Uri("http://localhost:5080"), anonymous.BaseAddress);
        Assert.IsFalse(anonymous.DefaultRequestHeaders.Contains("X-Demo-Service-Key"));

        using var keyed = PartnerLeadSwitch.CreateClient("https://crm-api.example", serviceKey: "secret-key");
        Assert.IsNotNull(keyed);
        Assert.AreEqual("secret-key", keyed.DefaultRequestHeaders.GetValues("X-Demo-Service-Key").Single());
    }

    [TestMethod]
    [DataRow(null, true)]
    [DataRow("true", true)]
    [DataRow("false", false)]
    public void PartnerLeadsState_StartsFromThePublishLeadsSetting(string? configured, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PARTNER_PUBLISH_LEADS"] = configured })
            .Build();

        var state = new PartnerLeadsState(PartnerLeadsState.EnabledAtStart(configuration));

        Assert.AreEqual(expected, state.Snapshot().Enabled);
    }

    [TestMethod]
    public void PartnerLeadsState_Set_ChangesTheFlagAndStampsTheChange()
    {
        var state = new PartnerLeadsState(enabledAtStart: true);
        var startedAt = state.Snapshot().ChangedAt;

        var unchanged = state.Set(true);
        Assert.AreEqual(startedAt, unchanged.ChangedAt, "Setting the same value is not a change.");

        var changed = state.Set(false);
        Assert.IsFalse(changed.Enabled);
        Assert.IsFalse(state.Snapshot().Enabled);
        Assert.IsTrue(changed.ChangedAt >= startedAt);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "";
        private Exception? _exception;

        public string? LastRequestPath { get; private set; }

        public void Respond(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
            _exception = null;
        }

        public void Throw(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestPath = request.RequestUri?.AbsolutePath;
            if (_exception is not null) throw _exception;

            var mediaType = _body.StartsWith('{') ? "application/json" : "text/html";
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, mediaType),
            });
        }
    }
}
