using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NimBus.CommandLine;

/// <summary>Kind of change detected between two AsyncAPI documents.</summary>
public enum ChangeKind
{
    /// <summary>A construct present only in the new document.</summary>
    Added,

    /// <summary>A construct present only in the old document.</summary>
    Removed,

    /// <summary>A construct present in both but modified.</summary>
    Changed,
}

/// <summary>A single classified difference between two AsyncAPI documents.</summary>
public sealed class AsyncApiChange
{
    internal AsyncApiChange(string category, ChangeKind kind, string path, bool breaking, string detail)
    {
        Category = category;
        Kind = kind;
        Path = path;
        Breaking = breaking;
        Detail = detail;
    }

    /// <summary>Section the change belongs to (channels, operations, messages, schemas).</summary>
    public string Category { get; }

    /// <summary>Whether the construct was added, removed, or changed.</summary>
    public ChangeKind Kind { get; }

    /// <summary>Document path of the affected construct.</summary>
    public string Path { get; }

    /// <summary>True when the change breaks existing consumers/producers (fails a CI gate).</summary>
    public bool Breaking { get; }

    /// <summary>Human-readable description of the change.</summary>
    public string Detail { get; }
}

/// <summary>Outcome of diffing two AsyncAPI documents.</summary>
public sealed class AsyncApiDiffResult
{
    internal AsyncApiDiffResult(IReadOnlyList<AsyncApiChange> changes) => Changes = changes;

    /// <summary>All classified changes.</summary>
    public IReadOnlyList<AsyncApiChange> Changes { get; }

    /// <summary>True when any change is breaking.</summary>
    public bool HasBreaking => Changes.Any(c => c.Breaking);
}

/// <summary>
/// Compares two AsyncAPI 3.0 documents and classifies added/removed/changed channels, operations,
/// messages, and schemas, flagging breaking changes for build gating (via <see cref="AsyncApiCli.RunDiff"/>).
/// </summary>
public static class AsyncApiDiff
{
    /// <summary>Diffs <paramref name="oldDoc"/> against <paramref name="newDoc"/>.</summary>
    public static AsyncApiDiffResult Diff(JObject oldDoc, JObject newDoc)
    {
        ArgumentNullException.ThrowIfNull(oldDoc);
        ArgumentNullException.ThrowIfNull(newDoc);

        var changes = new List<AsyncApiChange>();

        DiffChannels(oldDoc, newDoc, changes);
        DiffOperations(oldDoc, newDoc, changes);
        DiffMessages(oldDoc, newDoc, changes);
        DiffSchemas(oldDoc, newDoc, changes);

        return new AsyncApiDiffResult(changes);
    }

    private static void DiffChannels(JObject oldDoc, JObject newDoc, List<AsyncApiChange> changes)
    {
        var oldChannels = Section(oldDoc, "channels");
        var newChannels = Section(newDoc, "channels");

        foreach (var (name, oldChannel, newChannel) in Pair(oldChannels, newChannels))
        {
            var path = $"channels.{name}";
            if (newChannel is null)
            {
                changes.Add(new AsyncApiChange("channels", ChangeKind.Removed, path, breaking: true, "Channel removed."));
                continue;
            }

            if (oldChannel is null)
            {
                changes.Add(new AsyncApiChange("channels", ChangeKind.Added, path, breaking: false, "Channel added."));
                continue;
            }

            // address change → routing address moved (breaking).
            var oldAddress = oldChannel["address"]?.Value<string>();
            var newAddress = newChannel["address"]?.Value<string>();
            if (!string.Equals(oldAddress, newAddress, StringComparison.Ordinal))
            {
                changes.Add(new AsyncApiChange("channels", ChangeKind.Changed, $"{path}.address",
                    breaking: true, $"Channel address changed ('{oldAddress}' → '{newAddress}')."));
            }

            // message-key removed from the channel = breaking; added = additive.
            var oldMessages = oldChannel["messages"] as JObject;
            var newMessages = newChannel["messages"] as JObject;
            DiffKeySet("channels", $"{path}.messages",
                Keys(oldMessages), Keys(newMessages),
                removedBreaking: true, changes, thing: "message");

            // A same-key channel message whose $ref is retargeted (points at a different component
            // message) changes the channel/message contract even though the key is unchanged — breaking.
            foreach (var (messageKey, oldEntry, newEntry) in Pair(oldMessages, newMessages))
            {
                if (oldEntry is null || newEntry is null) continue; // add/remove already reported above.
                var oldRef = oldEntry["$ref"]?.Value<string>();
                var newRef = newEntry["$ref"]?.Value<string>();
                if (!string.Equals(oldRef, newRef, StringComparison.Ordinal))
                {
                    changes.Add(new AsyncApiChange("channels", ChangeKind.Changed, $"{path}.messages.{messageKey}",
                        breaking: true, $"Channel message '{messageKey}' $ref changed ('{oldRef ?? "(none)"}' → '{newRef ?? "(none)"}')."));
                }
            }

            // bindings / x-servicebus / x-* / description → informational.
            foreach (var field in new[] { "bindings", "x-servicebus", "description" })
            {
                if (!JToken.DeepEquals(oldChannel[field], newChannel[field]))
                {
                    changes.Add(new AsyncApiChange("channels", ChangeKind.Changed, $"{path}.{field}",
                        breaking: false, $"Channel {field} changed."));
                }
            }
        }
    }

