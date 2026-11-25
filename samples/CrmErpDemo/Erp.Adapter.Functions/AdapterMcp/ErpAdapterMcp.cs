using Erp.Adapter.Functions.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NimBus.Adapter.Mcp;

namespace Erp.Adapter.Functions.AdapterMcp;

/// <summary>
/// Wires the ERP adapter's MCP server (Spec 051 §7, <c>docs/adapter-mcp.md</c>): the ERP client
/// with its call log, and the server itself when the deployment gives it an Entra tenant and
/// audience. Served by <see cref="Functions.AdapterMcpFunction"/> at <c>/api/mcp</c>.
/// </summary>
public static class ErpAdapterMcp
{
    private const string ByCrm = "by-crm";

    /// <summary>
    /// Registers the ERP API client, recording each call that names a customer in the
    /// <see cref="AdapterCallLog"/>, so <c>adapter_get_last_call</c> can tell what ERP answered.
    /// </summary>
    /// <param name="services">The adapter's services.</param>
    /// <param name="erpApiBaseUrl">erp-api's base URL.</param>
    public static IHttpClientBuilder AddErpApiClient(IServiceCollection services, string erpApiBaseUrl)
        => services
            .AddHttpClient<IErpApiClient, ErpApiClient>(c => c.BaseAddress = new Uri(erpApiBaseUrl))
            .AddAdapterCallLog(ErpAdapterSystem.Customer, CustomerKey);

    /// <summary>
    /// Registers the MCP server when <c>AdapterMcp:TenantId</c> and <c>AdapterMcp:Audience</c> are
    /// both set, and returns whether it did. Without them the trigger answers 404: a local run and a
    /// deployment without an adapter registration serve no MCP server and still start.
    /// </summary>
    /// <param name="services">The adapter's services.</param>
    /// <param name="configuration">The adapter's configuration.</param>
    public static bool AddServer(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(AdapterMcpOptions.SectionName);
        if (string.IsNullOrWhiteSpace(section[nameof(AdapterMcpOptions.TenantId)])
            || string.IsNullOrWhiteSpace(section[nameof(AdapterMcpOptions.Audience)]))
        {
            return false;
        }

        services.AddAdapterMcp<ErpAdapterSystem>(configuration);
        return true;
    }

    /// <summary>
    /// The customer a request to erp-api is about: the CRM account id after <c>by-crm/</c>, as in
    /// <c>/api/customers/by-crm/{id}</c>, <c>…/by-crm/{id}/deleted</c> and
    /// <c>/api/customers/credit-hold/by-crm/{id}</c>, in the lower-case form the CRM events carry.
    /// Null for any other request.
    /// </summary>
    /// <param name="request">The outgoing request.</param>
    public static string? CustomerKey(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var segments = request.RequestUri?.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var at = Array.IndexOf(segments, ByCrm);
        return at >= 0 && at + 1 < segments.Length && Guid.TryParse(segments[at + 1], out var crmAccountId)
            ? crmAccountId.ToString("D")
            : null;
    }
}
