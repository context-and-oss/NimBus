namespace NimBus.WebApp.Problems;

/// <summary>
/// Completes the <c>/api</c> errors that no action result describes, so a caller always gets a
/// problem body. The cases are:
/// <list type="bullet">
/// <item>a bodyless error status: <c>ForbidResult</c>, the bare 401/403 the cookie and OIDC
/// handlers return for API paths, a <c>StatusCodeResult</c>, an unmatched <c>/api</c> route;</item>
/// <item>an unhandled exception, which becomes a 500 that shows its message only in
/// Development.</item>
/// </list>
/// It runs ahead of authentication, so the challenge and forbid responses are covered.
/// </summary>
internal sealed partial class ApiProblemDetailsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ApiProblemDetailsMiddleware> _logger;

    public ApiProblemDetailsMiddleware(RequestDelegate next, IHostEnvironment environment, ILogger<ApiProblemDetailsMiddleware> logger)
    {
        _next = next;
        _environment = environment;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!ApiProblems.IsApiPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            LogUnhandled(exception, context.Request.Method, context.Request.Path);
            // Keep the CORS answer, so a cross-origin caller can still read the problem.
            var cors = context.Response.Headers
                .Where(h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Vary", StringComparison.OrdinalIgnoreCase))
                .ToList();
            context.Response.Clear();
            foreach (var (name, value) in cors)
                context.Response.Headers[name] = value;
            var problem = ApiProblems.Create(context, StatusCodes.Status500InternalServerError,
                _environment.IsDevelopment() ? exception.Message : "The server could not complete the request.");
            await ApiProblems.WriteAsync(context, problem).ConfigureAwait(false);
            return;
        }

        var response = context.Response;
        if (response.StatusCode < 400
            || response.HasStarted
            || response.ContentLength is not null
            || !string.IsNullOrEmpty(response.ContentType)
            || HttpMethods.IsHead(context.Request.Method)
            || context.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        await ApiProblems.WriteAsync(context, ApiProblems.Create(context, response.StatusCode)).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
    private partial void LogUnhandled(Exception exception, string method, PathString path);
}
