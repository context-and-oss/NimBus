using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NimBus.WebApp.Mcp.Prompts;

namespace NimBus.WebApp.Mcp.Access;

/// <summary>
/// Hides the prompts that lead to a tool the MCP access policy makes unavailable from
/// <c>prompts/list</c>, and refuses <c>prompts/get</c> for them (Spec 040). Prompts have no switch
/// of their own: <c>recover-message</c> follows <c>nimbus_prepare_action</c>.
/// </summary>
public static class McpPromptAccess
{
    /// <summary>Whether <paramref name="prompt"/> is available to <paramref name="caller"/> under <paramref name="policy"/>.</summary>
    public static bool IsAvailable(string prompt, McpAccessPolicy policy, McpCaller caller)
        => !string.Equals(prompt, OperatorPrompts.RecoverMessage, StringComparison.Ordinal)
            || McpToolAccess.IsAvailable(McpToolAccess.PrepareTool, policy, caller);

    /// <summary>The <c>prompts/list</c> filter.</summary>
    public static McpRequestHandler<ListPromptsRequestParams, ListPromptsResult> FilterList(McpRequestHandler<ListPromptsRequestParams, ListPromptsResult> next)
        => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken).ConfigureAwait(false);
            var (policy, caller, _) = McpToolAccess.Resolve(request.Services);
            result.Prompts = policy is null
                ? []
                : result.Prompts.Where(prompt => IsAvailable(prompt.Name, policy, caller)).ToList();
            return result;
        };

    /// <summary>The <c>prompts/get</c> filter.</summary>
    public static McpRequestHandler<GetPromptRequestParams, GetPromptResult> FilterGet(McpRequestHandler<GetPromptRequestParams, GetPromptResult> next)
        => async (request, cancellationToken) =>
        {
            var prompt = request.Params?.Name ?? string.Empty;
            var (policy, caller, http) = McpToolAccess.Resolve(request.Services);
            if (policy is not null && IsAvailable(prompt, policy, caller))
                return await next(request, cancellationToken).ConfigureAwait(false);

            if (http is not null && policy is not null)
            {
                var refusals = http.RequestServices.GetRequiredService<McpAccessRefusals>();
                await refusals.RecordAsync(http, caller, McpRefusalReason.Tool, prompt).ConfigureAwait(false);
            }

            throw new McpProtocolException($"[PermissionDenied] {prompt} is turned off by an administrator.", McpErrorCode.InvalidParams);
        };
}
