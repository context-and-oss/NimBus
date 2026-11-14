using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace NimBus.WebApp.Problems;

/// <summary>
/// Turns every error result an <c>/api</c> action returns into an RFC 7807 problem, so the
/// several hundred <c>NotFoundObjectResult("...")</c>-style returns across the implementations
/// share one schema without each being rewritten:
/// <list type="bullet">
/// <item>a string body becomes the problem's <c>detail</c>;</item>
/// <item>a list of strings becomes <c>detail</c> plus an <c>errors</c> member;</item>
/// <item>a contract DTO (the generated <c>ProblemDetails</c>, <c>SessionStateView</c>, an anonymous
/// <c>{ code }</c>) keeps its members at the top level of the problem, so a client that parses
/// the declared schema still finds them;</item>
/// <item>an antiforgery refusal becomes a 400 with <c>code: InvalidAntiforgeryToken</c>.</item>
/// </list>
/// Bodyless results (<c>ForbidResult</c>, a bare status code) are completed by
/// <see cref="ApiProblemDetailsMiddleware"/> after authentication has run.
/// It always runs, so results from short-circuiting authorization filters are covered too.
/// </summary>
internal sealed class ApiProblemDetailsResultFilter : IAlwaysRunResultFilter, IOrderedFilter
{
    private static readonly HashSet<string> StandardMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "title", "status", "detail", "instance",
    };

    private readonly IOptions<JsonOptions> _json;

    public ApiProblemDetailsResultFilter(IOptions<JsonOptions> json) => _json = json;

    /// <summary>Last, so it sees the result every other filter settled on.</summary>
    public int Order => int.MaxValue;

    public void OnResultExecuting(ResultExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var http = context.HttpContext;
        if (!ApiProblems.IsApiPath(http.Request.Path))
            return;

        if (context.Result is IAntiforgeryValidationFailedResult)
        {
            var refused = ApiProblems.Create(http, StatusCodes.Status400BadRequest,
                "The antiforgery token was missing or invalid. Reload the page and try again.");
            refused.Extensions["code"] = ApiProblems.InvalidAntiforgeryTokenCode;
            context.Result = ProblemResult(refused);
            return;
        }

        if (context.Result is not ObjectResult { StatusCode: >= 400 and var status } result)
            return;

        context.Result = ProblemResult(ToProblem(http, status, result.Value));
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }

    private ProblemDetails ToProblem(HttpContext http, int status, object? value)
    {
        switch (value)
        {
            case ProblemDetails existing:
                existing.Status ??= status;
                existing.Title ??= ApiProblems.Create(http, status).Title;
                existing.Type ??= ApiProblems.TypeFor(status);
                ApiProblems.AddTraceId(http, existing);
                return existing;
            case null:
                return ApiProblems.Create(http, status);
            case string text:
                return ApiProblems.Create(http, status, text);
            case IEnumerable<string> messages:
            {
                var errors = messages.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
                var problem = ApiProblems.Create(http, status, string.Join(" ", errors));
                problem.Extensions["errors"] = errors;
                return problem;
            }
            case IEnumerable:
                // A bare array has no member names to keep; carry it whole.
                var listProblem = ApiProblems.Create(http, status);
                listProblem.Extensions["errors"] = value;
                return listProblem;
            default:
                return Flatten(http, status, value);
        }
    }

    // A contract DTO's members move to the top level of the problem, next to the standard ones.
    private ProblemDetails Flatten(HttpContext http, int status, object value)
    {
        var problem = ApiProblems.Create(http, status);
        if (JsonSerializer.SerializeToNode(value, value.GetType(), _json.Value.JsonSerializerOptions) is not JsonObject members)
        {
            problem.Extensions["value"] = value;
            return problem;
        }

        foreach (var (name, member) in members)
        {
            if (member is null)
                continue;

            if (StandardMembers.Contains(name))
            {
                // A DTO that names its own title or detail keeps it; status always follows the response.
                if (member is JsonValue text && text.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                {
                    if (name.Equals("title", StringComparison.OrdinalIgnoreCase)) problem.Title = s;
                    else if (name.Equals("detail", StringComparison.OrdinalIgnoreCase)) problem.Detail = s;
                    else if (name.Equals("type", StringComparison.OrdinalIgnoreCase)) problem.Type = s;
                    else if (name.Equals("instance", StringComparison.OrdinalIgnoreCase)) problem.Instance = s;
                }
                continue;
            }

            problem.Extensions[name] = member;
        }

        if (problem.Detail is null && problem.Extensions.TryGetValue("errors", out var errors) && errors is JsonArray list)
        {
            var messages = list.OfType<JsonValue>()
                .Select(e => e.TryGetValue<string>(out var m) ? m : null)
                .Where(m => !string.IsNullOrWhiteSpace(m));
            var joined = string.Join(" ", messages);
            if (joined.Length > 0)
                problem.Detail = joined;
        }

        return problem;
    }

    private static ObjectResult ProblemResult(ProblemDetails problem)
    {
        var result = new ObjectResult(problem) { StatusCode = problem.Status };
        result.ContentTypes.Add(ApiProblems.ContentType);
        return result;
    }
}
