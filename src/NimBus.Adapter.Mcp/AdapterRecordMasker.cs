using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;

namespace NimBus.Adapter.Mcp;

/// <summary>
/// Masks a record's <see cref="SensitiveAttribute"/>-marked properties with NimBus's own
/// <see cref="EventJsonMasker"/>, so the modes, the redaction token, the hash and the walk into
/// nested objects and collections are exactly NimBus's. The masker resolves a CLR type through an
/// event type catalog; each record type is given a catalog of its own.
/// </summary>
internal sealed class AdapterRecordMasker(string? hashSalt)
{
    private static readonly JsonSerializerSettings Serialization = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Include,
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
    };

    private readonly ConcurrentDictionary<Type, EventJsonMasker> _maskers = new();
    private readonly string _hashSalt = hashSalt ?? string.Empty;

    /// <summary>The record as JSON with its sensitive values masked; null for no record.</summary>
    /// <exception cref="InvalidOperationException">The record type marks a property in a way NimBus refuses.</exception>
    public JsonNode? Mask(object? record)
    {
        switch (record)
        {
            case null:
                return null;
            // Already JSON: there is no type to read markers from, so nothing to mask.
            case JsonNode node:
                return node.DeepClone();
            case JsonElement element:
                return JsonNode.Parse(element.GetRawText());
            case JToken token:
                return JsonNode.Parse(token.ToString(Formatting.None));
            // The masker walks an object's own properties; a dictionary or a list of typed values is
            // masked value by value, so a record never escapes masking by being wrapped.
            case System.Collections.IDictionary dictionary:
            {
                var values = new JsonObject();
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                    values[Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty] = Mask(entry.Value);
                return values;
            }
            case System.Collections.IEnumerable items and not string:
                return new JsonArray([.. items.Cast<object?>().Select(Mask)]);
        }

        var type = record.GetType();
        var json = JsonConvert.SerializeObject(record, Serialization);
        // A scalar has no properties to mark; the masker reads only objects.
        if (!json.StartsWith('{'))
            return JsonNode.Parse(json);

        var masker = _maskers.GetOrAdd(type, static (recordType, salt) => new EventJsonMasker(new RecordCatalog(recordType), salt), _hashSalt);
        var masked = masker.StripMaskedMarker(masker.Mask(RecordCatalog.Id(type), json));
        return JsonNode.Parse(masked);
    }

    /// <summary>An event type catalog holding one record type, for <see cref="EventJsonMasker"/>.</summary>
    private sealed class RecordCatalog(Type recordType) : IPlatform
    {
        public static string Id(Type type) => type.Name;

        public IEnumerable<IEndpoint> Endpoints => [];

        public IEnumerable<IEventType> EventTypes { get; } = [new EventType(recordType)];

        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => [];

        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => [];
    }
}
