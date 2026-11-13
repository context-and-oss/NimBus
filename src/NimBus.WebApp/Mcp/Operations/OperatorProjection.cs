using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Mcp.Tools;

namespace NimBus.WebApp.Mcp.Operations;

/// <summary>Shared rules for turning REST results into tool results.</summary>
public static class OperatorProjection
{
    /// <summary>Longest error or log text returned; longer text is cut and flagged.</summary>
    public const int MaxErrorTextLength = 2000;

    /// <summary>
    /// Marks a stored timestamp as UTC. Stores return UTC values, SQL Server's with an
    /// unspecified kind; without this they serialize without a Z and read as local time.
    /// </summary>
    public static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => value,
    };

    /// <summary>Cuts <paramref name="text"/> to <paramref name="maxLength"/> characters.</summary>
    public static (string? Text, bool Truncated) Truncate(string? text, int maxLength = MaxErrorTextLength)
        => text is not null && text.Length > maxLength ? (text[..maxLength], true) : (text, false);

    /// <summary>The message's page in the NimBus Web UI, on the host the caller used; null outside a request.</summary>
    public static string? WebUiUrl(HttpRequest? request, string endpointId, string eventId)
        => request is null
            ? null
            : $"{request.Scheme}://{request.Host}{request.PathBase}/Message/Index/{Uri.EscapeDataString(endpointId)}/{Uri.EscapeDataString(eventId)}";

    /// <summary>A message's error as type and truncated text, never a stack trace; null when it has none.</summary>
    public static MessageError? Error(Message? message)
    {
        var error = message?.ErrorContent;
        if (error is null || (string.IsNullOrEmpty(error.ErrorText) && string.IsNullOrEmpty(error.ErrorType)))
            return null;

        var (text, truncated) = Truncate(error.ErrorText);
        return new MessageError(message!.MessageId, error.ErrorType, text, truncated);
    }
}
