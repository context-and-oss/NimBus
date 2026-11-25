#pragma warning disable CA1707, CA2007
extern alias ErpAdapter;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CrmErpDemo.Contracts.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NimBus.Adapter.Mcp;
using AdapterMcpFunction = ErpAdapter::Erp.Adapter.Functions.Functions.AdapterMcpFunction;
using ErpAdapterMcp = ErpAdapter::Erp.Adapter.Functions.AdapterMcp.ErpAdapterMcp;
using ErpAdapterSystem = ErpAdapter::Erp.Adapter.Functions.AdapterMcp.ErpAdapterSystem;
using ErpApiClient = ErpAdapter::Erp.Adapter.Functions.Clients.ErpApiClient;
using ErpCustomerRecord = ErpAdapter::Erp.Adapter.Functions.Clients.ErpCustomerRecord;
using ErpModeProbe = ErpAdapter::Erp.Adapter.Functions.Clients.ErpModeProbe;
using ErrorModeSnapshot = ErpAdapter::Erp.Adapter.Functions.Clients.ErrorModeSnapshot;
using IErpApiClient = ErpAdapter::Erp.Adapter.Functions.Clients.IErpApiClient;
using IServiceModeClient = ErpAdapter::Erp.Adapter.Functions.Clients.IServiceModeClient;

namespace CrmErpDemo.AppHost.Tests;

/// <summary>
/// The ERP adapter's MCP server (Spec 051 §7): its view of the ERP (customers by CRM account id,
/// the mapping of the CRM events it handles, ERP's health), the call log on its ERP client, and
/// the HTTP trigger that serves it, driven by the official MCP client.
/// </summary>
[TestClass]
public sealed class ErpAdapterMcpTests
{
    private const string AccountId = "6f1c0a5e-4b2d-4e8a-9c3f-1a2b3c4d5e01";

    private static readonly string CustomerJson = $$"""
        {"id":"0b9e0c56-2f7a-4c1e-9d3e-6a1b2c3d4e5f","customerNumber":"C-10042","legalName":"Contoso Nordics ApS",
         "taxId":"DK12345678","countryCode":"DK","crmAccountId":"{{AccountId}}",
         "createdAt":"2026-11-25T09:00:00+00:00","updatedAt":null,"origin":"Crm","isDeleted":false,"creditHold":false}
        """;

    // --- The system -------------------------------------------------------------------------

    [TestMethod]
    public async Task A_customer_is_read_by_its_CRM_account_id()
    {
        var erp = new StubErp();
        var system = new ErpAdapterSystem(Client(erp), new StubModes());

        var record = (ErpCustomerRecord?)await system.GetRecordAsync("customer", AccountId, CancellationToken.None);

        Assert.AreEqual($"/api/customers/by-crm/{AccountId}", erp.Requests.Single().RequestUri!.AbsolutePath);
        Assert.IsNotNull(record);
        Assert.AreEqual("C-10042", record.CustomerNumber);
        Assert.AreEqual("Contoso Nordics ApS", record.LegalName);
        Assert.AreEqual("DK12345678", record.TaxId);
        Assert.AreEqual(Guid.Parse(AccountId), record.CrmAccountId);
    }

    [TestMethod]
    public async Task A_customer_ERP_does_not_have_is_no_record()
    {
        var system = new ErpAdapterSystem(Client(new StubErp { Status = HttpStatusCode.NotFound }), new StubModes());

        Assert.IsNull(await system.GetRecordAsync("customer", AccountId, CancellationToken.None));
    }

    [TestMethod]
    public async Task A_key_that_is_not_a_guid_is_no_record_and_reaches_nothing()
    {
        var erp = new StubErp();
        var system = new ErpAdapterSystem(Client(erp), new StubModes());

        Assert.IsNull(await system.GetRecordAsync("customer", "C-10042", CancellationToken.None));
        Assert.IsEmpty(erp.Requests);
    }

    [TestMethod]
    public async Task An_ERP_error_reaches_the_server_as_an_exception()
    {
        var system = new ErpAdapterSystem(Client(new StubErp { Status = HttpStatusCode.InternalServerError }), new StubModes());

        await Assert.ThrowsAsync<Exception>(() => system.GetRecordAsync("customer", AccountId, CancellationToken.None));
    }

