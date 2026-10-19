using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Map = System.Collections.Generic.Dictionary<string, object>;

namespace NimBus.ServiceBus.AsyncApi;

/// <summary>
/// Reflection-based JSON Schema generation from CLR event contracts, shared by the AsyncAPI
/// exporter (schemas under <c>#/components/schemas/</c>) and the EventCatalog exporter
/// (standalone per-message <c>schema.json</c> files with nested types under <c>#/$defs/</c>).
/// </summary>
internal static class JsonSchemaBuilder
{
    internal const string ComponentsRefPrefix = "#/components/schemas/";
    private const string DefsRefPrefix = "#/$defs/";

    internal static void EnsureObjectSchema(Type type, Map schemas, HashSet<Type> building, string refPrefix = ComponentsRefPrefix)
    {
        var name = type.Name;
        if (schemas.ContainsKey(name) || building.Contains(type)) return;

        building.Add(type);
        schemas[name] = BuildObjectSchema(type, schemas, building, refPrefix);
    }

    internal static Map BuildObjectSchema(Type type, Map schemas, HashSet<Type> building, string refPrefix = ComponentsRefPrefix)
    {
        var properties = new Map();
        var required = new List<object>();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .OrderBy(p => p.MetadataToken))
        {
            var node = MapType(property.PropertyType, schemas, building, refPrefix);

            var description = property.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (description != null) node["description"] = description;

            var range = property.GetCustomAttribute<RangeAttribute>();
            if (range != null)
            {
                node["minimum"] = range.Minimum;
                node["maximum"] = range.Maximum;
            }

            var propertyName = ToCamelCase(property.Name);
            properties[propertyName] = node;
            if (IsRequired(property)) required.Add(propertyName);
        }

        var schema = new Map { ["type"] = "object" };
        var typeDescription = type.GetCustomAttribute<DescriptionAttribute>()?.Description;
        if (typeDescription != null) schema["description"] = typeDescription;
        if (required.Count > 0) schema["required"] = required;
        schema["properties"] = properties;
        return schema;
    }

    internal static Map MapType(Type type, Map schemas, HashSet<Type> building, string refPrefix = ComponentsRefPrefix)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;

        if (t == typeof(string) || t == typeof(char)) return new Map { ["type"] = "string" };
        if (t == typeof(Guid)) return new Map { ["type"] = "string", ["format"] = "uuid" };
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return new Map { ["type"] = "string", ["format"] = "date-time" };
        if (t == typeof(TimeSpan) || t == typeof(Uri)) return new Map { ["type"] = "string" };
        if (t == typeof(bool)) return new Map { ["type"] = "boolean" };
        if (t.IsEnum) return new Map { ["type"] = "string", ["enum"] = Enum.GetNames(t).Cast<object>().ToList() };
        if (IsInteger(t)) return new Map { ["type"] = "integer", ["format"] = (t == typeof(long) || t == typeof(ulong)) ? "int64" : "int32" };
        if (IsNumber(t)) return new Map { ["type"] = "number" };

        var element = GetEnumerableElementType(t);
        if (element != null)
        {
            return new Map { ["type"] = "array", ["items"] = MapType(element, schemas, building, refPrefix) };
        }

        if (t.IsClass || (t.IsValueType && !t.IsPrimitive))
        {
            EnsureObjectSchema(t, schemas, building, refPrefix);
            return new Map { ["$ref"] = $"{refPrefix}{t.Name}" };
        }

        return new Map { ["type"] = "string" };
    }

    /// <summary>
    /// Builds a self-contained JSON Schema document for one event contract: the root schema is
    /// inlined and every nested complex type lands under <c>$defs</c> with <c>#/$defs/</c> refs,
    /// so the file stands alone (EventCatalog's <c>schemaPath</c>/<c>SchemaViewer</c> cannot
    /// resolve external refs). Self-referential root types are not supported.
    /// </summary>
    internal static string BuildStandaloneJson(Type type)
    {
        var defs = new Map();
        var building = new HashSet<Type> { type };
        var root = BuildObjectSchema(type, defs, building, DefsRefPrefix);
        if (defs.Count > 0) root["$defs"] = defs;
        return JsonConvert.SerializeObject(root, Formatting.Indented);
    }

    internal static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static bool IsInteger(Type t) =>
        t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) ||
        t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong);

    private static bool IsNumber(Type t) =>
        t == typeof(decimal) || t == typeof(double) || t == typeof(float);

    private static Type? GetEnumerableElementType(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();

        var enumerable = type.GetInterfaces()
            .Concat(type.IsInterface ? new[] { type } : Array.Empty<Type>())
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    private static bool IsRequired(PropertyInfo property)
    {
        if (property.GetCustomAttribute<RequiredAttribute>() != null) return true;

        // Non-nullable (value types, or NRT-annotated reference types) ⇒ required. Reference types
        // in nullable-oblivious assemblies report Unknown and are treated as optional.
        var nullability = new NullabilityInfoContext().Create(property);
        return nullability.ReadState == NullabilityState.NotNull;
    }
}
