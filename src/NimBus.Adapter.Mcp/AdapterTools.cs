using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace NimBus.Adapter.Mcp;

/// <summary>
/// The four read-only adapter tools (Spec 051 §7). Their names, arguments and result shapes are a
/// contract other teams build clients against: change none of them.
/// </summary>
internal static class AdapterTools
{
    public const string GetRecord = "adapter_get_record";
    public const string GetMapping = "adapter_get_mapping";
    public const string GetLastCall = "adapter_get_last_call";
    public const string GetHealth = "adapter_get_health";

    /// <summary>camelCase, enums as camelCase strings, and every member written even when null.</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static IReadOnlyList<McpServerTool> Create() =>
    [
        new AdapterTool(
            GetRecord,
            "Get record",
            "The record a message is about, as it is now in the adapter's own system, by entity and key. Sensitive fields are masked.",
            EntityKeyInput,
            """
            {"type":"object","properties":{
              "entity":{"type":"string"},"key":{"type":"string"},"found":{"type":"boolean"},
              "record":{"type":["object","null"]}},
             "required":["entity","key","found","record"]}
            """,
            GetRecordAsync),
        new AdapterTool(
            GetMapping,
            "Get mapping",
            "How the adapter maps an event type to the system's fields, and the settings it runs with.",
            """
            {"type":"object","properties":{"eventTypeId":{"type":"string","description":"The NimBus event type id, the unqualified event class name."}},"required":["eventTypeId"]}
            """,
            """
            {"type":"object","properties":{
              "eventTypeId":{"type":"string"},"known":{"type":"boolean"},
              "fields":{"type":"array","items":{"type":"object","properties":{
                "eventPath":{"type":"string"},"systemField":{"type":"string"},"note":{"type":["string","null"]}},
                "required":["eventPath","systemField","note"]}},
              "settings":{"type":"object","additionalProperties":{"type":"string"}}},
             "required":["eventTypeId","known","fields","settings"]}
            """,
            GetMappingAsync),
        new AdapterTool(
            GetLastCall,
            "Get last call",
            "What the system answered to this adapter instance's most recent call for a key: status code, outcome and message. The log is per instance and in memory.",
            EntityKeyInput,
            """
            {"type":"object","properties":{
              "entity":{"type":"string"},"key":{"type":"string"},"found":{"type":"boolean"},
              "at":{"type":["string","null"],"format":"date-time"},
              "statusCode":{"type":["integer","null"]},
              "outcome":{"type":["string","null"],"enum":["succeeded","failed",null]},
              "message":{"type":["string","null"]}},
             "required":["entity","key","found","at","statusCode","outcome","message"]}
            """,
            GetLastCallAsync),
        new AdapterTool(
            GetHealth,
            "Get health",
            "Whether the system is reachable from the adapter, and the version the adapter's deployment runs: the commit it was built from.",
            """{"type":"object","properties":{}}""",
            """
            {"type":"object","properties":{
              "reachable":{"type":"boolean"},"message":{"type":["string","null"]},
              "version":{"type":["string","null"]},"checkedAt":{"type":"string","format":"date-time"}},
             "required":["reachable","message","version","checkedAt"]}
            """,
            GetHealthAsync),
    ];

    private const string EntityKeyInput = """
        {"type":"object","properties":{
          "entity":{"type":"string","description":"An entity the adapter serves, such as customer."},
          "key":{"type":"string","description":"The record's key in the system."}},
         "required":["entity","key"]}
        """;

    private static async Task<object> GetRecordAsync(AdapterToolCall call, CancellationToken cancellationToken)
    {
        var entity = call.Entity();
        var key = call.RequiredString("key");
        var record = await call.System.GetRecordAsync(entity, key, cancellationToken).ConfigureAwait(false);
        var masked = call.Services.GetRequiredService<AdapterRecordMasker>().Mask(record);
        return new RecordResult(entity, key, masked is not null, masked);
    }

    private static Task<object> GetMappingAsync(AdapterToolCall call, CancellationToken cancellationToken)
    {
        var eventTypeId = call.RequiredString("eventTypeId");
        var mapping = call.System.GetMapping(eventTypeId);
        object result = new MappingResult(
            eventTypeId,
            mapping is not null,
            mapping?.Fields.Select(field => new FieldResult(field.EventPath, field.SystemField, field.Note)).ToList() ?? [],
            mapping?.Settings ?? new Dictionary<string, string>());
        return Task.FromResult(result);
    }

    private static Task<object> GetLastCallAsync(AdapterToolCall call, CancellationToken cancellationToken)
    {
        var entity = call.Entity();
        var key = call.RequiredString("key");
        object result = call.Services.GetRequiredService<AdapterCallLog>().TryGet(entity, key, out var last)
            ? new LastCallResult(entity, key, true, last.At, last.StatusCode, last.Outcome, last.Message)
            : new LastCallResult(entity, key, false, null, null, null, null);
        return Task.FromResult(result);
    }

