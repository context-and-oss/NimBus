#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Management.ServiceBus;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.Controllers;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The per-endpoint "send" kill switch: EndpointManagement maps endpoint → topic and
/// EndpointImplementation exposes it over REST with per-endpoint authorization + auditing.
/// Send-disable sets the endpoint's TOPIC to EntityStatus.SendDisabled (distinct from the
/// existing receive switch, which sets the SUBSCRIPTION to ReceiveDisabled).
/// </summary>
[TestClass]
public sealed class EndpointSendStatusTests
{
    [TestMethod]
    public async Task EndpointManagement_DisableEndpointSend_disables_topic()
    {
        var sb = new FakeServiceBusManagement();
        var mgmt = new EndpointManagement(sb);

        await mgmt.DisableEndpointSend("ep-1");

        Assert.AreEqual(TopicSendState.SendDisabled, sb.TopicState("ep-1"));
        CollectionAssert.Contains(sb.Calls, "DisableTopicSend:ep-1");
    }

    [TestMethod]
    public async Task EndpointManagement_EnableEndpointSend_enables_topic()
    {
        var sb = new FakeServiceBusManagement();
        sb.SetTopicState("ep-1", TopicSendState.SendDisabled);
        var mgmt = new EndpointManagement(sb);

        await mgmt.EnableEndpointSend("ep-1");

        Assert.AreEqual(TopicSendState.Enabled, sb.TopicState("ep-1"));
        Assert.AreEqual(TopicSendState.Enabled, await mgmt.GetEndpointSendState("ep-1"));
    }

    [TestMethod]
    public async Task PostSendstatus_disable_sets_topic_and_audits()
    {
        var sb = new FakeServiceBusManagement();
        var audit = new FakeAuditLogService();
        var sut = CreateSut(sb, audit, canManage: true, "ep-1");

        var result = await sut.PostEndpointSendstatusAsync("disable", "ep-1");

        Assert.IsInstanceOfType(result, typeof(OkObjectResult));
        Assert.AreEqual(TopicSendState.SendDisabled, sb.TopicState("ep-1"));
        Assert.IsTrue(audit.Entries.Any(e =>
            e.Type == MessageAuditType.DisableEndpointSend && !e.AccessDenied && e.EndpointId == "ep-1"));
    }

    [TestMethod]
    public async Task PostSendstatus_enable_sets_topic_active()
    {
        var sb = new FakeServiceBusManagement();
        sb.SetTopicState("ep-1", TopicSendState.SendDisabled);
        var sut = CreateSut(sb, new FakeAuditLogService(), canManage: true, "ep-1");

        var result = await sut.PostEndpointSendstatusAsync("enable", "ep-1");

        Assert.IsInstanceOfType(result, typeof(OkObjectResult));
        Assert.AreEqual(TopicSendState.Enabled, sb.TopicState("ep-1"));
    }

    [TestMethod]
    public async Task PostSendstatus_unauthorized_forbids_and_audits_denial()
    {
        var sb = new FakeServiceBusManagement();
        var audit = new FakeAuditLogService();
        var sut = CreateSut(sb, audit, canManage: false, "ep-1");

        var result = await sut.PostEndpointSendstatusAsync("disable", "ep-1");

        Assert.IsInstanceOfType(result, typeof(ForbidResult));
        Assert.AreEqual(TopicSendState.Enabled, sb.TopicState("ep-1")); // untouched
        Assert.IsTrue(audit.Entries.Any(e =>
            e.Type == MessageAuditType.DisableEndpointSend && e.AccessDenied && e.EndpointId == "ep-1"));
    }

    [TestMethod]
    public async Task PostSendstatus_unknown_endpoint_returns_404()
    {
        var sut = CreateSut(new FakeServiceBusManagement(), new FakeAuditLogService(), canManage: true, "ep-1");

        var result = await sut.PostEndpointSendstatusAsync("disable", "does-not-exist");

        Assert.IsInstanceOfType(result, typeof(NotFoundObjectResult));
    }

    [TestMethod]
    public async Task GetSendstatus_maps_topic_state_to_string()
    {
        var sb = new FakeServiceBusManagement();
        var sut = CreateSut(sb, new FakeAuditLogService(), canManage: true, "ep-1");

        sb.SetTopicState("ep-1", TopicSendState.Enabled);
        Assert.AreEqual("active", ValueOf(await sut.GetEndpointSendstatusAsync("ep-1")));

        sb.SetTopicState("ep-1", TopicSendState.SendDisabled);
        Assert.AreEqual("disabled", ValueOf(await sut.GetEndpointSendstatusAsync("ep-1")));

        sb.SetTopicState("ep-1", TopicSendState.NotFound);
        Assert.AreEqual("not-found", ValueOf(await sut.GetEndpointSendstatusAsync("ep-1")));
    }

    private static string ValueOf(ActionResult<string> result) =>
        (string)((OkObjectResult)result.Result!).Value!;

    private static EndpointImplementation CreateSut(
        IServiceBusManagement sb, IAuditLogService audit, bool canManage, params string[] endpointIds)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Environment"] = "dev" })
            .Build();