    private static void DiffOperations(JObject oldDoc, JObject newDoc, List<AsyncApiChange> changes)
    {
        var oldOps = Section(oldDoc, "operations");
        var newOps = Section(newDoc, "operations");

        foreach (var (name, oldOp, newOp) in Pair(oldOps, newOps))
        {
            var path = $"operations.{name}";
            if (newOp is null)
            {
                changes.Add(new AsyncApiChange("operations", ChangeKind.Removed, path, breaking: true, "Operation removed."));
                continue;
            }

            if (oldOp is null)
            {
                changes.Add(new AsyncApiChange("operations", ChangeKind.Added, path, breaking: false, "Operation added."));
                continue;
            }

            // action flip (send↔receive) → breaking direction change.
            var oldAction = oldOp["action"]?.Value<string>();
            var newAction = newOp["action"]?.Value<string>();
            if (!string.Equals(oldAction, newAction, StringComparison.Ordinal))
            {
                changes.Add(new AsyncApiChange("operations", ChangeKind.Changed, $"{path}.action",
                    breaking: true, $"Operation action changed ('{oldAction}' → '{newAction}')."));
            }

            // channel.$ref change → operation routed to a different topic (breaking).
            var oldChannel = (oldOp["channel"] as JObject)?["$ref"]?.Value<string>();
            var newChannel = (newOp["channel"] as JObject)?["$ref"]?.Value<string>();
            if (!string.Equals(oldChannel, newChannel, StringComparison.Ordinal))
            {
                changes.Add(new AsyncApiChange("operations", ChangeKind.Changed, $"{path}.channel",
                    breaking: true, $"Operation channel changed ('{oldChannel}' → '{newChannel}')."));
            }

            // messages[] association set: a ref removed from the operation = breaking capability
            // removal; added = additive; a retarget shows up as removed+added.
            DiffKeySet("operations", $"{path}.messages",
                MessageRefs(oldOp), MessageRefs(newOp), removedBreaking: true, changes, thing: "message association");
        }
    }

    private static void DiffMessages(JObject oldDoc, JObject newDoc, List<AsyncApiChange> changes)
    {
        var oldMessages = ComponentSection(oldDoc, "messages");
        var newMessages = ComponentSection(newDoc, "messages");

        foreach (var (name, oldMessage, newMessage) in Pair(oldMessages, newMessages))
        {
            var path = $"components.messages.{name}";
            if (newMessage is null)
            {
                changes.Add(new AsyncApiChange("messages", ChangeKind.Removed, path, breaking: true, "Message removed."));
                continue;
            }

            if (oldMessage is null)
            {
                changes.Add(new AsyncApiChange("messages", ChangeKind.Added, path, breaking: false, "Message added."));
                continue;
            }

            // payload.$ref / headers.$ref changes → contract/type change (breaking).
            CompareRef(oldMessage, newMessage, "payload", $"{path}.payload", "messages", changes);
            CompareRef(oldMessage, newMessage, "headers", $"{path}.headers", "messages", changes);

            // contentType → wire format (breaking).
            CompareScalar(oldMessage, newMessage, "contentType", $"{path}.contentType", "messages", breaking: true, changes);

            // session semantics under x-servicebus (breaking).
            var oldSb = oldMessage["x-servicebus"] as JObject;
            var newSb = newMessage["x-servicebus"] as JObject;
            CompareScalar(oldSb, newSb, "requiresSession", $"{path}.x-servicebus.requiresSession", "messages", breaking: true, changes);
            CompareScalar(oldSb, newSb, "sessionKeyProperty", $"{path}.x-servicebus.sessionKeyProperty", "messages", breaking: true, changes);

            // name/title/summary/description/tags/examples/externalDocs/x-nimbus-governance/bindings → informational.
            foreach (var field in new[] { "name", "title", "summary", "description", "tags", "examples", "externalDocs", "x-nimbus-governance", "bindings" })
            {
                if (!JToken.DeepEquals(oldMessage[field], newMessage[field]))
                {
                    changes.Add(new AsyncApiChange("messages", ChangeKind.Changed, $"{path}.{field}",
                        breaking: false, $"Message {field} changed."));
                }
            }
        }
    }

