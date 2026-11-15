using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NimBus.WebApp.Mcp.Operations;

/// <summary>
/// The checks and the preview behind <c>nimbus_resubmit_with_changes</c> (Spec 035 Phase 3): an
/// edited payload must be a JSON object, is bound to its action token by hash, and is previewed
/// as the JSON paths it changes, never as values.
/// </summary>
public static class OperatorPayloadEdit
{
    /// <summary>Longest edited payload accepted, in characters.</summary>
    public const int MaxLength = 262_144;

    /// <summary>Most changed paths a preview lists.</summary>
    public const int MaxChangedPaths = 50;

    private static readonly JsonSerializerSettings Settings = new() { DateParseHandling = DateParseHandling.None };

    /// <summary>
    /// The edited payload as a JSON object. Throws <c>[InvalidArgument]</c> when it is missing,
    /// too long, not JSON or not an object.
    /// </summary>
    public static JObject Parse(string? eventContent)
    {
        if (string.IsNullOrWhiteSpace(eventContent))
            throw OperatorToolErrors.InvalidArgument("eventContent is required: the complete edited payload as a JSON object.");
        if (eventContent.Length > MaxLength)
            throw OperatorToolErrors.InvalidArgument($"eventContent is longer than {MaxLength} characters.");

        return TryParse(eventContent) as JObject
            ?? throw OperatorToolErrors.InvalidArgument("eventContent must be one JSON object: the complete edited payload.");
    }

    /// <summary>The hash an action token binds an edited payload by: SHA-256 of its exact text.</summary>
    public static string Hash(string eventContent)
    {
        ArgumentNullException.ThrowIfNull(eventContent);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(eventContent)));
    }

    /// <summary>
    /// The JSON paths <paramref name="edited"/> adds, removes or changes against
    /// <paramref name="originalJson"/>, at most <see cref="MaxChangedPaths"/>. An original that is
    /// missing or not JSON makes every top-level property an addition.
    /// </summary>
    public static IReadOnlyList<string> ChangedPaths(string? originalJson, JObject edited)
    {
        ArgumentNullException.ThrowIfNull(edited);
        var changes = new List<string>();
        Compare("$", string.IsNullOrWhiteSpace(originalJson) ? null : TryParse(originalJson), edited, changes);
        if (changes.Count <= MaxChangedPaths)
            return changes;

        var listed = changes.Take(MaxChangedPaths).ToList();
        listed.Add($"… and {changes.Count - MaxChangedPaths} more");
        return listed;
    }

    private static void Compare(string path, JToken? before, JToken? after, List<string> changes)
    {
        if (before is null && after is null)
            return;
        if (before is null)
        {
            changes.Add($"{path} (added)");
            return;
        }

        if (after is null)
        {
            changes.Add($"{path} (removed)");
            return;
        }

        if (before is JObject left && after is JObject right)
        {
            foreach (var name in left.Properties().Select(p => p.Name).Union(right.Properties().Select(p => p.Name), StringComparer.Ordinal))
                Compare($"{path}.{name}", left[name], right[name], changes);
            return;
        }

        if (before is JArray from && after is JArray to)
        {
            for (var index = 0; index < Math.Max(from.Count, to.Count); index++)
                Compare($"{path}[{index}]", index < from.Count ? from[index] : null, index < to.Count ? to[index] : null, changes);
            return;
        }

        if (!JToken.DeepEquals(before, after))
            changes.Add($"{path} (changed)");
    }

    private static JToken? TryParse(string json)
    {
        try
        {
            return JsonConvert.DeserializeObject<JToken>(json, Settings);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
