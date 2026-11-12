using System.Net.Http.Json;
using System.Text.Json;

/// <summary>
/// Whether the recurring leads are on. With a crm-api address it follows the "Partner leads"
/// switch in crm-web, and keeps the last answer when crm-api cannot be reached; without one it
/// stays at the startup setting (<c>PARTNER_PUBLISH_LEADS</c>). Plain HTTP, no NimBus.
/// </summary>
internal sealed class PartnerLeadSwitch(HttpClient? crmApi, bool enabledAtStart)
{
    private const string FlagPath = "/api/admin/partner-leads";
    private const string ServiceKeyHeader = "X-Demo-Service-Key";

    private bool _enabled = enabledAtStart;

    /// <summary>True when crm-web's switch controls the leads.</summary>
    internal bool IsRemote => crmApi is not null;

    internal async Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        if (crmApi is null)
        {
            return _enabled;
        }

        try
        {
            var state = await crmApi.GetFromJsonAsync<SwitchState>(FlagPath, cancellationToken);
            if (state is not null)
            {
                _enabled = state.Enabled;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // crm-api is down, still starting, or refused the call: keep the last known answer.
        }

        return _enabled;
    }

    /// <summary>
    /// The client for crm-api's flag, or null when no address is configured. In Azure crm-api
    /// requires sign-in, so the demo service key rides along when there is one.
    /// </summary>
    internal static HttpClient? CreateClient(string? crmApiUrl, string? serviceKey)
    {
        if (string.IsNullOrWhiteSpace(crmApiUrl))
        {
            return null;
        }

        var client = new HttpClient { BaseAddress = new Uri(crmApiUrl), Timeout = TimeSpan.FromSeconds(5) };
        if (!string.IsNullOrEmpty(serviceKey))
        {
            client.DefaultRequestHeaders.Add(ServiceKeyHeader, serviceKey);
        }

        return client;
    }

    private sealed record SwitchState(bool Enabled);
}
