using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace NimBus.Core.Events;

public class Property : IProperty
{
    private readonly PropertyInfo _propertyInfo;

    public Property(PropertyInfo propertyInfo)
    {
        _propertyInfo = propertyInfo;
    }

    public string Name => _propertyInfo.Name;

    /// <summary>
    /// The property's CLR type rendered the way it is written in C#: <c>Guid?</c> rather
    /// than <c>Nullable`1</c>, <c>List&lt;String&gt;</c> rather than <c>List`1</c>,
    /// <c>String[]</c> for arrays. Non-generic types keep their plain name.
    /// </summary>
    public string TypeName => FormatTypeName(_propertyInfo.PropertyType, fullName: false);

    /// <summary>
    /// Same as <see cref="TypeName"/> but with namespace-qualified names, so a tooltip
    /// can disambiguate without showing assembly-qualified generic arguments.
    /// </summary>
    public string TypeFullName => FormatTypeName(_propertyInfo.PropertyType, fullName: true);

    public string? Description => _propertyInfo.GetCustomAttribute<DescriptionAttribute>()?.Description;

    public bool IsRequired => _propertyInfo.GetCustomAttribute<RequiredAttribute>() != null;

    /// <summary>
    /// Renders a type as C#-style source text. Shared by <see cref="TypeName"/> and
    /// <see cref="TypeFullName"/>; exposed for tests.
    /// </summary>
    public static string FormatTypeName(Type type, bool fullName)
    {
        ArgumentNullException.ThrowIfNull(type);

        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
        {
            return FormatTypeName(underlying, fullName) + "?";
        }

        if (type.IsArray)
        {
            var rank = type.GetArrayRank();
            var commas = rank > 1 ? new string(',', rank - 1) : string.Empty;
            return FormatTypeName(type.GetElementType()!, fullName) + "[" + commas + "]";
        }

        if (!type.IsGenericType)
        {
            return (fullName ? type.FullName : null) ?? type.Name;
        }

        // Open generic definitions have no FullName for their arguments; the
        // definition's own name is enough (List<T> rather than List`1).
        var raw = (fullName ? type.GetGenericTypeDefinition().FullName : null) ?? type.Name;
        var tick = raw.IndexOf('`');
        var head = tick >= 0 ? raw.Substring(0, tick) : raw;
        var args = type.GetGenericArguments().Select(a => a.IsGenericParameter ? a.Name : FormatTypeName(a, fullName));
        return head + "<" + string.Join(", ", args) + ">";
    }
}
