using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Newtonsoft.Json;
using System.Text.Json;

namespace EnrichmentAgent.Classification;

/// <summary>
/// Uses the Anthropic Claude API (claude-haiku-4-5) with structured JSON output
/// to classify a CRM contact's industry, lead score, and rationale.
/// Requires the <c>ANTHROPIC_API_KEY</c> environment variable to be set.
/// </summary>
public sealed class ClaudeContactClassifier : IContactClassifier, IDisposable
{
    /// <summary>The model used for classification. Haiku 4.5 is cheap and fast.</summary>
    public const string ModelId = "claude-haiku-4-5";

    private static readonly Dictionary<string, JsonElement> EnrichmentSchema = BuildSchema();

    private readonly AnthropicClient _client;

    /// <summary>
    /// Initializes a new instance using the ANTHROPIC_API_KEY environment variable.
    /// </summary>
    public ClaudeContactClassifier()
    {
        _client = new AnthropicClient();
    }

    /// <summary>
    /// Initializes a new instance with a custom <see cref="AnthropicClient"/>.
    /// Useful for testing or injecting custom options.
    /// </summary>
    public ClaudeContactClassifier(AnthropicClient client)
    {
        _client = client;
    }

    /// <inheritdoc/>
    public async Task<ContactEnrichment> Classify(ContactInput contact, CancellationToken cancellationToken = default)
    {
        var prompt = BuildPrompt(contact);

        var response = await _client.Messages.Create(
            new MessageCreateParams
            {
                Model = Model.ClaudeHaiku4_5,
                MaxTokens = 1024,
                Messages =
                [
                    new UserMessageParam
                    {
                        Content = new MessageContentParam(prompt),
                    }
                ],
                OutputConfig = new OutputConfig
                {
                    Format = new JsonOutputFormat { Schema = EnrichmentSchema },
                },
            },
            cancellationToken);

        // The model is instructed to return schema-valid JSON; the first text block contains it.
        // A missing/empty/safety-filtered response yields no text block — surface a clear error.
        var json = response.Content
            .Select(b => b.Value)
            .OfType<TextBlock>()
            .FirstOrDefault()?.Text
            ?? throw new InvalidOperationException("Claude returned no text block for the enrichment request.");

        var result = JsonConvert.DeserializeObject<EnrichmentResponse>(json)
            ?? throw new InvalidOperationException($"Claude returned null deserialized result. Raw JSON: {json}");

        // Clamp lead score defensively even though structured output enforces integer.
        var leadScore = Math.Clamp(result.LeadScore, 0, 100);

        return new ContactEnrichment(
            Industry: result.Industry,
            LeadScore: leadScore,
            Rationale: result.Rationale);
    }

    private static string BuildPrompt(ContactInput contact)
    {
        return $"""
            Classify this CRM contact into an industry vertical and estimate a lead score.

            Contact details:
            - Contact ID: {contact.ContactId}
            - First Name: {contact.FirstName ?? "(unknown)"}
            - Last Name:  {contact.LastName ?? "(unknown)"}
            - Email:      {contact.Email ?? "(unknown)"}
            - Phone:      {contact.Phone ?? "(unknown)"}

            Instructions:
            1. Infer the industry vertical from the name, email domain, or any other signal.
               Return a concise label such as "Technology", "Healthcare", "Retail", etc.
               If you cannot determine it, return "General Business".
            2. Estimate a lead score (integer 0–100) based on how likely this contact is
               to become a paying customer. Use heuristics from the contact data.
            3. Write a short rationale (1-2 sentences) explaining your classification.

            Respond ONLY with valid JSON matching the required schema.
            """;
    }

    /// <inheritdoc/>
    public void Dispose() => _client.Dispose();

    private static Dictionary<string, JsonElement> BuildSchema()
    {
        // crm.contact.enriched.v1 schema
        const string schemaJson = """
            {
              "type": "object",
              "required": ["industry", "leadScore", "rationale"],
              "additionalProperties": false,
              "properties": {
                "industry":  { "type": "string" },
                "leadScore": { "type": "integer" },
                "rationale": { "type": "string" }
              }
            }
            """;

        // Dispose the document but keep the values valid: JsonElement.Clone() detaches
        // each value from the pooled-buffer-backed document so it survives disposal.
        using var doc = JsonDocument.Parse(schemaJson);
        return doc.RootElement
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    /// <summary>Private DTO matching the structured output schema.</summary>
    private sealed class EnrichmentResponse
    {
        [JsonProperty("industry")]
        public string Industry { get; set; } = string.Empty;

        [JsonProperty("leadScore")]
        public int LeadScore { get; set; }

        [JsonProperty("rationale")]
        public string? Rationale { get; set; }
    }
}
