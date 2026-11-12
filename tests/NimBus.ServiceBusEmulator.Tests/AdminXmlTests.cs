#pragma warning disable CA1707, CA2007

using System.Xml.Linq;
using NimBus.ServiceBusEmulator.Admin;
using NimBus.ServiceBusEmulator.Broker;

namespace NimBus.ServiceBusEmulator.Tests;

[TestClass]
public sealed class AdminXmlTests
{
    private const string Atom = "http://www.w3.org/2005/Atom";
    private const string ServiceBus = "http://schemas.microsoft.com/netservices/2010/10/servicebus/connect";
    private const string SchemaInstance = "http://www.w3.org/2001/XMLSchema-instance";

    [TestMethod]
    public void ParseDefaultRule_reads_a_sql_filter_its_action_and_its_name()
    {
        var document = SubscriptionCreate("""
            <DefaultRuleDescription>
              <Filter i:type="SqlFilter"><SqlExpression>user.To = 'orders'</SqlExpression></Filter>
              <Action i:type="SqlRuleAction"><SqlExpression>SET user.From = 'x'</SqlExpression></Action>
              <Name>initial</Name>
            </DefaultRuleDescription>
            """);

        Assert.AreEqual(new RuleDefinition("initial", "user.To = 'orders'", "SET user.From = 'x'"), AdminXml.ParseDefaultRule(document));
    }

    [TestMethod]
    [DataRow("FalseFilter", "1=0")]
    [DataRow("TrueFilter", "1=1")]
    public void ParseDefaultRule_maps_the_constant_filters(string filterType, string expected)
    {
        var document = SubscriptionCreate($"""
            <DefaultRuleDescription>
              <Filter i:type="{filterType}"><SqlExpression>{expected}</SqlExpression></Filter>
              <Action i:type="EmptyRuleAction" />
              <Name>$Default</Name>
            </DefaultRuleDescription>
            """);

        Assert.AreEqual(new RuleDefinition("$Default", expected), AdminXml.ParseDefaultRule(document));
    }

    [TestMethod]
    public void ParseDefaultRule_maps_a_false_filter_without_an_expression()
    {
        var document = SubscriptionCreate("""
            <DefaultRuleDescription><Filter i:type="FalseFilter" /><Name>$Default</Name></DefaultRuleDescription>
            """);

        Assert.AreEqual("1=0", AdminXml.ParseDefaultRule(document)?.FilterExpression);
    }

    [TestMethod]
    public void ParseDefaultRule_falls_back_to_Default_without_a_name()
    {
        var document = SubscriptionCreate("""
            <DefaultRuleDescription><Filter i:type="FalseFilter" /></DefaultRuleDescription>
            """);

        Assert.AreEqual(new RuleDefinition("$Default", "1=0"), AdminXml.ParseDefaultRule(document));
    }

    [TestMethod]
    public void ParseDefaultRule_returns_null_without_a_DefaultRuleDescription()
    {
        Assert.IsNull(AdminXml.ParseDefaultRule(SubscriptionCreate(string.Empty)));
    }

    [TestMethod]
    public void ParseDefaultRule_rejects_an_unsupported_filter_type()
    {
        var document = SubscriptionCreate("""
            <DefaultRuleDescription><Filter i:type="CorrelationFilter"><CorrelationId>c</CorrelationId></Filter></DefaultRuleDescription>
            """);

        Assert.ThrowsExactly<FormatException>(() => AdminXml.ParseDefaultRule(document));
    }

    [TestMethod]
    public void RuleEntry_writes_a_false_filter_as_FalseFilter()
    {
        var entry = AdminXml.RuleEntry(new RuleDefinition("$Default", "1=0"));

        var filter = entry.Descendants(XName.Get("Filter", ServiceBus)).Single();
        Assert.AreEqual("FalseFilter", filter.Attribute(XName.Get("type", SchemaInstance))?.Value);
        Assert.AreEqual(new RuleDefinition("$Default", "1=0"), AdminXml.ParseRule("$Default", entry));
    }

    private static XDocument SubscriptionCreate(string inner) => XDocument.Parse($"""
        <entry xmlns="{Atom}">
          <content type="application/xml">
            <SubscriptionDescription xmlns="{ServiceBus}" xmlns:i="{SchemaInstance}">
              <LockDuration>PT30S</LockDuration>
              {inner}
            </SubscriptionDescription>
          </content>
        </entry>
        """);
}
