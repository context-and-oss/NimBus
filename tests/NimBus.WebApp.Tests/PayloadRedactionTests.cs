#pragma warning disable CA1707, CA2007
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using NimBus.Core.Messages.PII;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Field-level payload masking: only [Sensitive] values are replaced for callers
/// without the PII Reader role, and the helper fails closed to a whole-payload
/// marker when the event type cannot be resolved. Also covers the Reader floor on
/// the previously-ungated cross-endpoint read APIs.
/// </summary>
[TestClass]
public class PayloadRedactionTests
{
    // Fully qualified: NimBus.Core's Event/Endpoint collide with the ManagementApi
    // DTO Event and Microsoft.AspNetCore.Http.Endpoint used elsewhere in this file.
    public class OrderPlaced : NimBus.Core.Events.Event
    {
        [Sensitive]
        public string Cpr { get; set; } = string.Empty;

        public string OrderId { get; set; } = string.Empty;
    }

    private sealed class MaskingEndpoint : NimBus.Core.Endpoints.Endpoint
    {
        public MaskingEndpoint() { Produces<OrderPlaced>(); }
    }

    private sealed class MaskingPlatform : NimBus.Core.Platform
    {
        public MaskingPlatform() { AddEndpoint(new MaskingEndpoint()); }
    }

    internal static PayloadRedaction NewRedaction() =>
        new(new EventJsonMasker(new MaskingPlatform()));

    private const string OrderJson = "{\"Cpr\":\"010101-1234\",\"OrderId\":\"A-1\"}";

    [TestMethod]
    public void Redact_event_masks_only_sensitive_fields_and_keeps_type_id()
    {
        var e = new Event
        {
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventJson = OrderJson, EventTypeId = nameof(OrderPlaced) },
            },
        };

        NewRedaction().Redact(e);

        var parsed = JObject.Parse(e.MessageContent.EventContent.EventJson);
        Assert.AreEqual("***", (string?)parsed["Cpr"], "The [Sensitive] field must be masked.");
        Assert.AreEqual("A-1", (string?)parsed["OrderId"], "Non-sensitive fields must stay readable.");
        Assert.AreEqual(nameof(OrderPlaced), e.MessageContent.EventContent.EventTypeId);
    }

    [TestMethod]
    public void Redact_event_fails_closed_when_event_type_is_unresolvable()
    {
        var e = new Event
        {
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventJson = OrderJson, EventTypeId = "NotAKnownType" },
            },
        };

        NewRedaction().Redact(e);

        Assert.AreEqual(
            EventJsonMasker.UnknownTypeMarker,
            e.MessageContent.EventContent.EventJson,
            "An unresolvable type must never leave the payload readable.");
    }

    [TestMethod]
    public void Redact_is_null_safe_and_leaves_empty_payloads_untouched()
    {
        var redaction = NewRedaction();

        Assert.IsNull(redaction.Redact((Event?)null));
        Assert.IsNull(redaction.Redact((Message?)null));
        Assert.IsNull(redaction.Redact((EventDetails?)null));
        Assert.IsNull(redaction.Redact((EndpointStatus?)null));

        var noPayload = new Event { MessageContent = new MessageContent { EventContent = new EventContent() } };
        redaction.Redact(noPayload);
        Assert.IsNull(noPayload.MessageContent.EventContent.EventJson);
    }

    [TestMethod]
    public void Redact_message_and_details_and_status_and_log_and_subscription()
    {
        var redaction = NewRedaction();

        var message = new Message { EventTypeId = nameof(OrderPlaced), EventContent = OrderJson };
        redaction.Redact(message);
        Assert.AreEqual("***", (string?)JObject.Parse(message.EventContent)["Cpr"]);
        Assert.AreEqual("A-1", (string?)JObject.Parse(message.EventContent)["OrderId"]);

        var details = new EventDetails
        {
            FailedMessage = new Message { EventTypeId = nameof(OrderPlaced), EventContent = OrderJson },
            OriginatingMessage = new Message { EventTypeId = nameof(OrderPlaced), EventContent = OrderJson },
        };
        redaction.Redact(details);
        Assert.AreEqual("***", (string?)JObject.Parse(details.FailedMessage.EventContent)["Cpr"]);
        Assert.AreEqual("***", (string?)JObject.Parse(details.OriginatingMessage.EventContent)["Cpr"]);

        var status = new EndpointStatus
        {
            EnrichedUnresolvedEvents = new UnresolvedEvents
            {
                new Event
                {
                    MessageContent = new MessageContent
                    {
                        EventContent = new EventContent { EventJson = OrderJson, EventTypeId = nameof(OrderPlaced) },
                    },
                },
            },
        };
        redaction.Redact(status);
        var statusJson = status.EnrichedUnresolvedEvents.Single().MessageContent.EventContent.EventJson;
        Assert.AreEqual("***", (string?)JObject.Parse(statusJson)["Cpr"]);

        var log = new EventLogEntry { EventType = nameof(OrderPlaced), Payload = OrderJson };
        redaction.Redact(log);
        Assert.AreEqual("***", (string?)JObject.Parse(log.Payload)["Cpr"]);

        // Subscription filters are operator-authored fragments with no event type to
        // resolve annotations against, so they are omitted rather than masked.
        var sub = new ManagementApi.EndpointSubscription { Payload = "fragment" };
        PayloadRedaction.RedactSubscription(sub);
        Assert.IsNull(sub.Payload);
    }

    [TestMethod]
    public void Redact_adds_marker_so_a_masked_payload_is_detectable_on_resubmit()
    {
        var e = new Event
        {
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventJson = OrderJson, EventTypeId = nameof(OrderPlaced) },
            },
        };

        NewRedaction().Redact(e);

        var masker = new EventJsonMasker(new MaskingPlatform());
        Assert.IsTrue(
            masker.ContainsRedactPlaceholder(nameof(OrderPlaced), e.MessageContent.EventContent.EventJson),
            "A masked payload must be detectable, otherwise the resubmit gate cannot reject it.");
    }
}

