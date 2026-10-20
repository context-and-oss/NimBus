using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace NimBus.Core.Messages.PII;

/// <summary>
/// Answers "can payloads of this event type ever carry sensitive data?" by
/// walking the CLR type for <see cref="SensitiveAttribute"/> markers — the
/// same class/property/nested-object/collection-element walk
/// <see cref="EventJsonMasker"/> performs when masking, so the two always
/// agree on what counts as sensitive.
/// </summary>
public static class SensitiveTypeInspector
{
    private static readonly ConcurrentDictionary<Type, bool> Cache = new ConcurrentDictionary<Type, bool>();

    public static bool ContainsSensitiveData(Type type)
    {
        if (type == null)
        {
            return false;
        }

        return Cache.GetOrAdd(type, t => Inspect(t, new HashSet<Type>()));
    }

    private static bool Inspect(Type type, HashSet<Type> seen)
    {
        if (type == null || type == typeof(string) || !seen.Add(type))
        {
            return false;
        }

        if (type.GetCustomAttribute<SensitiveAttribute>(inherit: true) != null)
        {
            return true;
        }

        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetCustomAttribute<SensitiveAttribute>(inherit: true) != null)
            {
                return true;
            }

            var propType = p.PropertyType;
            if (propType != typeof(string) && propType.IsClass && Inspect(propType, seen))
            {
                return true;
            }

            var element = GetEnumerableElementType(propType);
            if (element != null && element != typeof(object) && Inspect(element, seen))
            {
                return true;
            }
        }

        return false;
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
