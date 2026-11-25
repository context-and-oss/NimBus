using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace NimBus.Adapter.Mcp;

/// <summary>
/// The adapter MCP endpoint: one path for ASP.NET Core (<c>MapAdapterMcp</c>) and for an Azure
/// Functions HttpTrigger with ASP.NET Core integration, which hands over the request's
/// <see cref="HttpContext"/>. It signs the caller in with <see cref="AdapterTokenValidator"/> and
/// then runs the request through the MCP SDK's stateless Streamable HTTP server.
/// </summary>
/// <remarks>
/// POST carries JSON-RPC. There are no sessions and no server-to-client stream, so GET and DELETE
/// answer 405. A missing or invalid token answers 401 with a <c>Bearer</c> challenge, a valid token
/// without the read role 403. Resolve it from the container; <c>AddAdapterMcp</c> registers it.
/// </remarks>
public sealed class AdapterMcpHandler
{
    private readonly AdapterTokenValidator _validator;
    private readonly SdkStreamableHttp _sdk;

    internal AdapterMcpHandler(AdapterTokenValidator validator, SdkStreamableHttp sdk)
    {
        _validator = validator;
        _sdk = sdk;
    }

    /// <summary>Serves one request to the adapter MCP endpoint.</summary>
    /// <param name="context">The request, from ASP.NET Core or a Functions HttpTrigger.</param>
    public async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = HttpMethods.Post;
            return;
        }

        var token = await _validator.ValidateAsync(context.Request.Headers.Authorization.ToString(), context.RequestAborted).ConfigureAwait(false);
        switch (token.Status)
        {
            case AdapterTokenStatus.Missing:
                Refuse(context, StatusCodes.Status401Unauthorized, "Bearer");
                return;
            case AdapterTokenStatus.Invalid:
                Refuse(context, StatusCodes.Status401Unauthorized, "Bearer error=\"invalid_token\"");
                return;
            case AdapterTokenStatus.Forbidden:
                Refuse(context, StatusCodes.Status403Forbidden, "Bearer error=\"insufficient_scope\"");
                return;
        }

        // The SDK hands the request's user to the tools, which name the caller in the read log.
        context.User = token.Principal!;
        await _sdk.PostAsync(context).ConfigureAwait(false);
    }

    private static void Refuse(HttpContext context, int status, string challenge)
    {
        context.Response.StatusCode = status;
        context.Response.Headers[HeaderNames.WWWAuthenticate] = challenge;
    }
}