    [TestMethod]
    public void The_tax_id_is_masked_as_on_the_CRM_events()
    {
        var onRecord = typeof(ErpCustomerRecord).GetProperty(nameof(ErpCustomerRecord.TaxId))!
            .GetCustomAttributes(typeof(NimBus.Core.Messages.PII.SensitiveAttribute), true).Cast<NimBus.Core.Messages.PII.SensitiveAttribute>().Single();
        var onEvent = typeof(CrmAccountCreated).GetProperty(nameof(CrmAccountCreated.TaxId))!
            .GetCustomAttributes(typeof(NimBus.Core.Messages.PII.SensitiveAttribute), true).Cast<NimBus.Core.Messages.PII.SensitiveAttribute>().Single();

        Assert.AreEqual(onEvent.Mode, onRecord.Mode);
        Assert.AreEqual(onEvent.Reveal, onRecord.Reveal);
    }

    [TestMethod]
    public void The_mapping_of_CrmAccountCreated_names_each_ERP_field()
    {
        var mapping = new ErpAdapterSystem(Client(new StubErp()), new StubModes()).GetMapping(nameof(CrmAccountCreated));

        Assert.IsNotNull(mapping);
        var fields = mapping.Fields.ToDictionary(f => f.EventPath, f => f.SystemField);
        Assert.AreEqual("CrmAccountId", fields["AccountId"]);
        Assert.AreEqual("LegalName", fields["LegalName"]);
        Assert.AreEqual("TaxId", fields["TaxId"]);
        Assert.AreEqual("CountryCode", fields["CountryCode"]);
        Assert.AreEqual("customer", mapping.Settings["entity"]);
    }

    [TestMethod]
    [DataRow("CrmAccountCreated")]
    [DataRow("CrmAccountUpdated")]
    [DataRow("CrmAccountDeleted")]
    [DataRow("CrmContactCreated")]
    [DataRow("CrmContactUpdated")]
    [DataRow("CrmContactDeleted")]
    [DataRow("ErpCreditCheckRequested")]
    [DataRow("PlaceCustomerOnCreditHold")]
    public void Every_event_type_the_adapter_handles_has_a_mapping(string eventTypeId)
    {
        var mapping = new ErpAdapterSystem(Client(new StubErp()), new StubModes()).GetMapping(eventTypeId);

        Assert.IsNotNull(mapping, eventTypeId);
        Assert.IsNotEmpty(mapping.Fields, eventTypeId);
    }

    [TestMethod]
    public void Mapped_paths_are_properties_of_the_event()
    {
        var system = new ErpAdapterSystem(Client(new StubErp()), new StubModes());
        foreach (var type in new[] { typeof(CrmAccountCreated), typeof(CrmAccountUpdated), typeof(CrmAccountDeleted), typeof(CrmContactCreated), typeof(CrmContactUpdated), typeof(CrmContactDeleted) })
        {
            foreach (var field in system.GetMapping(type.Name)!.Fields)
                Assert.IsNotNull(type.GetProperty(field.EventPath), $"{type.Name} has no {field.EventPath}");
        }
    }

    [TestMethod]
    public void An_event_type_the_adapter_does_not_handle_has_no_mapping()
    {
        Assert.IsNull(new ErpAdapterSystem(Client(new StubErp()), new StubModes()).GetMapping("ErpCustomerCreated"));
    }

    [TestMethod]
    public async Task ERP_is_healthy_when_it_answers_and_is_not_in_service_mode()
    {
        var health = await new ErpAdapterSystem(Client(new StubErp()), new StubModes()).GetHealthAsync(CancellationToken.None);

        Assert.IsTrue(health.Reachable);
        Assert.IsNotNull(health.Message);
    }

