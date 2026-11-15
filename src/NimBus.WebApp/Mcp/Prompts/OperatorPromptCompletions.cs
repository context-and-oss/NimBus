using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NimBus.WebApp.Mcp.Operations;

namespace NimBus.WebApp.Mcp.Prompts;

/// <summary>
/// Completions for the operator prompts' arguments (Spec 046 §5): the fixed values each prompt
/// validates against, and the endpoints the caller can read. Event ids are not completed. A
/// completion reads the platform model and the caller's roles, never the message store, and is
/// not audited; the prompt access filter answers nothing for a prompt the caller can't list.
/// </summary>
public static class OperatorPromptCompletions
{
    /// <summary>Most values in one completion.</summary>
    public const int MaxValues = 100;

    private static readonly Dictionary<string, string[]> ArgumentsByPrompt = new(StringComparer.Ordinal)
    {
        ["triage"] = ["endpointId", "period"],
        ["investigate-message"] = ["endpointId", "eventId"],
        [OperatorPrompts.RecoverMessage] = ["endpointId", "eventId", "action"],
        ["failure-report"] = ["period"],
        [OperatorPrompts.HandleFailures] = ["endpointId", "period", "unattended"],
    };

    private static readonly string[] Flags = ["true", "false"];

    /// <summary>The <c>completion/complete</c> handler.</summary>
    public static async ValueTask<CompleteResult> HandleAsync(RequestContext<CompleteRequestParams> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Params?.Ref is not PromptReference prompt
            || !ArgumentsByPrompt.TryGetValue(prompt.Name, out var arguments)
            || request.Params.Argument is not { } argument
            || !arguments.Contains(argument.Name, StringComparer.Ordinal))
        {
            return Empty();
        }

        var typed = argument.Value ?? string.Empty;
        IEnumerable<string> candidates = argument.Name switch
        {
            "period" => OperatorPrompts.Periods,
            "action" => OperatorPrompts.Actions,
            "unattended" => Flags,
            "endpointId" when request.Services?.GetService(typeof(OperatorEndpointCatalog)) is OperatorEndpointCatalog catalog
                => await catalog.GetReadableEndpointIdsAsync().ConfigureAwait(false),
            _ => [],
        };

        var matches = candidates.Where(value => value.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToList();
        return new CompleteResult
        {
            Completion = new Completion
            {
                Values = matches.Take(MaxValues).ToList(),
                Total = matches.Count,
                HasMore = matches.Count > MaxValues,
            },
        };
    }

    /// <summary>A completion with no values.</summary>
    public static CompleteResult Empty() => new() { Completion = new Completion { Values = [], Total = 0, HasMore = false } };
}
