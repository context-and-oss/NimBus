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
