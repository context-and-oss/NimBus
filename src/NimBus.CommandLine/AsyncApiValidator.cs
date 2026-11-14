using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NimBus.CommandLine;

/// <summary>Outcome of validating an AsyncAPI document.</summary>
public sealed class AsyncApiValidationResult
{
    internal AsyncApiValidationResult(IReadOnlyList<string> errors) => Errors = errors;

    /// <summary>True when no structural errors were found.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>Human-readable structural errors (empty when valid).</summary>
    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// Structural validator for AsyncAPI 3.0 documents. Checks the required top-level sections and,
/// crucially, that each <c>$ref</c> resolves to a node in the <em>correct</em> section for the
/// context it appears in (a payload ref must land on a component schema, not merely on any node).
/// Usable as a CI gate via <see cref="AsyncApiCli.RunValidate"/>.
/// </summary>
public static class AsyncApiValidator
{
    /// <summary>Validates <paramref name="document"/> and returns the collected errors.</summary>
    public static AsyncApiValidationResult Validate(JObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var errors = new List<string>();

        var version = document["asyncapi"]?.Value<string>();
        if (!string.Equals(version, "3.0.0", StringComparison.Ordinal))
        {
            errors.Add($"Top-level 'asyncapi' must be '3.0.0' but was '{version ?? "(missing)"}'.");
        }

        foreach (var section in new[] { "info", "channels", "operations", "components" })
        {
            if (document[section] is null)
            {
                errors.Add($"Required top-level section '{section}' is missing.");
            }
        }

        ValidateOperations(document, errors);
        ValidateChannels(document, errors);
        ValidateMessages(document, errors);
        ValidateNoDanglingRefs(document, errors);

        return new AsyncApiValidationResult(errors);
    }

    private static void ValidateOperations(JObject document, List<string> errors)
    {
        if (document["operations"] is not JObject operations) return;

        foreach (var (opName, opToken) in operations)
        {
            if (opToken is not JObject op) continue;
            var path = $"operations.{opName}";

            // channel.$ref → must resolve under #/channels
            var channelRef = (op["channel"] as JObject)?["$ref"]?.Value<string>();
            if (channelRef != null)
            {
                RequireRefInSection(document, channelRef, "#/channels/", $"{path}.channel", errors);
            }

            // messages[].$ref → generated docs use a channel-scoped message entry that forwards to
            // #/components/messages; supplied valid AsyncAPI docs may point there directly.
            foreach (var (msgRef, msgIndex) in RefsOf(op["messages"]))
            {
                var owner = $"{path}.messages[{msgIndex}]";
                if (msgRef.StartsWith("#/components/messages/", StringComparison.Ordinal))
                {
                    RequireRefInSection(document, msgRef, "#/components/messages/", owner, errors);
                    continue;
                }

                if (!msgRef.StartsWith("#/channels/", StringComparison.Ordinal))
                {
                    errors.Add($"{owner} $ref '{msgRef}' must point to a channel message "
                        + "(#/channels/<channel>/messages/<message>) or component message "
                        + "(#/components/messages/<message>).");
                    continue;
                }

                if (!TryResolve(document, msgRef, out var channelMessage))
                {
                    errors.Add($"{owner} $ref '{msgRef}' does not resolve to an existing node.");
                    continue;
                }

                var componentRef = (channelMessage as JObject)?["$ref"]?.Value<string>();
                if (componentRef is null)
                {
                    errors.Add($"{owner} $ref '{msgRef}' resolves to a node that is not a message reference.");
                }
                else
                {
                    RequireRefInSection(document, componentRef, "#/components/messages/", owner, errors);
                }
            }
        }
    }

    private static void ValidateChannels(JObject document, List<string> errors)
    {
        if (document["channels"] is not JObject channels) return;

        foreach (var (channelName, channelToken) in channels)
        {
            if ((channelToken as JObject)?["messages"] is not JObject messages) continue;

            foreach (var (entryName, entryToken) in messages)
            {
                var reference = (entryToken as JObject)?["$ref"]?.Value<string>();
                if (reference != null)
                {
                    RequireRefInSection(document, reference, "#/components/messages/",
                        $"channels.{channelName}.messages.{entryName}", errors);
                }
            }
        }
    }

    private static void ValidateMessages(JObject document, List<string> errors)
    {
        if ((document["components"] as JObject)?["messages"] is not JObject messages) return;

        foreach (var (messageName, messageToken) in messages)
        {
            if (messageToken is not JObject message) continue;
            var path = $"components.messages.{messageName}";

            // payload.$ref and headers.$ref must resolve to a Schema Object under #/components/schemas.
            foreach (var field in new[] { "payload", "headers" })
            {
                var reference = (message[field] as JObject)?["$ref"]?.Value<string>();
                if (reference != null)
                {
                    RequireRefInSection(document, reference, "#/components/schemas/", $"{path}.{field}", errors);
                }
            }
        }
    }

    // Backstop: any $ref anywhere that does not resolve at all (the section-aware checks above
    // catch wrong-section refs; this catches typos the specific checks don't reach).
    private static void ValidateNoDanglingRefs(JObject document, List<string> errors)
    {
        foreach (var refToken in document.Descendants().OfType<JProperty>().Where(p => p.Name == "$ref"))
        {
            var reference = refToken.Value?.Value<string>();
            if (reference is null || !reference.StartsWith("#/", StringComparison.Ordinal)) continue;
            if (!TryResolve(document, reference, out _))
            {
                errors.Add($"Dangling $ref '{reference}' does not resolve to any node.");
            }
        }
    }

    private static void RequireRefInSection(
        JObject document, string reference, string expectedPrefix, string owner, List<string> errors)
    {
        if (!reference.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            errors.Add($"{owner} $ref '{reference}' must resolve under '{expectedPrefix}'.");
            return;
        }

        if (!TryResolve(document, reference, out _))
        {
            errors.Add($"{owner} $ref '{reference}' does not resolve to an existing component.");
        }
    }

    private static IEnumerable<(string Reference, int Index)> RefsOf(JToken? messages)
    {
        if (messages is not JArray array) yield break;
        for (var i = 0; i < array.Count; i++)
        {
            var reference = (array[i] as JObject)?["$ref"]?.Value<string>();
            if (reference != null) yield return (reference, i);
        }
    }

    /// <summary>Resolves a local <c>#/</c>-rooted JSON pointer against <paramref name="document"/>.</summary>
    private static bool TryResolve(JObject document, string reference, out JToken? target)
    {
        target = null;
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) return false;

        JToken current = document;
        foreach (var rawSegment in reference.Substring(2).Split('/'))
        {
            var segment = rawSegment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            switch (current)
            {
                case JObject obj when obj.TryGetValue(segment, out var next):
                    current = next;
                    break;
                case JArray arr when int.TryParse(segment, out var idx) && idx >= 0 && idx < arr.Count:
                    current = arr[idx];
                    break;
                default:
                    return false;
            }
        }

        target = current;
        return true;
    }
}
