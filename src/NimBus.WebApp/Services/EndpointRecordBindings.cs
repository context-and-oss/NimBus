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

    /// <summary>Null when <paramref name="bindings"/> are valid; otherwise why not.</summary>
    /// <param name="bindings">The bindings as sent, untrimmed.</param>
    public static string? Validate(IEnumerable<(string? EventTypeId, string? PayloadPath, string? Entity)>? bindings)
    {
        var list = bindings?.ToList() ?? [];
        if (list.Count > MaximumBindings)
            return $"At most {MaximumBindings} record bindings.";

        var seen = new HashSet<(string, string)>();
        foreach (var (eventTypeId, payloadPath, entity) in list)
        {
            var type = eventTypeId?.Trim();
            var path = payloadPath?.Trim();
            var name = entity?.Trim();
            if (string.IsNullOrEmpty(type) || type.Length > MaximumLength || type.AsSpan().ContainsAnyInRange('\0', '\u001f'))
                return $"Each record binding needs an event type of at most {MaximumLength} characters.";
            if (string.IsNullOrEmpty(path) || path.Length > MaximumLength || !PathPattern().IsMatch(path))
                return $"The record binding path '{path}' must be a dotted path such as $.AccountId, without wildcards, indexes or filters.";
            if (string.IsNullOrEmpty(name) || name.Length > MaximumLength || !EntityPattern().IsMatch(name))
                return $"The record binding entity '{name}' must be a name of letters, digits, '.', '_' or '-'.";
            if (!seen.Add((type.ToUpperInvariant(), path.ToUpperInvariant())))
                return $"The path {path} is bound twice for {type}.";
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