    private static void DiffSchemas(JObject oldDoc, JObject newDoc, List<AsyncApiChange> changes)
    {
        var oldSchemas = ComponentSection(oldDoc, "schemas");
        var newSchemas = ComponentSection(newDoc, "schemas");

        foreach (var (name, oldSchema, newSchema) in Pair(oldSchemas, newSchemas))
        {
            var path = $"components.schemas.{name}";
            if (newSchema is null)
            {
                changes.Add(new AsyncApiChange("schemas", ChangeKind.Removed, path, breaking: true, "Schema removed."));
                continue;
            }

            if (oldSchema is null)
            {
                changes.Add(new AsyncApiChange("schemas", ChangeKind.Added, path, breaking: false, "Schema added."));
                continue;
            }

            DiffSchemaProperties(name, oldSchema, newSchema, path, changes);
            DiffRequired(oldSchema, newSchema, path, changes);

            // Root-level metadata (title/description/deprecated) — informational, never breaking, but
            // must be reported so a metadata-only schema delta is not swallowed as "No differences".
            CompareMetadata(oldSchema, newSchema, "title", path, changes);
            CompareMetadata(oldSchema, newSchema, "description", path, changes);
            CompareMetadata(oldSchema, newSchema, "deprecated", path, changes);

            // Root-level enum on a component schema that is itself an enum (e.g. components.schemas.Status):
            // a removed value is breaking (a producer may still emit it); an added value is additive.
            DiffKeySet("schemas", $"{path}.enum",
                EnumValues(oldSchema), EnumValues(newSchema), removedBreaking: true, changes, thing: "enum value");
            DiffArrayItemSchema(oldSchema, newSchema, path, changes);

            // Root-level effective shape (scalar type/format, array items, $ref): a change breaks
            // deserialization. Object schemas normalize to "object", so property/required deltas are not
            // double-reported here — those stay the responsibility of DiffSchemaProperties/DiffRequired.
            var oldShape = NormalizeShape(oldSchema);
            var newShape = NormalizeShape(newSchema);
            if (!string.Equals(oldShape, newShape, StringComparison.Ordinal))
            {
                changes.Add(new AsyncApiChange("schemas", ChangeKind.Changed, $"{path}.type",
                    breaking: true, $"Schema type changed ('{oldShape}' → '{newShape}')."));
            }
        }
    }

    private static void DiffSchemaProperties(string name, JObject oldSchema, JObject newSchema, string path, List<AsyncApiChange> changes)
    {
        var oldProps = oldSchema["properties"] as JObject;
        var newProps = newSchema["properties"] as JObject;

        foreach (var (propName, oldProp, newProp) in Pair(oldProps, newProps))
        {
            var propPath = $"{path}.properties.{propName}";
            if (newProp is null)
            {
                changes.Add(new AsyncApiChange("schemas", ChangeKind.Removed, propPath, breaking: true, "Property removed."));
                continue;
            }

            if (oldProp is null)
            {
                changes.Add(new AsyncApiChange("schemas", ChangeKind.Added, propPath, breaking: false, "Property added."));
                continue;
            }

            // Effective shape (type/format/$ref/array-items) — a change here breaks deserialization.
            var oldShape = NormalizeShape(oldProp);
            var newShape = NormalizeShape(newProp);
            if (!string.Equals(oldShape, newShape, StringComparison.Ordinal))
            {
                changes.Add(new AsyncApiChange("schemas", ChangeKind.Changed, propPath,
                    breaking: true, $"Property type changed ('{oldShape}' → '{newShape}')."));
            }

            // enum value removed → breaking (a producer may still emit it); added → additive.
            DiffKeySet("schemas", $"{propPath}.enum",
                EnumValues(oldProp), EnumValues(newProp), removedBreaking: true, changes, thing: "enum value");
            DiffArrayItemSchema(oldProp, newProp, propPath, changes);

            // [Range] validation bounds: tightening rejects values valid before (breaking); relaxing or
            // removing a bound is additive. A raised minimum or a lowered maximum is a tighter bound.
            CompareBound(oldProp, newProp, "minimum", propPath, BoundDirection.TightensWhenRaised, changes);
            CompareBound(oldProp, newProp, "maximum", propPath, BoundDirection.TightensWhenLowered, changes);

            // Documentation metadata ([Description]) — informational, never breaking.
            CompareMetadata(oldProp, newProp, "description", propPath, changes);
        }
    }