    [TestMethod]
    public async Task ERP_in_service_mode_is_unreachable_and_says_so()
    {
        var modes = new StubModes { Probe = new ErpModeProbe(Reachable: true, ServiceMode: true, Problem: null) };

        var health = await new ErpAdapterSystem(Client(new StubErp()), modes).GetHealthAsync(CancellationToken.None);

        Assert.IsFalse(health.Reachable);
        StringAssert.Contains(health.Message, "service mode", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task ERP_that_does_not_answer_is_unreachable_and_says_so()
    {
        var modes = new StubModes { Probe = new ErpModeProbe(Reachable: false, ServiceMode: false, Problem: "erp-api did not answer (HttpRequestException)") };

        var health = await new ErpAdapterSystem(Client(new StubErp()), modes).GetHealthAsync(CancellationToken.None);

        Assert.IsFalse(health.Reachable);
        StringAssert.Contains(health.Message, "did not answer", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task The_service_mode_probe_tells_an_unreachable_ERP_from_one_that_answers()
    {
        using var down = new HttpClient(new StubErp { Throw = true }) { BaseAddress = new Uri("http://erp-api") };
        var unreachable = await new ErpAdapter::Erp.Adapter.Functions.Clients.ServiceModeClient(down, NullLogger<ErpAdapter::Erp.Adapter.Functions.Clients.ServiceModeClient>.Instance)
            .ProbeAsync(CancellationToken.None);
        using var up = new HttpClient(new StubErp { ServiceMode = true }) { BaseAddress = new Uri("http://erp-api") };
        var inServiceMode = await new ErpAdapter::Erp.Adapter.Functions.Clients.ServiceModeClient(up, NullLogger<ErpAdapter::Erp.Adapter.Functions.Clients.ServiceModeClient>.Instance)
            .ProbeAsync(CancellationToken.None);

        Assert.IsFalse(unreachable.Reachable);
        StringAssert.Contains(unreachable.Problem, "did not answer", StringComparison.Ordinal);
        Assert.IsTrue(inServiceMode.Reachable);
        Assert.IsTrue(inServiceMode.ServiceMode);
    }

    // --- The call log -------------------------------------------------------------------------

    [TestMethod]
    [DataRow("/api/customers/by-crm/" + AccountId, AccountId)]
    [DataRow("/api/customers/by-crm/6F1C0A5E-4B2D-4E8A-9C3F-1A2B3C4D5E01", AccountId)]
    [DataRow("/api/customers/by-crm/" + AccountId + "/deleted", AccountId)]
    [DataRow("/api/customers/credit-hold/by-crm/" + AccountId, AccountId)]
    [DataRow("/api/customers/by-crm/not-a-guid", null)]
    [DataRow("/api/customers/by-crm/", null)]
    [DataRow("/api/contacts/upsert/" + AccountId, null)]
    [DataRow("/api/admin/service-mode", null)]
    public void The_call_log_key_is_the_guid_after_by_crm(string path, string? expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri("https://erp-api"), path));

        Assert.AreEqual(expected, ErpAdapterMcp.CustomerKey(request));
    }

    [TestMethod]
    public async Task The_ERP_client_records_its_calls_per_customer()
    {
        var services = new ServiceCollection();
        services.AddErpApiClientForTest(new StubErp { Status = HttpStatusCode.UnprocessableEntity, Reason = "Unprocessable Entity" });
        using var provider = services.BuildServiceProvider();
        var erp = provider.GetRequiredService<IErpApiClient>();

        await Assert.ThrowsAsync<Exception>(() => erp.UpsertCustomerAsync(
            Guid.Parse(AccountId),
            new ErpAdapter::Erp.Adapter.Functions.Clients.CustomerUpsertPayload(null, "Contoso", null, "Denmark"),
            CancellationToken.None));

        Assert.IsTrue(provider.GetRequiredService<AdapterCallLog>().TryGet("customer", AccountId, out var call));
        Assert.AreEqual(422, call.StatusCode);
        Assert.AreEqual(AdapterCallOutcome.Failed, call.Outcome);
        Assert.AreEqual("Unprocessable Entity", call.Message);
    }

    // --- Version ----------------------------------------------------------------------------

    [TestMethod]
    public void The_adapter_build_carries_its_commit_for_adapter_get_health()
    {
        // In the Functions worker the adapter is the entry assembly; adapter_get_health reports
        // the part of its informational version after '+'.
        var informational = typeof(ErpAdapterSystem).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;

        StringAssert.Matches(informational, new System.Text.RegularExpressions.Regex(@"\+[0-9a-f]{40}$"), informational);
    }

    // --- Registration ------------------------------------------------------------------------

    [TestMethod]
    public void Without_a_tenant_and_audience_the_server_is_not_registered()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AdapterMcp:TenantId"] = Tokens.TenantId }).Build();

        Assert.IsFalse(services.AddErpAdapterMcpServer(configuration));
        Assert.IsFalse(services.Any(s => s.ServiceType == typeof(AdapterMcpHandler)));
    }

    // --- The HTTP trigger, through the official MCP client ----------------------------------------

    [TestMethod]
    public async Task The_trigger_serves_the_ERP_adapter_tools_to_an_MCP_client()
    {
        var erp = new StubErp();
        await using var host = await TriggerHost.StartAsync(erp, configured: true);
        await using var client = await host.ConnectAsync();

        var tools = await client.ListToolsAsync();
        CollectionAssert.AreEquivalent(
            new[] { "adapter_get_record", "adapter_get_mapping", "adapter_get_last_call", "adapter_get_health" },
            tools.Select(t => t.Name).ToArray());

        var record = Structured(await client.CallToolAsync("adapter_get_record", new Dictionary<string, object?> { ["entity"] = "customer", ["key"] = AccountId }));
        Assert.IsTrue(record.GetProperty("found").GetBoolean());
        Assert.AreEqual("Contoso Nordics ApS", record.GetProperty("record").GetProperty("legalName").GetString());
        Assert.AreEqual("******5678", record.GetProperty("record").GetProperty("taxId").GetString());

        var last = Structured(await client.CallToolAsync("adapter_get_last_call", new Dictionary<string, object?> { ["entity"] = "customer", ["key"] = AccountId }));
        Assert.IsTrue(last.GetProperty("found").GetBoolean());
        Assert.AreEqual(200, last.GetProperty("statusCode").GetInt32());
        Assert.AreEqual("succeeded", last.GetProperty("outcome").GetString());

        var mapping = Structured(await client.CallToolAsync("adapter_get_mapping", new Dictionary<string, object?> { ["eventTypeId"] = "CrmAccountCreated" }));
        Assert.IsTrue(mapping.GetProperty("known").GetBoolean());

        var health = Structured(await client.CallToolAsync("adapter_get_health", new Dictionary<string, object?>()));
        Assert.IsTrue(health.GetProperty("reachable").GetBoolean());
        Assert.AreEqual(JsonValueKind.String, health.GetProperty("checkedAt").ValueKind);
    }

    [TestMethod]
    public async Task The_trigger_refuses_a_caller_without_a_token()
    {
        await using var host = await TriggerHost.StartAsync(new StubErp(), configured: true);

        using var response = await host.PostAsync(token: null);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task The_trigger_answers_404_when_the_server_is_not_configured()
    {
        await using var host = await TriggerHost.StartAsync(new StubErp(), configured: false);

        using var response = await host.PostAsync(Tokens.Create());

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static JsonElement Structured(CallToolResult result)
    {
        Assert.AreNotEqual(true, result.IsError, result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
        return JsonSerializer.SerializeToElement(result.StructuredContent);
    }

    private static ErpApiClient Client(StubErp erp) => new(new HttpClient(erp) { BaseAddress = new Uri("http://erp-api") });

    /// <summary>The ERP API as the adapter sees it: one customer, the service-mode flag.</summary>
    internal sealed class StubErp : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public string? Reason { get; init; }

        public bool ServiceMode { get; init; }

        public bool Throw { get; init; }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Throw)
                throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (erp-api:443)");

            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/api/admin/", StringComparison.Ordinal))
                return Task.FromResult(Json($$"""{"enabled":{{(ServiceMode && path.EndsWith("service-mode", StringComparison.Ordinal) ? "true" : "false")}},"changedAt":"2026-11-25T09:00:00+00:00"}"""));

            var response = Status == HttpStatusCode.OK ? Json(CustomerJson) : new HttpResponseMessage(Status) { Content = new StringContent("{}") };
            if (Reason is not null)
                response.ReasonPhrase = Reason;
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    internal sealed class StubModes : IServiceModeClient
    {
        public ErpModeProbe Probe { get; init; } = new(Reachable: true, ServiceMode: false, Problem: null);

        public Task<bool> IsServiceModeEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(Probe.ServiceMode);

        public Task<ErrorModeSnapshot> GetErrorModeAsync(CancellationToken cancellationToken) => Task.FromResult(ErrorModeSnapshot.Disabled);

        public Task<ErpModeProbe> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(Probe);
    }

    /// <summary>Locally signed Entra-shaped tokens for the adapter resource, and the metadata that trusts them.</summary>
    internal static class Tokens
    {
        public const string TenantId = "0d3aa8f9-0000-0000-0000-000000000001";
        public const string Audience = "d668afb2-0000-0000-0000-000000000001";

        private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "test" };

        public static string Issuer => $"https://login.microsoftonline.com/{TenantId}/v2.0";

        public static Dictionary<string, string?> Settings() => new()
        {
            ["AdapterMcp:TenantId"] = TenantId,
            ["AdapterMcp:Audience"] = Audience,
        };

        public static AdapterTokenValidator Validator()
        {
            var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
            metadata.SigningKeys.Add(Key);
            return new AdapterTokenValidator(
                new AdapterMcpOptions { TenantId = TenantId, Audience = Audience },
                new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata));
        }

        public static string Create() => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Claims = new Dictionary<string, object>
            {
                ["oid"] = "33333333-0000-0000-0000-000000000001",
                ["azp"] = "44444444-0000-0000-0000-000000000001",
                ["roles"] = new[] { "Adapter.Read" },
            },
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>
    /// The adapter's registration on a TestServer whose only request handler is the HTTP trigger
    /// class, called as the Functions worker calls it: with the request, no routing in between.
    /// </summary>
    private sealed class TriggerHost : IAsyncDisposable
    {
        private readonly IHost _host;

        private TriggerHost(IHost host) => _host = host;

        private TestServer Server => _host.GetTestServer();

        public static async Task<TriggerHost> StartAsync(StubErp erp, bool configured)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configured ? Tokens.Settings() : [])
                .Build();
            var host = await new HostBuilder()
                .ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices(services =>
                    {
                        services.AddLogging();
                        services.AddErpApiClientForTest(erp);
                        services.AddSingleton<IServiceModeClient>(new StubModes());
                        if (services.AddErpAdapterMcpServer(configuration))
                            services.Replace(ServiceDescriptor.Singleton(Tokens.Validator()));
                    });
                    web.Configure(app => app.Run(async context =>
                    {
                        var function = new AdapterMcpFunction(context.RequestServices);
                        var result = await function.Run(context.Request);
                        await result.ExecuteResultAsync(new Microsoft.AspNetCore.Mvc.ActionContext(
                            context, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
                    }));
                })
                .StartAsync();
            return new TriggerHost(host);
        }

        public async Task<McpClient> ConnectAsync()
        {
            var http = Server.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Tokens.Create());
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions { Endpoint = new Uri(Server.BaseAddress, "/api/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
                http,
                NullLoggerFactory.Instance,
                ownsHttpClient: true);
            return await McpClient.CreateAsync(transport);
        }

        public async Task<HttpResponseMessage> PostAsync(string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/mcp")
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            if (token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var client = Server.CreateClient();
            return await client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}

internal static class ErpAdapterMcpTestServices
{
    /// <summary>The adapter's own ERP client registration, over a stub ERP.</summary>
    public static void AddErpApiClientForTest(this IServiceCollection services, ErpAdapterMcpTests.StubErp erp)
        => ErpAdapterMcp.AddErpApiClient(services, "http://erp-api").ConfigurePrimaryHttpMessageHandler(() => erp);

    public static bool AddErpAdapterMcpServer(this IServiceCollection services, IConfiguration configuration)
        => ErpAdapterMcp.AddServer(services, configuration);
}
