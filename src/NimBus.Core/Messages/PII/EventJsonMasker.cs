using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace NimBus.Core.Messages.PII;

/// <summary>
/// Masks <see cref="SensitiveAttribute"/>-annotated leaves in a serialized event payload,
/// leaving every other field readable. The event type id is resolved to its CLR type via
/// <see cref="IPlatform.EventTypes"/>; a type that cannot be resolved fails closed.
/// </summary>
public class EventJsonMasker : IEventJsonMasker, IEventJsonRedactor
{
    public const string UnknownTypeMarker = "[REDACTED:unknown-type]";
    public const string InvalidJsonMarker = "[REDACTED:invalid-json]";
    public const string DefaultRedactToken = "***";

    // Sidecar property added to the root of any JSON the masker has actually masked at least
    // one sensitive leaf in. ContainsRedactPlaceholder reads this to detect masked-and-resubmitted
    // payloads regardless of which MaskMode was used (Redact / PartialReveal / Hash).
    public const string PiiMaskedMarker = "$piiMasked";

    private readonly IPlatform _platform;
    private readonly string _hashSalt;
    private readonly DefaultContractResolver _contractResolver = new DefaultContractResolver();
    private readonly Lazy<Dictionary<string, Type>> _typeCache;

    public EventJsonMasker(IPlatform platform) : this(platform, hashSalt: string.Empty) { }

