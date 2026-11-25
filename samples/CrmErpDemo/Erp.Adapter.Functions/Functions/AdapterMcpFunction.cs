using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using NimBus.Adapter.Mcp;

namespace Erp.Adapter.Functions.Functions;

/// <summary>
/// The ERP adapter's read-only MCP server (Spec 051 §7) at <c>/api/mcp</c>. Anonymous at the
/// Functions level: <see cref="AdapterMcpHandler"/> validates the caller's Entra token itself.
/// </summary>
/// <remarks>
/// With ASP.NET Core integration the trigger gets the worker's own <see cref="HttpContext"/>, and
/// the handler writes the answer straight to its response: the MCP SDK frames each POST's answer
/// as one server-sent event and completes it. <see cref="EmptyResult"/> then leaves that response
/// alone. Without an adapter registration (<c>AdapterMcp:TenantId</c> and
/// <c>AdapterMcp:Audience</c>) the server is not registered and the trigger answers 404.
/// </remarks>
public sealed class AdapterMcpFunction(IServiceProvider services)
{
    [Function("AdapterMcp")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", "delete", Route = "mcp")] HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var handler = services.GetService<AdapterMcpHandler>();
        if (handler is null)
            return new NotFoundResult();

        await handler.HandleAsync(request.HttpContext);
        return new EmptyResult();
    }
}
