using System;
using System.IO;
using Newtonsoft.Json.Linq;
using YamlDotNet.Serialization;

namespace NimBus.CommandLine;

/// <summary>
/// Loads an AsyncAPI document (YAML or JSON) from disk or a string into a uniform
/// <see cref="JObject"/> model so the validator and diff can work on one representation.
/// </summary>
public static class AsyncApiDocumentLoader
{
    /// <summary>Reads and parses the file at <paramref name="path"/>. Format inferred from extension, else content.</summary>
    public static JObject LoadFile(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path must be specified.", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException($"AsyncAPI document not found: {path}", path);

        var text = File.ReadAllText(path);
        var asJson = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        return Parse(text, asJson);
    }

    /// <summary>Parses <paramref name="content"/> as JSON when <paramref name="asJson"/>, otherwise YAML.</summary>
    public static JObject Parse(string content, bool asJson)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Fall back to sniffing the first non-whitespace char when the caller can't say: a leading
        // '{' means JSON, anything else is treated as YAML (a superset that also parses JSON).
        if (!asJson)
        {
            var trimmed = content.TrimStart();
            asJson = trimmed.StartsWith('{');
        }

        if (asJson)
        {
            return (JObject)JToken.Parse(content);
        }

        // YAML → object graph → JSON-compatible text → JObject, so downstream code only ever
        // deals with the Newtonsoft model regardless of the on-disk format.
        var yamlObject = new DeserializerBuilder().Build().Deserialize<object>(content);
        var json = new SerializerBuilder().JsonCompatible().Build().Serialize(yamlObject);
        return (JObject)JToken.Parse(json);
    }
}
