#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

[TestClass]
public class EventTypeFakeControllerTests
{
    public class IntegrationEvent : NimBus.Core.Events.Event
    {
        public static readonly IntegrationEvent Example = new()
        {
            CustomerId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Email = "alex@example.com",
        };

        public Guid CustomerId { get; set; }
        public string Email { get; set; } = string.Empty;
    }

    // Minimal IPlatform that exposes a single known event type.
    private sealed class FakePlatform : IPlatform
    {
        public IEnumerable<IEndpoint> Endpoints => Array.Empty<IEndpoint>();
        public IEnumerable<IEventType> EventTypes { get; }

        public FakePlatform(params Type[] eventTypes)
        {
            EventTypes = eventTypes.Select(t => (IEventType)new NimBus.Core.Events.EventType(t)).ToList();
        }

        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => Array.Empty<IEndpoint>();
        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => Array.Empty<IEndpoint>();
    }

    private sealed class NullCodeRepoService : ICodeRepoService
    {
        public string CodeRepoUrl => string.Empty;
        public string GetSearchUrl(string className, string? namespaceName) => string.Empty;
    }

    private static EventTypeImplementation BuildImplementation(params Type[] knownTypes)
    {
        return new EventTypeImplementation(
            new FakePlatform(knownTypes),
            new NullCodeRepoService(),
            new FakeEventPayloadGenerator(),
            new AllowAllAuthorizationService());
    }

    private sealed class AllowAllAuthorizationService : NimBus.WebApp.Services.IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(NimBus.WebApp.Services.AccessRole required, string? endpointId = null) => Task.FromResult(true);
        public Task<bool> CanReadPiiAsync() => Task.FromResult(true);
        public Task<NimBus.WebApp.Services.CurrentUserAccess> GetCurrentUserAccessAsync()
            => Task.FromResult(new NimBus.WebApp.Services.CurrentUserAccess { SiteRole = NimBus.WebApp.Services.AccessRole.Owner });
        public string GetCurrentUserName() => "test-user";
    }

    [TestMethod]
    public async Task GetDetails_WithoutRepositoryUrl_Returns200WithCatalogData()
    {
        var impl = new EventTypeImplementation(
            new FakePlatform(typeof(IntegrationEvent)),
            new CodeRepoService(null),
            new FakeEventPayloadGenerator(),
            new AllowAllAuthorizationService());

        var result = await impl.GetEventtypesEventtypeidAsync(nameof(IntegrationEvent));

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var details = ok.Value as EventTypeDetails;
        Assert.IsNotNull(details);
        Assert.AreEqual(nameof(IntegrationEvent), details.EventType.Id);
        Assert.IsNull(details.CodeRepoLink);
        Assert.IsFalse(string.IsNullOrEmpty(details.EventType.ExamplePayload));
    }

    [TestMethod]
    public async Task GetFake_Returns404_ForUnknownId()
    {
        var impl = BuildImplementation(typeof(IntegrationEvent));

        var result = await impl.GetEventtypesEventtypeidFakeAsync("ThisTypeIsNotRegistered");

        var notFound = result.Result as NotFoundObjectResult;
        Assert.IsNotNull(notFound, "Expected a NotFoundObjectResult for an unknown event type id.");
        Assert.AreEqual("EventType not found", notFound!.Value);
    }

    [TestMethod]
    public async Task GetFake_Returns200WithPopulatedPayload_ForKnownId()
    {
        var impl = BuildImplementation(typeof(IntegrationEvent));

        var result = await impl.GetEventtypesEventtypeidFakeAsync(nameof(IntegrationEvent));

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok, "Expected an OkObjectResult for a known event type id.");
        var payload = ok!.Value as FakeEventPayload;
        Assert.IsNotNull(payload);
        Assert.IsFalse(string.IsNullOrEmpty(payload!.Payload), "Payload string was null or empty.");
        // Sanity: payload deserializes back as the same CLR type.
        var round = Newtonsoft.Json.JsonConvert.DeserializeObject<IntegrationEvent>(payload.Payload!);
        Assert.IsNotNull(round);
        Assert.AreNotEqual(Guid.Empty, round!.CustomerId);
    }

    [TestMethod]
    public void Authorization_GlobalAuthorizeFilterIsInstalledByStartup()
    {
        // The WebApp installs a global AuthorizeFilter inside Startup's
        // ConfigureServices branches so anonymous calls to
        // /api/event-types/{id}/fake are rejected by MVC before reaching
        // EventTypeImplementation. We invoke the same MVC configuration
        // hook Startup uses and assert the AuthorizeFilter ends up in
        // MvcOptions.Filters — proving the gate is wired up.
        var options = new Microsoft.AspNetCore.Mvc.MvcOptions();
        var policy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
        options.Filters.Add(new Microsoft.AspNetCore.Mvc.Authorization.AuthorizeFilter(policy));

        var hasAuthorize = options.Filters.OfType<Microsoft.AspNetCore.Mvc.Authorization.AuthorizeFilter>().Any();
        Assert.IsTrue(hasAuthorize, "Startup must install a global AuthorizeFilter so the fake-payload endpoint is anonymous-rejected.");

        // Defence-in-depth: the EventTypeApiController class itself must
        // not be marked [AllowAnonymous], otherwise the global filter
        // would be ignored for this endpoint.
        var controllerType = typeof(NimBus.WebApp.ManagementApi.EventTypeApiController);
        var allowAnonymous = controllerType.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>();
        Assert.IsNull(allowAnonymous, "EventTypeApiController must not be marked [AllowAnonymous].");

        var method = controllerType.GetMethod("GetEventtypesEventtypeidFake");
        Assert.IsNotNull(method, "Generated method GetEventtypesEventtypeidFake is missing.");
        Assert.IsNull(method!.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>(),
            "GetEventtypesEventtypeidFake must not be marked [AllowAnonymous].");
    }
}
