#pragma warning disable CA1707, CA2007

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.SDK;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// HTTP-boundary coverage for agent settlement. These tests deliberately send
/// raw JSON so omitted and unknown fields pass through the same System.Text.Json
/// model binding used in production.
/// </summary>
[TestClass]
public sealed class AgentSettleContractTests
{
    private const string EventId = "event-1";
    private const string MessageId = "message-1";
    private const string ZoneId = AgentZone.DefaultAgentZoneEndpointId;

    [TestMethod]
    [DataRow("{\"coordinates\":{\"eventId\":\"event-1\",\"messageId\":\"message-1\"}}", DisplayName = "missing outcome")]
    [DataRow("{\"coordinates\":{\"eventId\":\"event-1\"},\"outcome\":\"complete\"}", DisplayName = "missing messageId")]
    [DataRow("{\"coordinates\":{\"eventId\":\"event-1\",\"messageId\":\"   \"},\"outcome\":\"complete\"}", DisplayName = "blank messageId")]
    [DataRow("{\"coordinates\":{\"eventId\":\"event-1\",\"messageId\":\"message-1\"},\"outcome\":\"unknown\"}", DisplayName = "unknown string outcome")]
    [DataRow("{\"coordinates\":{\"eventId\":\"event-1\",\"messageId\":\"message-1\"},\"outcome\":99,\"errorText\":\"must not settle\"}", DisplayName = "unknown numeric outcome")]
    public async Task PostAgentSettle_malformed_contract_returns_400_without_side_effects(string json)
    {
        var store = new InMemoryMessageStore();
        await SeedPendingHandoff(store);
        var handoffs = new CapturingHandoffClientFactory();

        using var host = await CreateHost(store, handoffs);
        using var client = host.GetTestServer().CreateClient();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/agent/settle", content);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(0, handoffs.SettlementCount,
            "Invalid input must be rejected before a settlement control message is published.");
        var audits = await store.GetMessageAudits(EventId);
        Assert.IsFalse(audits.Any(),
            "Invalid input must not produce an audit row that implies settlement occurred.");
    }

    private static async Task<IHost> CreateHost(
        InMemoryMessageStore store,
        CapturingHandoffClientFactory handoffs)
    {
        var audit = new AuditLogService(NullLogger<AuditLogService>.Instance, store);
        var settlement = new HandoffSettlementService(
            store,
            audit,
            new HandoffSettlementTracker(TimeProvider.System),
            NullLogger<HandoffSettlementService>.Instance);
        var implementation = new AgentImplementation(
            store,
            platform: null!,
            publisher: null!,
            store,
            handoffs,
            settlement,
            new AgentSubscriptionRegistry(),
            config: null!,
            httpContextAccessor: null!,
            NullLogger<AgentImplementation>.Instance);

        var builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<IAgentApiController>(implementation);
                    services.AddControllers()
                        .AddApplicationPart(typeof(AgentApiController).Assembly)
                        .AddJsonOptions(options =>
                            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            });

        return await builder.StartAsync();
    }

    private static Task SeedPendingHandoff(InMemoryMessageStore store) =>
        store.UploadPendingMessage(EventId, "session-1", ZoneId, new UnresolvedEvent
        {
            EventTypeId = "orders.created.v1",
            LastMessageId = MessageId,
            CorrelationId = "correlation-1",
            OriginatingMessageId = "origin-1",
            PendingSubStatus = "Handoff",
        });

    private sealed class CapturingHandoffClientFactory : IHandoffClientFactory
    {
        public int SettlementCount { get; private set; }

        public IHandoffClient ForEndpoint(string endpointId) => new Client(this);

        private sealed class Client : IHandoffClient
        {
            private readonly CapturingHandoffClientFactory _owner;

            public Client(CapturingHandoffClientFactory owner)
            {
                _owner = owner;
            }

            public Task CompleteAsync(
                HandoffSettlement coords,
                object? result = null,
                CancellationToken cancellationToken = default)
            {
                _owner.SettlementCount++;
                return Task.CompletedTask;
            }

            public Task FailAsync(
                HandoffSettlement coords,
                string errorText,
                string? errorType = null,
                CancellationToken cancellationToken = default)
            {
                _owner.SettlementCount++;
                return Task.CompletedTask;
            }
        }
    }
}