    public EventJsonMasker(IPlatform platform, string hashSalt)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _hashSalt = hashSalt ?? string.Empty;
        _typeCache = new Lazy<Dictionary<string, Type>>(BuildTypeCache, LazyThreadSafetyMode.ExecutionAndPublication);
        ValidateAttributes();
    }

    public string Mask(string eventTypeId, string eventJson)
    {
        if (string.IsNullOrEmpty(eventJson))
        {
            return eventJson;
        }

        var clrType = ResolveType(eventTypeId);
        if (clrType == null)
        {
            return UnknownTypeMarker;
        }

        JObject root;
        try
        {
            root = JObject.Parse(eventJson);
        }
        catch (JsonException)
        {
            return InvalidJsonMarker;
        }

        bool maskedAny = false;
        MaskObject(root, clrType, inheritedSensitive: null, ref maskedAny);
        if (maskedAny)
        {
            root[PiiMaskedMarker] = true;
        }
        return root.ToString(Formatting.None);
    }

    /// <inheritdoc />
    public string Redact(string eventTypeId, string eventJson)
    {
        if (string.IsNullOrEmpty(eventJson))
        {
            return eventJson;
        }

        var clrType = ResolveType(eventTypeId);
        if (clrType == null)
        {
            return UnknownTypeMarker;
        }

        JObject root;
        try
        {
            root = JObject.Parse(eventJson);
        }
        catch (JsonException)
        {
            return InvalidJsonMarker;
        }

        var redactedAny = false;
        RedactObject(root, clrType, inheritedSensitive: null, ref redactedAny);
        if (redactedAny)
        {
            root[PiiMaskedMarker] = true;
        }

        return root.ToString(Formatting.None);
    }

    public bool ContainsRedactPlaceholder(string eventTypeId, string eventJson)
    {
        if (string.IsNullOrEmpty(eventJson)) return false;

        JObject root;
        try { root = JObject.Parse(eventJson); }
        catch (JsonException) { return false; }

        // Primary detection: the sidecar marker. Catches all three MaskMode values.
        var marker = root[PiiMaskedMarker];
        if (marker?.Type == JTokenType.Boolean && (bool)marker)
        {
            return true;
        }

        // Defense in depth: even if a client stripped the marker, the Redact token "***"
        // left in any [Sensitive] leaf is still detectable. PartialReveal/Hash with stripped
        // marker can slip through here — the resubmit gate caller is expected to fail-closed
        // on unresolvable types as a separate guard.
        var clrType = ResolveType(eventTypeId);
        if (clrType == null) return false;
        return ScanForRedactToken(root, clrType, inheritedSensitive: null);
    }

    public string StripMaskedMarker(string eventJson)
    {
        if (string.IsNullOrEmpty(eventJson)) return eventJson;
        try
        {
            var root = JObject.Parse(eventJson);
            var prop = root.Property(PiiMaskedMarker);
            if (prop == null) return eventJson;
            prop.Remove();
            return root.ToString(Formatting.None);
        }
        catch (JsonException)
        {
            return eventJson;
        }
    }

    public bool TryCollectSensitiveValues(string eventTypeId, string eventJson, [NotNullWhen(true)] out IReadOnlyCollection<string>? values)
    {
        if (string.IsNullOrEmpty(eventJson))
        {
            values = Array.Empty<string>();
            return true;
        }

        var clrType = ResolveType(eventTypeId);
        if (clrType == null)
        {
            values = null;
            return false;
        }

        JObject root;
        try
        {
            root = JObject.Parse(eventJson);
        }
        catch (JsonException)
        {
            values = null;
            return false;
        }

        var collected = new List<string>();
        CollectSensitiveValues(root, clrType, inheritedSensitive: null, collected);
        values = collected;
        return true;
    }

    private void CollectSensitiveValues(JObject obj, Type clrType, SensitiveAttribute? inheritedSensitive, List<string> collected)
    {
        if (obj == null || clrType == null) return;

        var classAttr = inheritedSensitive ?? clrType.GetCustomAttribute<SensitiveAttribute>(inherit: true);
        JsonObjectContract? contract = null;
        try { contract = _contractResolver.ResolveContract(clrType) as JsonObjectContract; }
        catch { contract = null; }

        foreach (var jProp in obj.Properties())
        {
            if (jProp.Name == PiiMaskedMarker) continue;

            var jsonProp = contract?.Properties.GetClosestMatchProperty(jProp.Name);
            PropertyInfo? clrProp = null;
            if (jsonProp?.UnderlyingName != null)
            {
                clrProp = clrType.GetProperty(jsonProp.UnderlyingName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            }
            if (clrProp == null)
            {
                clrProp = clrType.GetProperty(jProp.Name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            }

            var propAttr = clrProp?.GetCustomAttribute<SensitiveAttribute>(inherit: true);
            var effective = propAttr ?? classAttr;

            if (effective != null)
            {
                CollectLeafValues(jProp.Value, collected);
            }
            else if (clrProp != null)
            {
                if (jProp.Value is JObject nested)
                {
                    CollectSensitiveValues(nested, clrProp.PropertyType, inheritedSensitive: null, collected);
                }
                else if (jProp.Value is JArray array)
                {
                    var elementType = GetEnumerableElementType(clrProp.PropertyType);
                    if (elementType != null)
                    {
                        foreach (var item in array.OfType<JObject>())
                        {
                            CollectSensitiveValues(item, elementType, inheritedSensitive: null, collected);
                        }
                    }
                }
            }
        }
    }

    private static void CollectLeafValues(JToken token, List<string> collected)
    {
        if (token == null || token.Type == JTokenType.Null) return;

        if (token is JObject obj)
        {
            foreach (var child in obj.Properties())
            {
                CollectLeafValues(child.Value, collected);
            }
            return;
        }

        if (token is JArray arr)
        {
            foreach (var item in arr)
            {
                CollectLeafValues(item, collected);
            }
            return;
        }

        var raw = token.Type == JTokenType.String ? (string?)token : token.ToString(Formatting.None);
        if (!string.IsNullOrEmpty(raw))
        {
            collected.Add(raw);
        }
    }

    private bool ScanForRedactToken(JObject obj, Type clrType, SensitiveAttribute? inheritedSensitive)
    {
        if (obj == null || clrType == null) return false;
        var classAttr = inheritedSensitive ?? clrType.GetCustomAttribute<SensitiveAttribute>(inherit: true);
        JsonObjectContract? contract = null;
        try { contract = _contractResolver.ResolveContract(clrType) as JsonObjectContract; }
        catch { contract = null; }

        foreach (var jProp in obj.Properties())
        {
            var jsonProp = contract?.Properties.GetClosestMatchProperty(jProp.Name);
            PropertyInfo? clrProp = null;
            if (jsonProp?.UnderlyingName != null)
            {
                clrProp = clrType.GetProperty(jsonProp.UnderlyingName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            }
            if (clrProp == null)
            {
                clrProp = clrType.GetProperty(jProp.Name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            }

            var propAttr = clrProp?.GetCustomAttribute<SensitiveAttribute>(inherit: true);
            var effective = propAttr ?? classAttr;

            if (effective != null && TokenContainsRedact(jProp.Value))
            {
                return true;
            }

            if (clrProp != null)
            {
                if (jProp.Value is JObject nested && ScanForRedactToken(nested, clrProp.PropertyType, effective))
                {
                    return true;
                }
                if (jProp.Value is JArray arr)
                {
                    var elementType = GetEnumerableElementType(clrProp.PropertyType);
                    if (elementType != null)
                    {
                        foreach (var item in arr.OfType<JObject>())
                        {
                            if (ScanForRedactToken(item, elementType, effective)) return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    private static bool TokenContainsRedact(JToken token)
    {
        if (token == null) return false;
        if (token.Type == JTokenType.String && (string?)token == DefaultRedactToken) return true;
        if (token is JObject o)
        {
            foreach (var c in o.Properties())
            {
                if (TokenContainsRedact(c.Value)) return true;
            }
        }
        if (token is JArray a)
        {
            foreach (var c in a)
            {
                if (TokenContainsRedact(c)) return true;
            }
        }
        return false;
    }

    private Dictionary<string, Type> BuildTypeCache()
    {
        return _platform.EventTypes
            .Where(et => et != null && !string.IsNullOrEmpty(et.Id) && et.GetEventClassType() != null)
            .GroupBy(et => et.Id)
            .ToDictionary(g => g.Key, g => g.First().GetEventClassType());
    }

    private Type? ResolveType(string eventTypeId)
    {
        if (string.IsNullOrEmpty(eventTypeId))
        {
            return null;
        }

        _typeCache.Value.TryGetValue(eventTypeId, out var type);
        return type;
    }

    private void ValidateAttributes()
    {
        var seen = new HashSet<Type>();
        foreach (var et in _platform.EventTypes)
        {
            var t = et?.GetEventClassType();
            if (t == null) continue;
            ValidateType(t, seen);
        }
    }

    private static void ValidateType(Type t, HashSet<Type> seen)
    {
        if (t == null || !seen.Add(t)) return;
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = p.GetCustomAttribute<SensitiveAttribute>(inherit: true);
            if (attr != null && attr.Mode == MaskMode.PartialReveal && attr.Reveal <= 0)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[Sensitive] on {t.FullName}.{p.Name} uses PartialReveal but Reveal={attr.Reveal}. " +
                    $"PartialReveal requires Reveal > 0; otherwise use MaskMode.Redact explicitly."));
            }

            var propType = p.PropertyType;
            if (propType == typeof(string)) continue;
            if (propType.IsClass)
            {
                ValidateType(propType, seen);
            }
            var element = GetEnumerableElementType(propType);
            if (element != null && element != typeof(object))
            {
                ValidateType(element, seen);
            }
        }
    }

    private void MaskObject(JObject obj, Type clrType, SensitiveAttribute? inheritedSensitive, ref bool maskedAny)
    {
        if (obj == null || clrType == null)
        {
            return;
        }

        var classAttr = inheritedSensitive ?? clrType.GetCustomAttribute<SensitiveAttribute>(inherit: true);
        JsonObjectContract? contract = null;
        try
        {
            contract = _contractResolver.ResolveContract(clrType) as JsonObjectContract;
        }
        catch
        {
            contract = null;
        }

        foreach (var jProp in obj.Properties().ToList())
        {
            // Don't recurse into our own sidecar marker if it's already present (idempotent re-mask).
            if (jProp.Name == PiiMaskedMarker) continue;

            var jsonProp = contract?.Properties.GetClosestMatchProperty(jProp.Name);
            PropertyInfo? clrProp = null;
            if (jsonProp?.UnderlyingName != null)
            {
                clrProp = clrType.GetProperty(jsonProp.UnderlyingName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            }
            if (clrProp == null)
            {
                clrProp = clrType.GetProperty(jProp.Name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            }

            var propAttr = clrProp?.GetCustomAttribute<SensitiveAttribute>(inherit: true);
            var effective = propAttr ?? classAttr;

            if (effective != null)
            {
                ApplyMaskRecursive(jProp, effective, ref maskedAny);
            }
            else if (clrProp != null)
            {
                var propType = clrProp.PropertyType;
                if (jProp.Value is JObject nested)
                {
                    MaskObject(nested, propType, inheritedSensitive: null, ref maskedAny);
                }
                else if (jProp.Value is JArray array)
                {
                    var elementType = GetEnumerableElementType(propType);
                    if (elementType != null)
                    {
                        foreach (var item in array.OfType<JObject>())
                        {
                            MaskObject(item, elementType, inheritedSensitive: null, ref maskedAny);
                        }
                    }
                }
            }
        }
    }

    private void RedactObject(JObject obj, Type clrType, SensitiveAttribute? inheritedSensitive, ref bool redactedAny)
    {
        if (obj == null || clrType == null)
        {
            return;
        }

        var classAttr = inheritedSensitive ?? clrType.GetCustomAttribute<SensitiveAttribute>(inherit: true);
        JsonObjectContract? contract = null;
        try
        {
            contract = _contractResolver.ResolveContract(clrType) as JsonObjectContract;
        }
        catch
        {
            contract = null;
        }

        foreach (var jProp in obj.Properties().ToList())
        {
            if (jProp.Name == PiiMaskedMarker)
            {
                continue;
            }

            var jsonProp = contract?.Properties.GetClosestMatchProperty(jProp.Name);
            var clrProp = jsonProp?.UnderlyingName is { } underlyingName
                ? clrType.GetProperty(underlyingName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                : null;
            clrProp ??= clrType.GetProperty(jProp.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            var propAttr = clrProp?.GetCustomAttribute<SensitiveAttribute>(inherit: true);
            var effective = propAttr ?? classAttr;
            if (effective != null)
            {
                jProp.Value = RedactToken(jProp.Value, ref redactedAny);
            }
            else if (clrProp != null)
            {
                if (jProp.Value is JObject nested)
                {
                    RedactObject(nested, clrProp.PropertyType, inheritedSensitive: null, ref redactedAny);
                }
                else if (jProp.Value is JArray array)
                {
                    var elementType = GetEnumerableElementType(clrProp.PropertyType);
                    if (elementType != null)
                    {
                        foreach (var item in array.OfType<JObject>())
                        {
                            RedactObject(item, elementType, inheritedSensitive: null, ref redactedAny);
                        }
                    }
                }
            }
        }
    }

    [return: NotNullIfNotNull(nameof(token))]
    private static JToken? RedactToken(JToken? token, ref bool redactedAny)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return token;
        }

        if (token is JObject obj)
        {
            foreach (var child in obj.Properties().ToList())
            {
                child.Value = RedactToken(child.Value, ref redactedAny);
            }

            return obj;
        }

        if (token is JArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                array[i] = RedactToken(array[i], ref redactedAny);
            }

            return array;
        }

        redactedAny = true;
        return new JValue(DefaultRedactToken);
    }

    private void ApplyMaskRecursive(JProperty jProp, SensitiveAttribute attr, ref bool maskedAny)
    {
        jProp.Value = MaskToken(jProp.Value, attr, ref maskedAny);
    }

    [return: NotNullIfNotNull(nameof(token))]
    private JToken? MaskToken(JToken? token, SensitiveAttribute attr, ref bool maskedAny)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return token;
        }

        if (token is JObject obj)
        {
            foreach (var child in obj.Properties().ToList())
            {
                child.Value = MaskToken(child.Value, attr, ref maskedAny);
            }
            return obj;
        }

        if (token is JArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                arr[i] = MaskToken(arr[i], attr, ref maskedAny);
            }
            return arr;
        }

        // Leaf value with a non-null sensitive attribute — mark and mask.
        maskedAny = true;
        var raw = token.Type == JTokenType.String ? (string?)token : token.ToString(Formatting.None);
        return new JValue(MaskString(raw, attr));
    }

    [return: NotNullIfNotNull(nameof(value))]
    private string? MaskString(string? value, SensitiveAttribute attr)
    {
        if (value == null)
        {
            return null;
        }

        switch (attr.Mode)
        {
            case MaskMode.PartialReveal:
                if (attr.Reveal > 0 && value.Length > attr.Reveal)
                {
                    return new string('*', value.Length - attr.Reveal) + value.Substring(value.Length - attr.Reveal);
                }
                return DefaultRedactToken;

            case MaskMode.Hash:
                return HashWithSalt(value);

            case MaskMode.Redact:
            default:
                return DefaultRedactToken;
        }
    }

    /// <summary>
    /// Produces a deterministic pseudonymous token for log correlation: same input + same salt
    /// yields the same output. NOT a MAC — the salt is not assumed secret, so this hash must
    /// not be used to authenticate values. Suitable only for pseudonymization / correlation.
    /// </summary>
    private string HashWithSalt(string value)
    {
        using (var sha = SHA256.Create())
        {
            var bytes = Encoding.UTF8.GetBytes(_hashSalt + value);
            var hash = sha.ComputeHash(bytes);
            var sb = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }

    private static Type? GetEnumerableElementType(Type type)
    {
        if (type == null || type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        foreach (var iface in new[] { type }.Concat(type.GetInterfaces()))
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return iface.GetGenericArguments()[0];
            }
        }

        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            return typeof(object);
        }

        return null;
    }
}
