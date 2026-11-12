using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace NimBus.WebApp.Problems;

/// <summary>
/// Builds and writes the RFC 7807 problem responses every <c>/api</c> error carries, so callers
/// can branch on one schema: <c>type</c>, <c>title</c>, <c>status</c>, <c>detail</c>,
/// <c>traceId</c>, and a <c>code</c> or <c>errors</c> member where the operation has one.
/// </summary>
public static class ApiProblems
{
    /// <summary>The media type of a problem response.</summary>
    public const string ContentType = "application/problem+json";

    /// <summary>The <c>code</c> of an antiforgery refusal; the SPA reloads its token on it.</summary>
    public const string InvalidAntiforgeryTokenCode = "InvalidAntiforgeryToken";

    /// <summary>True for the REST API surface, the only paths whose errors become problems.</summary>
    public static bool IsApiPath(PathString path) => path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A problem for <paramref name="status"/>: the reason phrase as title, the RFC 9110 section
    /// as type and the request's trace id, plus <paramref name="detail"/> when there is one.
    /// </summary>
    public static ProblemDetails Create(HttpContext context, int status, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var problem = new ProblemDetails
        {
            Status = status,
            Title = ReasonPhrases.GetReasonPhrase(status) is { Length: > 0 } reason ? reason : "Error",
            Type = TypeFor(status),
            Detail = string.IsNullOrWhiteSpace(detail) ? null : detail,
        };
        AddTraceId(context, problem);
        return problem;
    }

    /// <summary>Adds the request's trace id to <paramref name="problem"/> unless it has one.</summary>
    public static void AddTraceId(HttpContext context, ProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(problem);
        if (!problem.Extensions.ContainsKey("traceId"))
            problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
    }

    /// <summary>Writes <paramref name="problem"/> as the response, with the problem media type.</summary>
    public static Task WriteAsync(HttpContext context, ProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(problem);
        context.Response.StatusCode = problem.Status ?? context.Response.StatusCode;
        return context.Response.WriteAsJsonAsync(problem, options: null, contentType: ContentType, context.RequestAborted);
    }

    /// <summary>The RFC 9110 section that defines <paramref name="status"/>, or null for one it doesn't.</summary>
    public static string? TypeFor(int status) => status switch
    {
        400 => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        401 => "https://tools.ietf.org/html/rfc9110#section-15.5.2",
        403 => "https://tools.ietf.org/html/rfc9110#section-15.5.4",
        404 => "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        405 => "https://tools.ietf.org/html/rfc9110#section-15.5.6",
        406 => "https://tools.ietf.org/html/rfc9110#section-15.5.7",
        409 => "https://tools.ietf.org/html/rfc9110#section-15.5.10",
        412 => "https://tools.ietf.org/html/rfc9110#section-15.5.13",
        413 => "https://tools.ietf.org/html/rfc9110#section-15.5.14",
        415 => "https://tools.ietf.org/html/rfc9110#section-15.5.16",
        422 => "https://tools.ietf.org/html/rfc9110#section-15.5.21",
        429 => "https://tools.ietf.org/html/rfc6585#section-4",
        500 => "https://tools.ietf.org/html/rfc9110#section-15.6.1",
        501 => "https://tools.ietf.org/html/rfc9110#section-15.6.2",
        502 => "https://tools.ietf.org/html/rfc9110#section-15.6.3",
        503 => "https://tools.ietf.org/html/rfc9110#section-15.6.4",
        504 => "https://tools.ietf.org/html/rfc9110#section-15.6.5",
        _ => null,
    };
}
