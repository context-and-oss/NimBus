using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace NimBus.Extensions.IntegrationIntelligence.Evidence;

/// <summary>Removes common credential-shaped values from provider evidence.</summary>
public sealed class IntelligenceDataRedactor
{
    private readonly HashSet<string> _additionalSecretKeys;

    /// <summary>Creates a redactor with optional operator-defined sensitive keys.</summary>
    public IntelligenceDataRedactor(FailureClassificationOptions? options = null)
        => _additionalSecretKeys = (options?.Data.AdditionalRedactedKeys ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "secret", "clientSecret", "token", "accessToken", "refreshToken",
        "authorization", "apiKey", "connectionString", "cookie", "bearer", "sharedAccessKey",
    };

    private static readonly Regex SecretPattern = new(
        "(?i)(Authorization\\s*:\\s*Bearer\\s*|SharedAccessKey\\s*[=:]\\s*|Password\\s*[=:]\\s*|AccountKey\\s*[=:]\\s*)([^\\s,;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly Regex ExceptionDumpPattern = new(
        "(?m)^\\s*at\\s+[^\\r\\n]+$|--- End of (?:inner exception|stack trace)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // " in C:\src\Adapter\Mapper.cs:line 42" → " in Mapper.cs:line 42": the file and line help an
    // agent find the code; the build machine's directory layout does not.
    private static readonly Regex StackTraceFilePattern = new(
        @"(?m) in (?:[^\r\n]*[\\/])?([^\\/\r\n]+?):line (\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>The longest scrubbed stack trace.</summary>
    public const int MaximumStackTraceLength = 8000;

    /// <summary>
    /// Scrubs a stack trace for agents (Spec 044 Phase 3): keeps the frames, shortens file paths to
    /// the file name and redacts credential-shaped values. Unlike <see cref="ScrubText"/>, frames
    /// are what is wanted here.
    /// </summary>
    /// <param name="value">The stored trace.</param>
    /// <param name="maximumLength">The most characters to keep.</param>
    public static string? ScrubStackTrace(string? value, int maximumLength = MaximumStackTraceLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var scrubbed = SecretPattern.Replace(StackTraceFilePattern.Replace(value, " in $1:line $2"), "$1[redacted]");
        return scrubbed.Length <= maximumLength ? scrubbed : scrubbed[..maximumLength];
    }

    /// <summary>Returns a scrubbed free-text field, or null for embedded exception dumps.</summary>
    public static string? ScrubText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || ExceptionDumpPattern.IsMatch(value))
        {
            return null;
        }

        var scrubbed = SecretPattern.Replace(value, "$1[redacted]");
        return scrubbed.Length <= maximumLength ? scrubbed : scrubbed[..maximumLength];
    }

    /// <summary>Scrubs secret-shaped object values while preserving JSON structure.</summary>
    public string? ScrubJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var token = JToken.Parse(json);
            ScrubToken(token);
            return token.ToString(Newtonsoft.Json.Formatting.None);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    private void ScrubToken(JToken token)
    {
        if (token is JObject obj)
        {
            foreach (var property in obj.Properties().ToList())
            {
                if (SecretKeys.Contains(property.Name) || _additionalSecretKeys.Contains(property.Name))
                {
                    property.Value = "[redacted]";
                }
                else
                {
                    ScrubToken(property.Value);
                }
            }
        }
        else if (token is JArray array)
        {
            foreach (var child in array) ScrubToken(child);
        }
    }
}