    private static void DiffArrayItemSchema(JToken oldSchema, JToken newSchema, string path, List<AsyncApiChange> changes)
    {
        var oldItems = (oldSchema as JObject)?["items"];
        var newItems = (newSchema as JObject)?["items"];
        if (oldItems is null || newItems is null) return;

        DiffKeySet("schemas", $"{path}.items.enum",
            EnumValues(oldItems), EnumValues(newItems), removedBreaking: true, changes, thing: "enum value");
        DiffArrayItemSchema(oldItems, newItems, $"{path}.items", changes);
    }

    /// <summary>Whether a numeric validation bound gets more restrictive when raised or when lowered.</summary>
    private enum BoundDirection
    {
        /// <summary>A larger value is a tighter bound (e.g. <c>minimum</c>).</summary>
        TightensWhenRaised,

        /// <summary>A smaller value is a tighter bound (e.g. <c>maximum</c>).</summary>
        TightensWhenLowered,
    }

    // Compares a numeric validation bound. A newly-added or tightened bound rejects previously-valid
    // values (breaking); a removed or relaxed bound is additive.
    private static void CompareBound(JObject oldProp, JObject newProp, string field, string propPath, BoundDirection direction, List<AsyncApiChange> changes)
    {
        var oldToken = oldProp[field];
        var newToken = newProp[field];
        if (JToken.DeepEquals(oldToken, newToken)) return;

        var oldValue = AsDecimal(oldToken);
        var newValue = AsDecimal(newToken);

        bool breaking;
        if (newValue is null)
        {
            breaking = false; // bound removed → relaxed.
        }
        else if (oldValue is null)
        {
            breaking = true; // bound newly added → tighter.
        }
        else
        {
            breaking = direction == BoundDirection.TightensWhenRaised
                ? newValue > oldValue
                : newValue < oldValue;
        }

        changes.Add(new AsyncApiChange("schemas", ChangeKind.Changed, $"{propPath}.{field}",
            breaking, $"Property {field} changed ('{Display(oldToken)}' → '{Display(newToken)}')."));
    }

    // Reports a metadata field change as informational (non-breaking).
    private static void CompareMetadata(JObject oldProp, JObject newProp, string field, string propPath, List<AsyncApiChange> changes)
    {
        if (JToken.DeepEquals(oldProp[field], newProp[field])) return;

        changes.Add(new AsyncApiChange("schemas", ChangeKind.Changed, $"{propPath}.{field}",
            breaking: false, $"Property {field} changed."));
    }

