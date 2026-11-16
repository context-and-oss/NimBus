#pragma warning disable CA1707, CA2007
using System.Collections.Generic;
using System.Linq;
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
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using ApiSubscription = NimBus.WebApp.ManagementApi.EndpointSubscription;

namespace NimBus.WebApp.Tests;

/// <summary>
/// <c>POST /api/v1/endpoint/{endpointId}/subscribe</c> creates an alert subscription. A webhook
/// subscription needs an https URL and a deployment with a signing key, and answers once with the
/// secret its requests are signed with. Invalid input is a 400 on every storage provider.
/// </summary>
[TestClass]
public sealed class EndpointAlertSubscribeTests
{
    private const string Endpoint = "ep-a";
    private const string SigningKey = "0123456789abcdef0123456789abcdef";

    private static ApiSubscription Webhook(string url = "https://agent.example.com/hooks/nimbus") => new()
    {
        Type = "webhook",
        Url = url,
        EventTypes = [],
        Frequency = 300,
    };

    private static ApiSubscription Mail(string? mail = "ops@example.com") => new()
    {
        Type = "mail",
        Mail = mail!,
        EventTypes = [],
        Frequency = 3600,
    };

    [TestMethod]
    public async Task A_webhook_subscription_answers_with_its_signing_secret()
    {
        var store = new InMemoryMessageStore();

        var result = await CreateSut(store).PostEndpointSubscribeAsync(Webhook(), Endpoint);

        var created = Created(result);
        var stored = (await store.GetSubscriptionsOnEndpoint(Endpoint)).Single();
        Assert.AreEqual(stored.Id, created.Id);
        Assert.AreEqual("webhook", stored.Type);
        Assert.AreEqual("https://agent.example.com/hooks/nimbus", stored.Url);
        Assert.AreEqual("test-user", stored.AuthorId);
        Assert.AreEqual(EndpointAlertWebhook.DeriveSigningSecret(SigningKey, stored.Id), created.SigningSecret);
    }