/// <summary>
/// The Reader floor: cross-endpoint reads (metrics, event types, message
/// search) require at least a site Reader; role-less authenticated users get
/// 403, Readers get through.
/// </summary>
[TestClass]
public class ReaderFloorTests
{
    private sealed class StubAuthz : IEndpointAuthorizationService
    {
        public AccessRole SiteRole { get; init; } = AccessRole.None;
        public bool PiiReader { get; init; }

        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null)
            => Task.FromResult(SiteRole >= required);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(PiiReader);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync()
            => Task.FromResult(new CurrentUserAccess { SiteRole = SiteRole, IsPiiReader = PiiReader });

        public string? GetCurrentUserName() => "test-user";
    }

    private static MetricsImplementation Metrics(StubAuthz authz) => FailedInsightsServiceTests.Metrics(
        new InMemoryMessageStore(),
        new StoreResultCache(new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
        authz);

    private static MessageImplementation Messages(StubAuthz authz) => new(
        new InMemoryMessageStore(),
        NullLogger<MessageImplementation>.Instance,
        authz,
        PayloadRedactionTests.NewRedaction());

    [TestMethod]
    public async Task Metrics_require_site_reader()
    {
        var denied = Metrics(new StubAuthz());
        Assert.IsInstanceOfType<ForbidResult>((await denied.GetMetricsOverviewAsync(Period._1d)).Result);
        Assert.IsInstanceOfType<ForbidResult>((await denied.GetMetricsLatencyAsync(Period._1d)).Result);
        Assert.IsInstanceOfType<ForbidResult>((await denied.GetMetricsFailedInsightsAsync(Period._1d)).Result);
        Assert.IsInstanceOfType<ForbidResult>((await denied.GetMetricsTimeseriesAsync(Period._1d)).Result);

        var reader = Metrics(new StubAuthz { SiteRole = AccessRole.Reader });
        Assert.IsNotInstanceOfType<ForbidResult>((await reader.GetMetricsOverviewAsync(Period._1d)).Result);
    }

    [TestMethod]
    public async Task Message_search_requires_site_reader()
    {
        var denied = Messages(new StubAuthz());
        var result = await denied.PostMessagesSearchAsync(new MessageSearchRequest());
        Assert.IsInstanceOfType<ForbidResult>(result.Result);

        var reader = Messages(new StubAuthz { SiteRole = AccessRole.Reader });
        var ok = await reader.PostMessagesSearchAsync(new MessageSearchRequest());
        Assert.IsNull(ok.Result, "A site Reader's search must not be rejected.");
    }
}
