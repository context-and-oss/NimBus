using System.Text.Json.Serialization;

namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>
/// Versioned question sets. <see cref="Items"/> (version <see cref="Version"/>) is what agents
/// answer when they record a classification over MCP (Spec 044 §6.2). The deprecated provider
/// keeps asking <see cref="ProviderItems"/> (version <see cref="ProviderVersion"/>).
/// </summary>
public static class FailureClassificationQuestionSet
{
    /// <summary>Current question-set version, answered by agents.</summary>
    public const int Version = 2;

    /// <summary>The version the deprecated provider asks; it has no fault-origin question.</summary>
    public const int ProviderVersion = 1;

    private static readonly Question FailureCategory = new("choice", "Classify the primary cause of this failure.", new Dictionary<string, object?>
    {
        ["transient_dependency"] = new QuestionChoice("A temporary downstream or infrastructure condition that may clear without changing the message, configuration or code.", ["timeout", "connection reset", "HTTP 429", "temporary 5xx", "throttling", "deadlock", "DNS/network blip"]),
        ["authentication_configuration"] = new QuestionChoice("Credentials, permissions, endpoint URLs, certificates or environment settings are wrong or expired.", ["401/403", "expired secret", "wrong host", "missing setting"]),
        ["contract_schema"] = new QuestionChoice("The data does not satisfy the technical contract.", ["malformed JSON", "deserialization error", "missing required property", "wrong type", "unsupported version"]),
        ["business_rule"] = new QuestionChoice("Technically valid data was rejected by a domain rule.", ["invalid state transition", "credit limit", "closed account"]),
        ["missing_reference_data"] = new QuestionChoice("A referenced business record does not exist.", ["customer not found", "order not found", "missing master data"]),
        ["application_defect"] = new QuestionChoice("Evidence points at a programming error.", ["NullReferenceException", "IndexOutOfRangeException", "unexpected internal exception"]),
        ["messaging_platform"] = new QuestionChoice("NimBus, Service Bus, topology, sessions or transport behaviour caused the failure.", ["lock lost", "session unavailable", "entity not found", "message too large"]),
        ["unknown"] = new QuestionChoice("There is insufficient evidence or no category fits.", []),
    });

    private static readonly Question RetryLikely = new("noul", "Given the supplied failure evidence, is processing the same message again without changing its payload, configuration, application code or dependent data likely to succeed?", null);
    private static readonly Question ChangeRequired = new("noul", "Does the evidence indicate that data, configuration, permissions, application code or another persistent condition must change before this message can succeed?", null);
    private static readonly Question ExternalDependency = new("noul", "Does the evidence indicate that the failure originates primarily in an external dependency rather than in the handler's own logic?", null);

    private static readonly Question FaultOrigin = new("choice", "Where must something change, or where does the fault originate, for this message to succeed?", new Dictionary<string, object?>
    {
        ["source_data"] = new QuestionChoice("The publishing system sent data that is wrong, incomplete or breaks a rule.", ["empty required field", "unknown reference", "value out of range"]),
        ["adapter_code"] = new QuestionChoice("The adapter's own logic is wrong: a mapping, a null it does not handle, a case it does not support.", ["NullReferenceException in a mapper", "unhandled enum value", "wrong field mapping"]),
        ["target_system"] = new QuestionChoice("The receiving system or a dependency is unavailable or refuses a valid request.", ["HTTP 503", "timeout", "throttling", "target rejects a valid record"]),
        ["configuration"] = new QuestionChoice("Credentials, permissions, addresses or settings.", ["expired secret", "401/403", "wrong base URL", "missing setting"]),
        ["platform"] = new QuestionChoice("NimBus, Service Bus, topology or sessions.", ["lock lost", "entity not found", "message too large"]),
        ["unknown"] = new QuestionChoice("Not enough evidence.", []),
    });

    private static readonly Dictionary<string, Question> ProviderQuestions = new(StringComparer.Ordinal)
    {
        ["failure_category"] = FailureCategory,
        ["retry_likely_to_succeed_unchanged"] = RetryLikely,
        ["change_required_before_success"] = ChangeRequired,
        ["external_dependency_involved"] = ExternalDependency,
    };

    private static readonly Dictionary<string, Question> Questions = new(ProviderQuestions, StringComparer.Ordinal)
    {
        ["fault_origin"] = FaultOrigin,
    };

    /// <summary>The current (version <see cref="Version"/>) question set, answered by agents.</summary>
    public static IReadOnlyDictionary<string, Question> Items => Questions;

    /// <summary>The version <see cref="ProviderVersion"/> question set the deprecated provider asks.</summary>
    public static IReadOnlyDictionary<string, Question> ProviderItems => ProviderQuestions;

    /// <summary>
    /// Every category name the question sets can return, including <c>unknown</c>. Readers use
    /// it to treat a stored category from a newer question set as unrecognized (spec 043 §5).
    /// Both versions share the same categories.
    /// </summary>
    public static IReadOnlySet<string> Categories { get; } =
        new HashSet<string>(FailureCategory.Criteria!.Keys, StringComparer.Ordinal);

    /// <summary>Every fault origin of version 2, including <c>unknown</c> (Spec 044 §6.2).</summary>
    public static IReadOnlySet<string> FaultOrigins { get; } =
        new HashSet<string>(FaultOrigin.Criteria!.Keys, StringComparer.Ordinal);

    /// <summary>Question structure. <see cref="Criteria"/> values are <see cref="QuestionChoice"/> for a choice question.</summary>
    public sealed record Question(string Type, string Instructions, IReadOnlyDictionary<string, object?>? Criteria);

    /// <summary>One option of a choice question; serializes as <c>{"what":…,"examples":[…]}</c>.</summary>
    /// <param name="What">What the option means.</param>
    /// <param name="Examples">Typical evidence for it.</param>
    public sealed record QuestionChoice(
        [property: JsonPropertyName("what")] string What,
        [property: JsonPropertyName("examples")] IReadOnlyList<string> Examples);
}
