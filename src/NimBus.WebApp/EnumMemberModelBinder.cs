using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace NimBus.WebApp;

public class EnumMemberModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        if (context.Metadata.ModelType.IsEnum &&
            context.Metadata.ModelType.GetFields().Any(f => f.GetCustomAttribute<EnumMemberAttribute>() != null))
        {
            return new EnumMemberModelBinder(context.Metadata.ModelType);
        }
        return null;
    }
}

public class EnumMemberModelBinder : IModelBinder
{
    private readonly Type _enumType;
    private readonly Dictionary<string, object> _valueMap;

    public EnumMemberModelBinder(Type enumType)
    {
        _enumType = enumType;

        // The binder is cached per model type by MVC, so reflect once here instead
        // of on every bind. Insertion order mirrors the original per-field scan
        // (EnumMember value before field name, fields in declaration order), so
        // first-match-wins semantics are preserved by TryAdd.
        _valueMap = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var enumValue = field.GetValue(null)!;
            var attr = field.GetCustomAttribute<EnumMemberAttribute>();
            if (attr?.Value != null)
                _valueMap.TryAdd(attr.Value, enumValue);
            _valueMap.TryAdd(field.Name, enumValue);
        }
    }

    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var value = bindingContext.ValueProvider.GetValue(bindingContext.ModelName).FirstValue;
        if (string.IsNullOrEmpty(value))
            return Task.CompletedTask;

        if (_valueMap.TryGetValue(value, out var mapped))
        {
            bindingContext.Result = ModelBindingResult.Success(mapped);
            return Task.CompletedTask;
        }

        if (Enum.TryParse(_enumType, value, ignoreCase: true, out var parsed))
        {
            bindingContext.Result = ModelBindingResult.Success(parsed);
            return Task.CompletedTask;
        }

        bindingContext.ModelState.AddModelError(bindingContext.ModelName, $"Invalid value '{value}' for {_enumType.Name}.");
        return Task.CompletedTask;
    }
}
