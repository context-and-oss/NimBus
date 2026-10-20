using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NimBus.Validation.Attributes;

public abstract class CustomValidationAttribute : ValidationAttribute
{
    public CustomValidationAttribute(string errorMessage) : base(errorMessage) { }

    public abstract override bool IsValid(object? value);

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value == null || IsValid(value))
        {
            return null;
        }
        return new ValidationResult(FormatErrorMessage(validationContext.MemberName!));
    }
}

public class PhoneNumberAttribute : CustomValidationAttribute
{
    public PhoneNumberAttribute() : base("The field {0} must be a string that starts with '+'.") { }

    public override bool IsValid(object? value)
    {
        if (!(value is string s))
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(s))
        {
            return s.StartsWith('+');
        }

        return true;
    }
}

public class NonEmptyGuidAttribute : CustomValidationAttribute
{
    public NonEmptyGuidAttribute() : base("The field {0} must not be an empty Guid.") { }

    public override bool IsValid(object? value)
    {
        if (!(value is Guid guid))
            return true;

        return !guid.Equals(Guid.Empty);
    }
}

public class NonEmptyAttribute : CustomValidationAttribute
{
    public NonEmptyAttribute() : base("The field {0} must be a non-empty Guid.") { }

    public override bool IsValid(object? value)
    {
        if (!(value is String s))
            return false;

        return !string.IsNullOrWhiteSpace(s);
    }
}

public class EnsureOneElementAttribute : CustomValidationAttribute
{
    public EnsureOneElementAttribute() : base("The field {0} must be a non-empty List.") { }

    public override bool IsValid(object? value)
    {
        if (value is ICollection list)
        {
            return list.Count > 0;
        }
        return false;
    }
}

public class RequiredAllowEmptyString : ValidationAttribute
{
    public RequiredAllowEmptyString() : base("The field {0} must be a non-null String.") { }

    public override bool IsValid(object? value)
    {
        if (value == null)
        {
            return false;
        }
        return true;
    }
}

/// <summary>
/// Specify a field as required only when other fields are equal to specific values
/// </summary>
public class RequiredIfAttribute : ValidationAttribute
{
    private readonly List<KeyValuePair<string, string>> dependentProperties;

    public RequiredIfAttribute(string[] dependencies)
        : base("The field {0} is required due to unfulfilled dependencies")
    {
        dependentProperties = new List<KeyValuePair<string, string>>();
        for (var i = 0; i < dependencies.Length - 1; i += 2)
        {
            dependentProperties.Add(new KeyValuePair<string, string>(dependencies[i], dependencies[i + 1]));
        }
    }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        object instance = validationContext.ObjectInstance;
        Type type = instance.GetType();

        bool isRequired = false;
        string depMsg = string.Empty;
        foreach (var kv in dependentProperties)
        {
            var k = kv.Key;
            var v = kv.Value;
            object? propertyValue = type.GetProperty(k)?.GetValue(instance, null);

            if (propertyValue?.ToString() == v?.ToString())
            {
                isRequired = true;
                depMsg = $"{k} == {v}";
            }
        }

        if (isRequired && value == null)
        {
            return new ValidationResult($"The field '{validationContext.DisplayName}' is required due to dependency: '{depMsg}'");
        }
        return ValidationResult.Success;
    }
}
