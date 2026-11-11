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
    public void Parser_rejects_unsupported_syntax_at_rule_creation()
    {
        Assert.ThrowsExactly<FormatException>(() => SqlRuleEngine.Compile("user.Value LIKE '%x%'", null));
    }
}
