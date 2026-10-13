using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NimBus.WebApp.Mcp.Operations;

namespace NimBus.WebApp.Mcp.Access;

/// <summary>
/// Hides the tools the MCP access policy switches off from <c>tools/list</c> and refuses direct
/// calls to them (Spec 037). The read tools are never hidden.
/// </summary>
public static class McpToolAccess
{
    /// <summary>The tools that change a message, and the capability each needs.</summary>
    public static readonly IReadOnlyDictionary<string, OperatorAction> ChangeTools = new Dictionary<string, OperatorAction>(StringComparer.Ordinal)
    {
        ["nimbus_resubmit_message"] = OperatorAction.Resubmit,
        ["nimbus_skip_message"] = OperatorAction.Skip,
        ["nimbus_set_message_reported"] = OperatorAction.Report,
        ["nimbus_classify_failure"] = OperatorAction.Classify,
    };

    /// <summary>Previews resubmit or skip; listed while either is allowed.</summary>
    public const string PrepareTool = "nimbus_prepare_action";

    /// <summary>Whether <paramref name="tool"/> is available to <paramref name="caller"/> under <paramref name="policy"/>.</summary>
    public static bool IsAvailable(string tool, McpAccessPolicy policy, McpCaller caller)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(caller);

        if (string.Equals(tool, PrepareTool, StringComparison.Ordinal))
        {
            return policy.ClientMayChange(caller)
                && (policy.AllowsAction(OperatorAction.Resubmit) || policy.AllowsAction(OperatorAction.Skip));
        }

        return !ChangeTools.TryGetValue(tool, out var action)
            || (policy.ClientMayChange(caller) && policy.AllowsAction(action));
    }

    /// <summary>The <c>tools/list</c> filter.</summary>
    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> FilterList(McpRequestHandler<ListToolsRequestParams, ListToolsResult> next)
        => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken).ConfigureAwait(false);
            var (policy, caller, _) = Resolve(request.Services);
            if (policy is null)
            {
                result.Tools = [];
                return result;
            }

            result.Tools = result.Tools.Where(tool => IsAvailable(tool.Name, policy, caller)).ToList();
            return result;
        };

    /// <summary>The <c>tools/call</c> filter.</summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> FilterCall(McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => async (request, cancellationToken) =>
        {
            var tool = request.Params?.Name ?? string.Empty;
            var (policy, caller, http) = Resolve(request.Services);
            if (policy is not null && IsAvailable(tool, policy, caller))
                return await next(request, cancellationToken).ConfigureAwait(false);

            if (http is not null && policy is not null)
            {
                var refusals = http.RequestServices.GetRequiredService<McpAccessRefusals>();
                await refusals.RecordAsync(http, caller, McpRefusalReason.Tool, tool).ConfigureAwait(false);
            }

            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"[PermissionDenied] {tool} is turned off by an administrator." }],
            };
        };

    /// <summary>The live policy, the caller and the HTTP context of an MCP request.</summary>
    internal static (McpAccessPolicy? Policy, McpCaller Caller, HttpContext? Http) Resolve(IServiceProvider? services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var http = services.GetService<IHttpContextAccessor>()?.HttpContext;
        var runtime = services.GetRequiredService<McpOperatorRuntime>();
        var policy = services.GetRequiredService<IMcpAccessPolicyProvider>().Current;
        return (policy, McpCaller.From(http?.User, runtime.Mode), http);
    }
}
