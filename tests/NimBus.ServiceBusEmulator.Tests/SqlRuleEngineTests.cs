#pragma warning disable CA1707, CA2007

using NimBus.ServiceBusEmulator.Broker;

namespace NimBus.ServiceBusEmulator.Tests;

[TestClass]
public sealed class SqlRuleEngineTests
{
    [TestMethod]
    public void Parser_supports_the_complete_NimBus_filter_and_action_subset()
    {
        var rule = SqlRuleEngine.Compile(
            "user.To = 'Deferred' AND user.OriginalSessionId IS NOT NULL",
            "SET user.From = 'publisher'; SET user.EventId = newid(); SET user.To = 'consumer';");
        var properties = new Dictionary<string, object?>
        {
            ["To"] = "Deferred",
            ["OriginalSessionId"] = "session-1",
        };

        Assert.IsTrue(rule.IsMatch(properties));
        rule.Apply(properties);

        Assert.AreEqual("publisher", properties["From"]);
        Assert.AreEqual("consumer", properties["To"]);
        Assert.IsInstanceOfType<Guid>(properties["EventId"]);
    }

    [TestMethod]
    [DataRow("1=0")]
    [DataRow("1 = 0")]
    public void False_filter_matches_nothing(string expression)
    {
        var rule = SqlRuleEngine.Compile(expression, null);

        Assert.IsFalse(rule.IsMatch(new Dictionary<string, object?>()));
        Assert.IsFalse(rule.IsMatch(new Dictionary<string, object?> { ["To"] = "anyone" }));
    }

    [TestMethod]
    [DataRow("01", true)]
    [DataRow("02", true)]
    [DataRow("03", false)]
    [DataRow(null, false)]
    public void In_list_matches_listed_values_only(string? key, bool expected)
    {
        var rule = SqlRuleEngine.Compile("user.RoutingKey IN ('01', '02')", null);

        Assert.AreEqual(expected, rule.IsMatch(Properties(key)));
    }

    [TestMethod]
    [DataRow("01", false)]
    [DataRow("03", true)]
    [DataRow(null, false)]
    public void Not_in_list_is_false_for_a_missing_property(string? key, bool expected)
    {
        var rule = SqlRuleEngine.Compile("user.RoutingKey NOT IN ('01', '02')", null);

        Assert.AreEqual(expected, rule.IsMatch(Properties(key)));
    }

    [TestMethod]
    [DataRow("01", false)]
    [DataRow("02", false)]
    [DataRow("99", true)]
    [DataRow(null, true)]
    public void Unmatched_forward_filter_takes_unclaimed_and_missing_keys(string? key, bool expected)
    {
        var rule = SqlRuleEngine.Compile(
            "user.EventTypeId = 'CustomerCreated' AND user.MessageType = 'EventRequest' AND user.From IS NULL" +
            " AND (user.RoutingKey IS NULL OR user.RoutingKey NOT IN ('01', '02'))",
            null);
        var properties = Properties(key);
        properties["EventTypeId"] = "CustomerCreated";
        properties["MessageType"] = "EventRequest";

        Assert.AreEqual(expected, rule.IsMatch(properties));
    }

    [TestMethod]
    public void In_list_compares_ordinally()
    {
        var rule = SqlRuleEngine.Compile("user.RoutingKey = 'DK'", null);

        Assert.IsFalse(rule.IsMatch(Properties("dk")));
        Assert.IsTrue(rule.IsMatch(Properties("DK")));
    }

    private static Dictionary<string, object?> Properties(string? routingKey)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (routingKey is not null)
            properties["RoutingKey"] = routingKey;
        return properties;
    }

    [TestMethod]
    public void Parser_rejects_unsupported_syntax_at_rule_creation()
    {
        Assert.ThrowsExactly<FormatException>(() => SqlRuleEngine.Compile("user.Value LIKE '%x%'", null));
    }
}