    [TestMethod]
    public async Task The_secret_is_not_returned_when_the_subscriptions_are_listed()
    {
        var store = new InMemoryMessageStore();
        var sut = CreateSut(store);
        var created = Created(await sut.PostEndpointSubscribeAsync(Webhook(), Endpoint));

        var listed = await ((IEndpointApiController)sut).GetEndpointSubscribeAsync(Endpoint);

        var body = System.Text.Json.JsonSerializer.Serialize(((OkObjectResult)listed.Result!).Value);
        Assert.IsFalse(body.Contains(created.SigningSecret!, System.StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("signingSecret", System.StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task A_mail_subscription_answers_without_a_secret()
    {
        var store = new InMemoryMessageStore();

        var created = Created(await CreateSut(store).PostEndpointSubscribeAsync(Mail(), Endpoint));

        Assert.AreEqual((await store.GetSubscriptionsOnEndpoint(Endpoint)).Single().Id, created.Id);
        Assert.IsNull(created.SigningSecret);
    }

    [TestMethod]
    public async Task A_mail_subscription_needs_no_signing_key()
    {
        var store = new InMemoryMessageStore();

        var result = await CreateSut(store, signingKey: null).PostEndpointSubscribeAsync(Mail(), Endpoint);

        Assert.IsNotNull(Created(result).Id);
    }

    [TestMethod]
    public async Task A_webhook_is_refused_while_the_deployment_has_no_signing_key()
    {
        var store = new InMemoryMessageStore();

        var result = await CreateSut(store, signingKey: null).PostEndpointSubscribeAsync(Webhook(), Endpoint);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.AreEqual(0, (await store.GetSubscriptionsOnEndpoint(Endpoint)).Count());
    }

    [TestMethod]
    [DataRow("http://agent.example.com/hook", DisplayName = "http")]
    [DataRow("agent.example.com/hook", DisplayName = "not absolute")]
    [DataRow("https://user:pw@agent.example.com/hook", DisplayName = "user information")]
    [DataRow("", DisplayName = "empty")]
    [DataRow(null, DisplayName = "missing")]
    public async Task A_webhook_url_that_is_not_plain_https_is_refused(string? url)
    {
        var store = new InMemoryMessageStore();

        var result = await CreateSut(store).PostEndpointSubscribeAsync(Webhook(url!), Endpoint);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.AreEqual(0, (await store.GetSubscriptionsOnEndpoint(Endpoint)).Count());
    }

    [TestMethod]
    public async Task An_http_webhook_is_accepted_when_the_deployment_allows_it()
    {
        var store = new InMemoryMessageStore();
        var sut = CreateSut(store, allowInsecureHttp: true);

        var result = await sut.PostEndpointSubscribeAsync(Webhook("http://localhost:5090/hook"), Endpoint);

        Assert.IsNotNull(Created(result).SigningSecret);
    }

    [TestMethod]
    [DataRow("sms")]
    [DataRow("mail;sms")]
    [DataRow("")]
    [DataRow(null)]
    public async Task An_unknown_type_is_refused(string? type)
    {
        var store = new InMemoryMessageStore();
        var body = Mail();
        body.Type = type!;

        var result = await CreateSut(store).PostEndpointSubscribeAsync(body, Endpoint);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.AreEqual(0, (await store.GetSubscriptionsOnEndpoint(Endpoint)).Count());
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not-an-address")]
    public async Task A_mail_subscription_needs_an_address(string? mail)
    {
        var store = new InMemoryMessageStore();

        var result = await CreateSut(store).PostEndpointSubscribeAsync(Mail(mail), Endpoint);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.AreEqual(0, (await store.GetSubscriptionsOnEndpoint(Endpoint)).Count());
    }

    [TestMethod]
    public async Task A_negative_frequency_is_refused()
    {
        var store = new InMemoryMessageStore();
        var body = Webhook();
        body.Frequency = -1;

        var result = await CreateSut(store).PostEndpointSubscribeAsync(body, Endpoint);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task A_caller_who_is_not_the_endpoints_owner_is_refused()
    {
        var store = new InMemoryMessageStore();

        var result = await CreateSut(store, owner: false).PostEndpointSubscribeAsync(Webhook(), Endpoint);

        Assert.IsInstanceOfType<ForbidResult>(result.Result);
        Assert.AreEqual(0, (await store.GetSubscriptionsOnEndpoint(Endpoint)).Count());
    }

    private static EndpointSubscriptionCreated Created(ActionResult<EndpointSubscriptionCreated> result)
    {
        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok, $"Expected 200, got {result.Result?.GetType().Name}: {(result.Result as ObjectResult)?.Value}");
        return (EndpointSubscriptionCreated)ok.Value!;
    }

    private static EndpointImplementation CreateSut(
        InMemoryMessageStore store, string? signingKey = SigningKey, bool allowInsecureHttp = false, bool owner = true)
    {
        var settings = new Dictionary<string, string?> { ["Environment"] = "dev" };
        if (signingKey is not null)
        {
            settings["NimBus:Alerts:Webhook:SigningKey"] = signingKey;
        }

        if (allowInsecureHttp)
        {
            settings["NimBus:Alerts:Webhook:AllowInsecureHttp"] = "true";
        }

        return new EndpointImplementation(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new FakePlatform(Endpoint),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            store,
            store,
            store,
            serviceBusManagement: null!,
            new FixedAuthorization(owner),
            NullLogger<EndpointImplementation>.Instance,
            auditLogService: null!,
            new StoreResultCache(new MemoryCache(new MemoryCacheOptions())),
            PayloadRedactionTests.NewRedaction());
    }

    private sealed class FixedAuthorization(bool owner) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(owner);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess());

        public string GetCurrentUserName() => "test-user";
    }

    private sealed class FakePlatform(params string[] endpointIds) : IPlatform
    {
        public IEnumerable<IEndpoint> Endpoints { get; } = endpointIds.Select(id => (IEndpoint)new FakeEndpoint(id)).ToList();
        public IEnumerable<IEventType> EventTypes => [];
        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => [];
        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => [];
    }

    private sealed class FakeEndpoint(string id) : IEndpoint
    {
        public string Id { get; } = id;
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => string.Empty;
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => [];
        public IEnumerable<IEventType> EventTypesConsumed => [];
        public IEnumerable<IRoleAssignment> RoleAssignments => [];
    }
}
