namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>Versioned question set sent as one provider request.</summary>
public static class FailureClassificationQuestionSet
{
    /// <summary>Current question-set version.</summary>
    public const int Version = 1;

    private static readonly Dictionary<string, Question> Questions =
        new Dictionary<string, Question>(StringComparer.Ordinal)
        {
            ["failure_category"] = new("choice", "Classify the primary cause of this failure.", new Dictionary<string, object?>
            {
                ["transient_dependency"] = new { what = "A temporary downstream or infrastructure condition that may clear without changing the message, configuration or code.", examples = new[] { "timeout", "connection reset", "HTTP 429", "temporary 5xx", "throttling", "deadlock", "DNS/network blip" } },
                ["authentication_configuration"] = new { what = "Credentials, permissions, endpoint URLs, certificates or environment settings are wrong or expired.", examples = new[] { "401/403", "expired secret", "wrong host", "missing setting" } },
                ["contract_schema"] = new { what = "The data does not satisfy the technical contract.", examples = new[] { "malformed JSON", "deserialization error", "missing required property", "wrong type", "unsupported version" } },
                ["business_rule"] = new { what = "Technically valid data was rejected by a domain rule.", examples = new[] { "invalid state transition", "credit limit", "closed account" } },
                ["missing_reference_data"] = new { what = "A referenced business record does not exist.", examples = new[] { "customer not found", "order not found", "missing master data" } },
                ["application_defect"] = new { what = "Evidence points at a programming error.", examples = new[] { "NullReferenceException", "IndexOutOfRangeException", "unexpected internal exception" } },
                ["messaging_platform"] = new { what = "NimBus, Service Bus, topology, sessions or transport behaviour caused the failure.", examples = new[] { "lock lost", "session unavailable", "entity not found", "message too large" } },
                ["unknown"] = new { what = "There is insufficient evidence or no category fits.", examples = Array.Empty<string>() },
            }),
            ["retry_likely_to_succeed_unchanged"] = new("noul", "Given the supplied failure evidence, is processing the same message again without changing its payload, configuration, application code or dependent data likely to succeed?", null),
            ["change_required_before_success"] = new("noul", "Does the evidence indicate that data, configuration, permissions, application code or another persistent condition must change before this message can succeed?", null),
            ["external_dependency_involved"] = new("noul", "Does the evidence indicate that the failure originates primarily in an external dependency rather than in the handler's own logic?", null),
        };

    /// <summary>Returns an immutable view of the current question set.</summary>
    public static IReadOnlyDictionary<string, Question> Items => Questions;

    /// <summary>
    /// Every category name the current question set can return, including <c>unknown</c>. Readers use
    /// it to treat a stored category from a newer question set as unrecognized (spec 043 §5).
    /// </summary>
    public static IReadOnlySet<string> Categories { get; } =
        new HashSet<string>(Questions["failure_category"].Criteria!.Keys, StringComparer.Ordinal);

    /// <summary>Provider question structure.</summary>
    public sealed record Question(string Type, string Instructions, IReadOnlyDictionary<string, object?>? Criteria);
}
