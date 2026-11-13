using System.Text.RegularExpressions;

namespace NimBus.ServiceBusEmulator.Broker;

internal static partial class SqlRuleEngine
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static CompiledSqlRule Compile(string filterExpression, string? actionExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filterExpression);
        if (filterExpression.Length > 16_384 || actionExpression?.Length > 16_384)
        {
            throw new FormatException("SQL rule expressions cannot exceed 16 KiB.");
        }

        var predicates = SplitAnd(filterExpression).Select(ParsePredicate).ToArray();
        var actions = string.IsNullOrWhiteSpace(actionExpression)
            ? []
            : actionExpression.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseAction)
                .ToArray();

        return new CompiledSqlRule(predicates, actions);
    }

    private static List<string> SplitAnd(string expression)
    {
        var result = new List<string>();
        var start = 0;
        var inString = false;
        for (var index = 0; index < expression.Length; index++)
        {
            if (expression[index] == '\'')
            {
                if (inString && index + 1 < expression.Length && expression[index + 1] == '\'')
                {
                    index++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (!inString && index + 5 <= expression.Length &&
                expression.AsSpan(index, 5).Equals(" AND ", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(expression[start..index].Trim());
                start = index + 5;
                index += 4;
            }
        }

        if (inString)
        {
            throw new FormatException("Unterminated SQL string literal.");
        }

        result.Add(expression[start..].Trim());
        return result;
    }

    private static Func<IDictionary<string, object?>, bool> ParsePredicate(string expression)
    {
        if (Regex.IsMatch(expression, "^1\\s*=\\s*1$", RegexOptions.CultureInvariant, RegexTimeout))
        {
            return static _ => true;
        }

        // The SDK's FalseRuleFilter: a subscription is created closed with $Default = 1=0.
        if (Regex.IsMatch(expression, "^1\\s*=\\s*0$", RegexOptions.CultureInvariant, RegexTimeout))
        {
            return static _ => false;
        }

        var nullMatch = Regex.Match(
            expression,
            "^user\\.([A-Za-z_][A-Za-z0-9_]*)\\s+IS\\s+(NOT\\s+)?NULL$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
        if (nullMatch.Success)
        {
            var key = nullMatch.Groups[1].Value;
            var negate = nullMatch.Groups[2].Success;
            return properties =>
            {
                var isNull = !properties.TryGetValue(key, out var value) || value is null;
                return negate ? !isNull : isNull;
            };
        }

        var equality = Regex.Match(
            expression,
            "^user\\.([A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*'((?:''|[^'])*)'$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
        if (equality.Success)
        {
            var key = equality.Groups[1].Value;
            var expected = equality.Groups[2].Value.Replace("''", "'", StringComparison.Ordinal);
            return properties => properties.TryGetValue(key, out var value) &&
                string.Equals(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), expected, StringComparison.Ordinal);
        }

        // Spec 045: `user.X IN ('a', 'b')` and `user.X NOT IN (...)`. NOT IN on a missing
        // property is false, as SQL-92 defines it; NimBus pairs it with an IS NULL branch.
        var inList = Regex.Match(
            expression,
            "^user\\.([A-Za-z_][A-Za-z0-9_]*)\\s+(NOT\\s+)?IN\\s*\\(((?:\\s*'(?:''|[^'])*'\\s*,)*\\s*'(?:''|[^'])*'\\s*)\\)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
        if (inList.Success)
        {
            var key = inList.Groups[1].Value;
            var negate = inList.Groups[2].Success;
            var values = Regex.Matches(inList.Groups[3].Value, "'((?:''|[^'])*)'", RegexOptions.CultureInvariant, RegexTimeout)
                .Select(match => match.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);
            return properties =>
            {
                if (!properties.TryGetValue(key, out var value) || value is null)
                    return false;
                var contained = values.Contains(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                return negate ? !contained : contained;
            };
        }

        // Spec 045: one parenthesised group of predicates joined by OR.
        if (expression.Length > 2 && expression[0] == '(' && expression[^1] == ')')
        {
            var alternatives = SplitOutsideStrings(expression[1..^1], " OR ").Select(ParsePredicate).ToArray();
            if (alternatives.Length > 1)
            {
                return properties => alternatives.Any(alternative => alternative(properties));
            }
        }

        throw new FormatException($"Unsupported SQL filter expression '{expression}'.");
    }

    private static List<string> SplitOutsideStrings(string expression, string separator)
    {
        var result = new List<string>();
        var start = 0;
        var inString = false;
        for (var index = 0; index < expression.Length; index++)
        {
            if (expression[index] == '\'')
            {
                inString = !inString;
                continue;
            }

            if (!inString && index + separator.Length <= expression.Length &&
                expression.AsSpan(index, separator.Length).Equals(separator, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(expression[start..index].Trim());
                start = index + separator.Length;
                index += separator.Length - 1;
            }
        }

        result.Add(expression[start..].Trim());
        return result;
    }

    private static Action<IDictionary<string, object?>> ParseAction(string expression)
    {
        var guidAction = Regex.Match(
            expression,
            "^SET\\s+user\\.([A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*newid\\(\\)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
        if (guidAction.Success)
        {
            var key = guidAction.Groups[1].Value;
            return properties => properties[key] = Guid.NewGuid();
        }

        var stringAction = Regex.Match(
            expression,
            "^SET\\s+user\\.([A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*'((?:''|[^'])*)'$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
        if (stringAction.Success)
        {
            var key = stringAction.Groups[1].Value;
            var value = stringAction.Groups[2].Value.Replace("''", "'", StringComparison.Ordinal);
            return properties => properties[key] = value;
        }

        throw new FormatException($"Unsupported SQL rule action '{expression}'.");
    }
}

internal sealed class CompiledSqlRule(
    IReadOnlyList<Func<IDictionary<string, object?>, bool>> predicates,
    IReadOnlyList<Action<IDictionary<string, object?>>> actions)
{
    public bool HasActions => actions.Count > 0;

    public bool IsMatch(IDictionary<string, object?> properties) => predicates.All(predicate => predicate(properties));

    public void Apply(IDictionary<string, object?> properties)
    {
        foreach (var action in actions)
        {
            action(properties);
        }
    }
}
