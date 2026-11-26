using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services;

/// <summary>
/// Validates and normalises an endpoint's record bindings (Spec 051 §5). A caller takes the key
/// from the redacted payload at the binding's path, so a path is a plain dotted path, never a
/// query: no wildcards, indexes, filters or recursive descent.
/// </summary>
public static partial class EndpointRecordBindings
{
    /// <summary>Most bindings one endpoint may list.</summary>
    public const int MaximumBindings = 100;

    /// <summary>Longest event type id, path or entity name.</summary>
    public const int MaximumLength = 200;

    /// <summary>
    /// Null when <paramref name="bindings"/> are valid; otherwise why not, naming the row (1-based)
    /// and the field, so an empty row the user added is not mistaken for a filled-in one. The
    /// WebApp's <c>adapter-mcp.ts</c> gives the same messages.
    /// </summary>
    /// <param name="bindings">The bindings as sent, untrimmed.</param>
    public static string? Validate(IEnumerable<(string? EventTypeId, string? PayloadPath, string? Entity)>? bindings)
    {
        var list = bindings?.ToList() ?? [];
        if (list.Count > MaximumBindings)
            return $"At most {MaximumBindings} record bindings.";

        var seen = new Dictionary<(string, string), int>();
        for (var index = 0; index < list.Count; index++)
        {
            var row = $"Binding {index + 1}";
            var type = list[index].EventTypeId?.Trim() ?? string.Empty;
            var path = list[index].PayloadPath?.Trim() ?? string.Empty;
            var name = list[index].Entity?.Trim() ?? string.Empty;
            (string Name, string Value)[] fields = [("the event type", type), ("the payload path", path), ("the entity", name)];

            var empty = fields.Where(f => f.Value.Length == 0).Select(f => f.Name).ToList();
            if (empty.Count == fields.Length)
                return $"{row} is empty: fill in the event type, the payload path and the entity, or delete it.";
            if (empty.Count > 0)
                return $"{row}: {string.Join(" and ", empty)} {(empty.Count == 1 ? "is" : "are")} empty.";

            foreach (var (fieldName, value) in fields)
            {
                if (value.Length > MaximumLength)
                    return $"{row}: {fieldName} is longer than {MaximumLength} characters.";
            }

            if (type.AsSpan().ContainsAnyInRange('\0', '\u001f'))
                return $"{row}: the event type contains control characters.";
            if (!PathPattern().IsMatch(path))
                return $"{row}: the payload path '{path}' must be a dotted path such as $.AccountId, without wildcards, indexes or filters.";
            if (!EntityPattern().IsMatch(name))
                return $"{row}: the entity '{name}' must be a name of letters, digits, '.', '_' or '-'.";
            if (seen.TryGetValue((type.ToUpperInvariant(), path.ToUpperInvariant()), out var first))
                return $"{row}: the path {path} is already bound for {type} in binding {first}.";
            seen[(type.ToUpperInvariant(), path.ToUpperInvariant())] = index + 1;
        }

        return null;
    }

    /// <summary>The bindings trimmed, as stored.</summary>
    /// <param name="bindings">Validated bindings.</param>
    public static List<RecordBinding> Normalize(IEnumerable<(string? EventTypeId, string? PayloadPath, string? Entity)>? bindings)
        => bindings?.Select(b => new RecordBinding
        {
            EventTypeId = b.EventTypeId!.Trim(),
            PayloadPath = b.PayloadPath!.Trim(),
            Entity = b.Entity!.Trim(),
        }).ToList() ?? [];

    [GeneratedRegex(@"^\$(\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EntityPattern();
}