    private static async Task<object> GetHealthAsync(AdapterToolCall call, CancellationToken cancellationToken)
    {
        var health = await call.System.GetHealthAsync(cancellationToken).ConfigureAwait(false);
        var now = call.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        return new HealthResult(health.Reachable, health.Message, AdapterVersion.Current, now);
    }

    private sealed record RecordResult(string Entity, string Key, bool Found, JsonNode? Record);

    private sealed record MappingResult(string EventTypeId, bool Known, IReadOnlyList<FieldResult> Fields, IReadOnlyDictionary<string, string> Settings);

    private sealed record FieldResult(string EventPath, string SystemField, string? Note);

    private sealed record LastCallResult(string Entity, string Key, bool Found, DateTimeOffset? At, int? StatusCode, AdapterCallOutcome? Outcome, string? Message);

    private sealed record HealthResult(bool Reachable, string? Message, string? Version, DateTimeOffset CheckedAt);
}

/// <summary>A tool call's refusal, answered as <c>isError</c> with one text block <c>"&lt;code&gt;: &lt;message&gt;"</c>.</summary>
internal sealed class AdapterToolException(string code, string message) : Exception(message)
{
    public const string UnknownEntity = "unknown-entity";
    public const string InvalidArguments = "invalid-arguments";
    public const string SystemError = "system-error";

    public string Code { get; } = code;
}

/// <summary>One tool call's arguments and services.</summary>
internal sealed class AdapterToolCall(IServiceProvider services, IDictionary<string, JsonElement>? arguments)
{
    private IAdapterSystem? _system;

    public IServiceProvider Services { get; } = services;

    public IAdapterSystem System => _system ??= Services.GetRequiredService<IAdapterSystem>();

    /// <summary>The argument as given, for the read log; null when absent or not a string.</summary>
    public string? Raw(string name)
        => arguments is not null && arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public string RequiredString(string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value))
            throw new AdapterToolException(AdapterToolException.InvalidArguments, $"{name} is required.");
        if (value.ValueKind != JsonValueKind.String)
            throw new AdapterToolException(AdapterToolException.InvalidArguments, $"{name} must be a string.");
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
            throw new AdapterToolException(AdapterToolException.InvalidArguments, $"{name} must not be empty.");
        return text;
    }

    /// <summary>The served entity the caller named, as the adapter lists it; refused when not served.</summary>
    public string Entity()
    {
        var requested = RequiredString("entity");
        return System.Entities.FirstOrDefault(entity => string.Equals(entity, requested, StringComparison.OrdinalIgnoreCase))
            ?? throw new AdapterToolException(AdapterToolException.UnknownEntity, $"'{requested}' is not an entity this adapter serves.");
    }
}

/// <summary>A tool with a fixed protocol description, run by a delegate, logged and fenced.</summary>
internal sealed class AdapterTool : McpServerTool
{
    private readonly Func<AdapterToolCall, CancellationToken, Task<object>> _run;

    public AdapterTool(string name, string title, string description, string inputSchema, string outputSchema, Func<AdapterToolCall, CancellationToken, Task<object>> run)
    {
        _run = run;
        ProtocolTool = new Tool
        {
            Name = name,
            Title = title,
            Description = description,
            InputSchema = JsonElement.Parse(inputSchema),
            OutputSchema = JsonElement.Parse(outputSchema),
            Annotations = new ToolAnnotations
            {
                Title = title,
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false,
            },
        };
    }

    public override Tool ProtocolTool { get; }

    public override IReadOnlyList<object> Metadata => [];

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var services = request.Services ?? throw new InvalidOperationException("The MCP request carries no services.");
        var call = new AdapterToolCall(services, request.Params?.Arguments);
        var outcome = "succeeded";
        try
        {
            var result = JsonSerializer.SerializeToElement(await _run(call, cancellationToken).ConfigureAwait(false), AdapterTools.Json);
            return new CallToolResult
            {
                StructuredContent = result,
                Content = [new TextContentBlock { Text = result.GetRawText() }],
            };
        }
        catch (AdapterToolException refusal)
        {
            outcome = refusal.Code;
            return Error(refusal.Code, refusal.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The type only: an exception's message can carry connection strings, hosts or values.
            // The adapter's operator gets the exception in the host's own log.
            outcome = AdapterToolException.SystemError;
            services.GetRequiredService<AdapterReadLog>().Failed(ProtocolTool.Name, exception);
            return Error(AdapterToolException.SystemError, $"the adapter failed ({exception.GetType().Name}).");
        }
        finally
        {
            services.GetRequiredService<AdapterReadLog>().Read(request.User, ProtocolTool.Name, call.Raw("entity"), call.Raw("key"), outcome);
        }
    }

    private static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"{code}: {message}" }],
    };
}