        return new EndpointImplementation(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new FakePlatform(endpointIds),
            configuration,
            messageStore: null!, // send status never touches the message store
            subscriptionStore: null!,
            metadataStore: null!,
            sb,
            topologyRebuilder: null!, // send status never rebuilds topology
            new FakeAuthorizationService(canManage),
            NullLogger<EndpointImplementation>.Instance,
            audit,
            new StoreResultCache(new MemoryCache(new MemoryCacheOptions())),
            PayloadRedactionTests.NewRedaction());
    }

    // ---------------- Fakes ----------------

    private sealed class FakeServiceBusManagement : IServiceBusManagement
    {
        private readonly Dictionary<string, TopicSendState> _topicState = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = new();

        public TopicSendState TopicState(string topic) =>
            _topicState.TryGetValue(topic, out var s) ? s : TopicSendState.Enabled;

        public void SetTopicState(string topic, TopicSendState state) => _topicState[topic] = state;

        public Task DisableTopicSend(string topicName)
        {
            Calls.Add($"DisableTopicSend:{topicName}");
            _topicState[topicName] = TopicSendState.SendDisabled;
            return Task.CompletedTask;
        }

        public Task EnableTopicSend(string topicName)
        {
            Calls.Add($"EnableTopicSend:{topicName}");
            _topicState[topicName] = TopicSendState.Enabled;
            return Task.CompletedTask;
        }

        public Task<TopicSendState> GetTopicSendState(string topicName) => Task.FromResult(TopicState(topicName));

        // Unused by the send kill switch.
        public Task CreateCustomRule(string t, string s, string r, string f, string? a) => throw new NotSupportedException();
        public Task CreateSubscription(string t, string s) => throw new NotSupportedException();
        public Task DeleteRule(string t, string s, string r) => throw new NotSupportedException();
        public Task DeleteSubscription(string t, string s) => throw new NotSupportedException();
        public Task DeleteTopic(string t) => throw new NotSupportedException();
        public Task DisableSubscription(string t, string s) => throw new NotSupportedException();
        public Task EnableSubscription(string t, string s) => throw new NotSupportedException();
        public Task<bool> IsSubscriptionActive(string t, string s) => throw new NotSupportedException();
        public Task<SubscriptionState> GetSubscriptionState(string t, string s) => throw new NotSupportedException();
        public Task UpdateForwardTo(string t, string s, string f) => throw new NotSupportedException();
        public Task UpdateSubscription(string t, string s, Azure.Messaging.ServiceBus.Administration.EntityStatus status, string? f, bool c) => throw new NotSupportedException();
        public Task<Azure.Messaging.ServiceBus.Administration.SubscriptionProperties?> GetSubscription(string t, string s) => throw new NotSupportedException();
        public IAsyncEnumerable<Azure.Messaging.ServiceBus.Administration.TopicProperties> ListTopicsAsync() => throw new NotSupportedException();
        public IAsyncEnumerable<Azure.Messaging.ServiceBus.Administration.SubscriptionProperties> ListSubscriptionsAsync(string t) => throw new NotSupportedException();
        public IAsyncEnumerable<Azure.Messaging.ServiceBus.Administration.RuleProperties> ListRulesAsync(string t, string s) => throw new NotSupportedException();
        public IAsyncEnumerable<Azure.Messaging.ServiceBus.Administration.TopicRuntimeProperties> ListTopicRuntimePropertiesAsync() => throw new NotSupportedException();
        public IAsyncEnumerable<Azure.Messaging.ServiceBus.Administration.SubscriptionRuntimeProperties> ListSubscriptionRuntimePropertiesAsync(string t) => throw new NotSupportedException();
    }

    private sealed record AuditEntry(MessageAuditType Type, bool AccessDenied, string? EndpointId);

    private sealed class FakeAuditLogService : IAuditLogService
    {
        public List<AuditEntry> Entries { get; } = new();

        public Task LogAuditAsync(
            MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null,
            string? auditorNameOverride = null, CancellationToken cancellationToken = default)
        {
            Entries.Add(new AuditEntry(type, accessDenied, endpointId));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAuthorizationService : IEndpointAuthorizationService
    {
        private readonly bool _canManage;
        public FakeAuthorizationService(bool canManage) => _canManage = canManage;

        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(_canManage);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(_canManage);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess
        {
            SiteRole = _canManage ? AccessRole.Owner : AccessRole.None,
            IsPiiReader = _canManage,
        });

        public string GetCurrentUserName() => "test-user";
    }

    private sealed class FakePlatform : IPlatform
    {
        private readonly List<IEndpoint> _endpoints;
        public FakePlatform(IEnumerable<string> ids) => _endpoints = ids.Select(id => (IEndpoint)new FakeEndpoint(id)).ToList();
        public IEnumerable<IEndpoint> Endpoints => _endpoints;
        public IEnumerable<IEventType> EventTypes => Enumerable.Empty<IEventType>();
        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
    }

    private sealed class FakeEndpoint : IEndpoint
    {
        public FakeEndpoint(string id) => Id = id;
        public string Id { get; }
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => string.Empty;
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => Enumerable.Empty<IEventType>();
        public IEnumerable<IEventType> EventTypesConsumed => Enumerable.Empty<IEventType>();
        public IEnumerable<IRoleAssignment> RoleAssignments => Enumerable.Empty<IRoleAssignment>();
    }
}