    private static decimal? AsDecimal(JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null) return null;
        return decimal.TryParse(token.Value<string>(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : (decimal?)null;
    }

    private static string Display(JToken? token) => token is null || token.Type == JTokenType.Null ? "(none)" : token.ToString();

    private static void DiffRequired(JObject oldSchema, JObject newSchema, string path, List<AsyncApiChange> changes)
    {
        var oldRequired = ArrayValues(oldSchema["required"]);
        var newRequired = ArrayValues(newSchema["required"]);

        foreach (var added in newRequired.Except(oldRequired, StringComparer.Ordinal))
        {
            changes.Add(new AsyncApiChange("schemas", ChangeKind.Changed, $"{path}.required",
                breaking: true, $"Property '{added}' became required."));
        }

        foreach (var removed in oldRequired.Except(newRequired, StringComparer.Ordinal))
        {
            changes.Add(new AsyncApiChange("schemas", ChangeKind.Changed, $"{path}.required",
                breaking: false, $"Property '{removed}' no longer required."));
        }
    }

    // ---- shape/normalization helpers ----

    // Captures the three shapes MapType emits: $ref for nested objects (no type), {type:array,items}
    // for collections, {type, format?} for scalars — so a scalar↔$ref switch, $ref retarget, or
    // array element type/format change is detected even though the raw JSON differs in structure.
    private static string NormalizeShape(JToken prop)
    {
        if (prop is not JObject obj) return "unknown";

        var reference = obj["$ref"]?.Value<string>();
        if (reference != null) return $"ref:{reference}";

        var type = obj["type"]?.Value<string>();
        if (string.Equals(type, "array", StringComparison.Ordinal))
        {
            var items = obj["items"];
            return $"array<{(items is null ? "?" : NormalizeShape(items))}>";
        }

        var format = obj["format"]?.Value<string>();
        return format is null ? $"{type}" : $"{type}:{format}";
    }

    private static void CompareRef(JObject oldObj, JObject newObj, string field, string path, string category, List<AsyncApiChange> changes)
    {
        var oldRef = (oldObj[field] as JObject)?["$ref"]?.Value<string>();
        var newRef = (newObj[field] as JObject)?["$ref"]?.Value<string>();
        if (!string.Equals(oldRef, newRef, StringComparison.Ordinal))
        {
            changes.Add(new AsyncApiChange(category, ChangeKind.Changed, path,
                breaking: true, $"{field} $ref changed ('{oldRef ?? "(none)"}' → '{newRef ?? "(none)"}')."));
        }
    }

    private static void CompareScalar(JObject? oldObj, JObject? newObj, string field, string path, string category, bool breaking, List<AsyncApiChange> changes)
    {
        var oldValue = oldObj?[field];
        var newValue = newObj?[field];
        if (!JToken.DeepEquals(oldValue, newValue))
        {
            changes.Add(new AsyncApiChange(category, ChangeKind.Changed, path,
                breaking, $"{field} changed ('{oldValue}' → '{newValue}')."));
        }
    }

    // Emits Added/Removed changes for the symmetric difference of two key sets.
    private static void DiffKeySet(
        string category, string path, IEnumerable<string> oldKeys, IEnumerable<string> newKeys,
        bool removedBreaking, List<AsyncApiChange> changes, string thing)
    {
        var oldSet = new HashSet<string>(oldKeys, StringComparer.Ordinal);
        var newSet = new HashSet<string>(newKeys, StringComparer.Ordinal);

        foreach (var removed in oldSet.Where(k => !newSet.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
        {
            changes.Add(new AsyncApiChange(category, ChangeKind.Removed, $"{path}.{removed}",
                removedBreaking, $"{Capitalize(thing)} '{removed}' removed."));
        }

        foreach (var added in newSet.Where(k => !oldSet.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
        {
            changes.Add(new AsyncApiChange(category, ChangeKind.Added, $"{path}.{added}",
                breaking: false, $"{Capitalize(thing)} '{added}' added."));
        }
    }

    // ---- extraction helpers ----

    private static JObject? Section(JObject doc, string name) => doc[name] as JObject;

    private static JObject? ComponentSection(JObject doc, string name) => (doc["components"] as JObject)?[name] as JObject;

    private static IEnumerable<string> Keys(JObject? obj) =>
        obj?.Properties().Select(p => p.Name) ?? Enumerable.Empty<string>();

    private static IEnumerable<string> MessageRefs(JObject op)
    {
        if (op["messages"] is not JArray array) yield break;
        foreach (var entry in array)
        {
            var reference = (entry as JObject)?["$ref"]?.Value<string>();
            if (reference != null) yield return reference;
        }
    }

    private static IEnumerable<string> EnumValues(JToken prop) => ArrayValues((prop as JObject)?["enum"]);

    private static IEnumerable<string> ArrayValues(JToken? token) =>
        token is JArray array
            ? array.Select(v => v.Value<string>()).Where(v => v != null).Select(v => v!).ToList()
            : Enumerable.Empty<string>();

    // Pairs same-key entries of two objects, yielding (name, old, new) with null where absent.
    private static IEnumerable<(string Name, JObject? Old, JObject? New)> Pair(JObject? oldObj, JObject? newObj)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in Keys(oldObj)) names.Add(name);
        foreach (var name in Keys(newObj)) names.Add(name);

        foreach (var name in names)
        {
            yield return (name, oldObj?[name] as JObject, newObj?[name] as JObject);
        }
    }

    private static string Capitalize(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value.Substring(1);
}
