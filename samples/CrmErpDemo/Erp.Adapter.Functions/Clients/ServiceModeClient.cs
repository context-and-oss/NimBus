using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Erp.Adapter.Functions.Clients;

public sealed class ServiceModeClient(HttpClient http, ILogger<ServiceModeClient> logger) : IServiceModeClient
{
    public async Task<bool> IsServiceModeEnabledAsync(CancellationToken cancellationToken)
    {
        var mode = await ReadAsync("/api/admin/service-mode", "service mode", cancellationToken);
        return mode?.Enabled ?? false;
    }

    public async Task<ErrorModeSnapshot> GetErrorModeAsync(CancellationToken cancellationToken)
    {
        var mode = await ReadAsync("/api/admin/error-mode", "error mode", cancellationToken);
        // An erp-api from before the failure rate sends none: it meant every message.
        return mode is null ? ErrorModeSnapshot.Disabled : new ErrorModeSnapshot(mode.Enabled, mode.Reason, mode.FailureRate ?? 1.0);
    }

    public async Task<ErpModeProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var mode = await http.GetFromJsonAsync<ModeResponse>("/api/admin/service-mode", cancellationToken);
            return new ErpModeProbe(Reachable: true, ServiceMode: mode?.Enabled ?? false, Problem: null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } status)
        {
            return new ErpModeProbe(Reachable: false, ServiceMode: false, Problem: $"erp-api answered {(int)status}");
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            && !cancellationToken.IsCancellationRequested)
        {
            // The kind only: the message can name hosts and ports.
            return new ErpModeProbe(Reachable: false, ServiceMode: false, Problem: $"erp-api did not answer ({ex.GetType().Name})");
        }
    }

    private async Task<ModeResponse?> ReadAsync(string path, string modeName, CancellationToken cancellationToken)
    {
        try
        {
            return await http.GetFromJsonAsync<ModeResponse>(path, cancellationToken);
        }
        catch (Exception ex)
        {
            // If the flag can't be read (e.g. erp-api unreachable), default to off — the
            // downstream call will fail on its own and surface the real cause.
            logger.LogDebug(ex, "Could not read {ModeName} flag — assuming disabled.", modeName);
            return null;
        }
    }

    // Reason and FailureRate are only present on the error-mode response; service-mode leaves them null.
    private sealed record ModeResponse(bool Enabled, string? Reason, double? FailureRate, DateTimeOffset ChangedAt);
}
