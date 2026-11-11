using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NimBus.Extensions.IntegrationIntelligence.Evidence;

namespace NimBus.Extensions.IntegrationIntelligence.Providers;

/// <summary>Failure raised by the provider with a stable operational category. Deprecated; removed in v5.0.0.</summary>
[Obsolete(IntegrationIntelligenceDeprecation.Provider)]
public sealed class IntelligenceProviderException : Exception
{
    /// <summary>Creates a provider exception.</summary>
    public IntelligenceProviderException(string code, bool outcomeUnknown, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        OutcomeUnknown = outcomeUnknown;
    }

    /// <summary>Stable provider error code.</summary>
    public string Code { get; }

    /// <summary>True when the provider may have accepted the request.</summary>
    public bool OutcomeUnknown { get; }
}

/// <summary>Named TypeSafe provider client. It sends one bounded request per analysis. Deprecated; removed in v5.0.0.</summary>
[Obsolete(IntegrationIntelligenceDeprecation.Provider)]
public sealed class TypeSafeFailureIntelligenceProvider : IFailureIntelligenceProvider
{
    private const string ClientName = "NimBus.TypeSafe";
    private readonly HttpClient _httpClient;
    private readonly FailureClassificationOptions _options;
    private readonly ILogger<TypeSafeFailureIntelligenceProvider> _logger;
    private static readonly Action<ILogger, int, Exception?> ProviderStatusLog =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(1, "ProviderStatus"), "TypeSafe returned HTTP status {StatusCode}.");

    /// <summary>Creates a TypeSafe client.</summary>
    public TypeSafeFailureIntelligenceProvider(
        IHttpClientFactory clientFactory,
        FailureClassificationOptions options,
        ILogger<TypeSafeFailureIntelligenceProvider> logger)
    {
        _httpClient = clientFactory.CreateClient(ClientName);
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "TypeSafe";

    /// <inheritdoc />
    public async Task<FailureIntelligenceProviderResult> ClassifyAsync(
        FailureClassificationInput input,
        CancellationToken cancellationToken = default)
    {
        var state = OutboundFailureState.Create(input);
        var request = new
        {
            model = _options.Model,
            state,
            questions = FailureClassificationQuestionSet.ProviderItems.ToDictionary(
                pair => pair.Key,
                pair => new { type = pair.Value.Type, instructions = pair.Value.Instructions, criteria = pair.Value.Criteria }),
        };

        var body = JsonSerializer.Serialize(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        for (var attempt = 0; ; attempt++)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/systemone")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IntelligenceProviderException("ProviderTimeout", outcomeUnknown: true, "The provider request timed out.");
            }
            catch (HttpRequestException exception)
            {
                throw new IntelligenceProviderException("ProviderTransportFailure", outcomeUnknown: true, "The provider request did not complete.", exception);
            }

            using (response)
            {
                if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode == 529) && attempt < 2)
                {
                    var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt));
                    try { await Task.Delay(delay, deadline.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new IntelligenceProviderException("ProviderTimeout", outcomeUnknown: true, "The provider retry budget expired.");
                    }
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    ProviderStatusLog(_logger, (int)response.StatusCode, null);
                    var code = response.StatusCode == HttpStatusCode.Unauthorized ? "ProviderUnauthorized" : "ProviderRejected";
                    throw new IntelligenceProviderException(code, outcomeUnknown: false, "The provider rejected the classification request.");
                }

                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token).ConfigureAwait(false);
                    return ParseResult(document.RootElement);
                }
                catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
                {
                    throw new IntelligenceProviderException("ProviderInvalidResponse", outcomeUnknown: false, "The provider response was invalid.", exception);
                }
            }
        }
    }

    private static FailureIntelligenceProviderResult ParseResult(JsonElement root)
    {
        var model = RequiredString(root, "model");
        var answers = root.GetProperty("answers");
        var category = answers.GetProperty("failure_category");
        var categoryId = RequiredString(category, "choice");
        var pinnedCategories = FailureClassificationQuestionSet.ProviderItems["failure_category"].Criteria!.Keys
            .ToHashSet(StringComparer.Ordinal);
        if (!pinnedCategories.Contains(categoryId))
        {
            throw new IntelligenceProviderException("ProviderInvalidResponse", false, "The provider returned an unknown failure category.");
        }
        var confidence = RequiredFinite(category, "confidence");
        if (!category.TryGetProperty("probabilities", out var probabilityMap)
            || probabilityMap.ValueKind != JsonValueKind.Object)
        {
            throw InvalidDistribution();
        }

        Dictionary<string, double> probabilities;
        try
        {
            probabilities = probabilityMap.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetDouble(), StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            throw InvalidDistribution(exception);
        }

        if (!probabilities.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(pinnedCategories)
            || !probabilities.ContainsKey(categoryId)
            || probabilities.Values.Any(value => value is < 0 or > 1 || double.IsNaN(value) || double.IsInfinity(value))
            || Math.Abs(probabilities.Values.Sum() - 1d) > 0.001)
        {
            throw InvalidDistribution();
        }

        var usage = root.TryGetProperty("usage", out var usageElement) ? usageElement : default;
        int? inputTokens = ReadUsage(usage.ValueKind, usageElement, "input_tokens");
        int? outputTokens = ReadUsage(usage.ValueKind, usageElement, "output_tokens");
        return new FailureIntelligenceProviderResult(
            model,
            categoryId,
            confidence,
            probabilities,
            RequiredNoul(answers, "retry_likely_to_succeed_unchanged"),
            RequiredNoul(answers, "change_required_before_success"),
            RequiredNoul(answers, "external_dependency_involved"),
            inputTokens,
            outputTokens);
    }

    private static IntelligenceProviderException InvalidDistribution(Exception? inner = null)
        => new("ProviderInvalidResponse", false, "The provider returned an invalid category distribution.", inner);

    private static int? ReadUsage(JsonValueKind usageKind, JsonElement usage, string name)
    {
        if (usageKind != JsonValueKind.Object || !usage.TryGetProperty(name, out var value)) return null;
        if (!value.TryGetInt32(out var number) || number < 0)
            throw new IntelligenceProviderException("ProviderInvalidResponse", false, $"The provider returned an invalid '{name}'.");
        return number;
    }

    private static double RequiredNoul(JsonElement answers, string name)
        => RequiredFinite(answers.GetProperty(name), "noul");

    private static string RequiredString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new IntelligenceProviderException("ProviderInvalidResponse", false, $"The provider response did not contain '{name}'.");

    private static double RequiredFinite(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var value)
            || value is < 0 or > 1 || double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new IntelligenceProviderException("ProviderInvalidResponse", false, $"The provider response did not contain a valid '{name}'.");
        }
        return value;
    }
}
