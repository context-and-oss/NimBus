using System.Xml;
using System.Xml.Linq;
using NimBus.ServiceBusEmulator.Broker;

namespace NimBus.ServiceBusEmulator.Admin;

internal static class AdminXml
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace ServiceBus = "http://schemas.microsoft.com/netservices/2010/10/servicebus/connect";
    private static readonly XNamespace SchemaInstance = "http://www.w3.org/2001/XMLSchema-instance";

    public static async Task<XDocument> ReadAsync(Stream input, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1024 * 1024,
            MaxCharactersFromEntities = 0,
        };
        using var reader = XmlReader.Create(input, settings);
        return await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
    }

    public static TopicDefinition ParseTopic(string name, XDocument document)
    {
        var description = Description(document, "TopicDescription");
        return new TopicDefinition(name)
        {
            DefaultMessageTimeToLive = Duration(description, "DefaultMessageTimeToLive"),
            MaxSizeInMegabytes = Long(description, "MaxSizeInMegabytes") ?? 1024,
            RequiresDuplicateDetection = Boolean(description, "RequiresDuplicateDetection"),
            DuplicateDetectionHistoryTimeWindow = Duration(description, "DuplicateDetectionHistoryTimeWindow") ?? TimeSpan.FromMinutes(10),
            EnableBatchedOperations = Boolean(description, "EnableBatchedOperations", true),
            SupportOrdering = Boolean(description, "SupportOrdering"),
            Status = Status(description),
        };
    }

    public static SubscriptionDefinition ParseSubscription(string name, XDocument document)
    {
        var description = Description(document, "SubscriptionDescription");
        return new SubscriptionDefinition(name)
        {
            LockDuration = Duration(description, "LockDuration") ?? TimeSpan.FromSeconds(30),
            RequiresSession = Boolean(description, "RequiresSession"),
            DefaultMessageTimeToLive = Duration(description, "DefaultMessageTimeToLive"),
            DeadLetterOnFilterEvaluationExceptions = Boolean(description, "DeadLetteringOnFilterEvaluationExceptions", true),
            MaxDeliveryCount = checked((int)(Long(description, "MaxDeliveryCount") ?? BrokerDefaults.MaxDeliveryCount)),
            ForwardTo = NormalizeForwardTo(Value(description, "ForwardTo")),
            Status = Status(description),
        };
    }

    public static RuleDefinition ParseRule(string name, XDocument document) =>
        ParseRuleDescription(name, Description(document, "RuleDescription"));

    /// <summary>
    /// Reads the rule a subscription create asks to be seeded with instead of the
    /// true-filter $Default (the SDK's <c>CreateSubscriptionAsync(options, rule)</c>), or
    /// null when the request carries none.
    /// </summary>
    public static RuleDefinition? ParseDefaultRule(XDocument document)
    {
        var rule = Description(document, "SubscriptionDescription").Element(ServiceBus + "DefaultRuleDescription");
        if (rule is null)
        {
            return null;
        }

        var name = Value(rule, "Name");
        return ParseRuleDescription(string.IsNullOrWhiteSpace(name) ? "$Default" : name, rule);
    }

    private static RuleDefinition ParseRuleDescription(string name, XElement description)
    {
        var filter = description.Element(ServiceBus + "Filter");
        var filterType = filter?.Attribute(SchemaInstance + "type")?.Value;
        var expression = filterType switch
        {
            not null when filterType.EndsWith("TrueFilter", StringComparison.Ordinal) => "1=1",
            not null when filterType.EndsWith("FalseFilter", StringComparison.Ordinal) => "1=0",
            _ => filter?.Element(ServiceBus + "SqlExpression")?.Value,
        };
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new FormatException("Only SqlFilter, TrueFilter and FalseFilter rules are supported by Spec 027.");
        }

        var action = description.Element(ServiceBus + "Action");
        var actionExpression = action?.Element(ServiceBus + "SqlExpression")?.Value;
        return new RuleDefinition(name, expression, string.IsNullOrWhiteSpace(actionExpression) ? null : actionExpression);
    }

    public static XDocument TopicEntry(TopicDefinition definition, TopicRuntimeProperties? runtime = null) =>
        Entry(definition.Name, TopicDescription(definition, runtime));

    public static XDocument SubscriptionEntry(
        string topicName,
        SubscriptionDefinition definition,
        SubscriptionRuntimeProperties? runtime = null) =>
        Entry(definition.Name, SubscriptionDescription(topicName, definition, runtime));

    public static XDocument RuleEntry(RuleDefinition definition) => Entry(definition.Name, RuleDescription(definition));

    public static XDocument Feed(IEnumerable<XDocument> entries) =>
        // The Azure SDK rejects a self-closing <feed /> as "not found". Azure
        // Service Bus feeds contain metadata even when there are no entries.
        new(new XElement(Atom + "feed",
            new XElement(Atom + "title", "Service Bus entities"),
            entries.Select(entry => entry.Root)));

    public static XDocument Error(string code, string detail) =>
        new(new XElement(ServiceBus + "Error",
            new XElement(ServiceBus + "Code", code),
            new XElement(ServiceBus + "Detail", detail)));

    private static XElement TopicDescription(TopicDefinition definition, TopicRuntimeProperties? runtime)
    {
        var description = new XElement(ServiceBus + "TopicDescription",
            definition.DefaultMessageTimeToLive is { } ttl ? Element("DefaultMessageTimeToLive", XmlConvert.ToString(ttl)) : null,
            Element("MaxSizeInMegabytes", definition.MaxSizeInMegabytes),
            Element("RequiresDuplicateDetection", definition.RequiresDuplicateDetection.ToString().ToLowerInvariant()),
            definition.RequiresDuplicateDetection ? Element("DuplicateDetectionHistoryTimeWindow", XmlConvert.ToString(definition.DuplicateDetectionHistoryTimeWindow)) : null,
            Element("EnableBatchedOperations", definition.EnableBatchedOperations.ToString().ToLowerInvariant()),
            Element("Status", definition.Status),
            Element("SupportOrdering", definition.SupportOrdering.ToString().ToLowerInvariant()),
            Element("EnablePartitioning", "false"),
            Element("EnableSubscriptionPartitioning", "false"),
            Element("EnableExpress", "false"));
        if (runtime is not null)
        {
            description.Add(
                Element("SizeInBytes", runtime.SizeInBytes),
                Element("SubscriptionCount", runtime.SubscriptionCount),
                Element("AccessedAt", runtime.AccessedAt.ToString("O")),
                Element("CreatedAt", runtime.CreatedAt.ToString("O")),
                Element("UpdatedAt", runtime.UpdatedAt.ToString("O")),
                new XElement(ServiceBus + "CountDetails", Element("ScheduledMessageCount", runtime.ScheduledMessageCount)));
        }

        return description;
    }

    private static XElement SubscriptionDescription(
        string topicName,
        SubscriptionDefinition definition,
        SubscriptionRuntimeProperties? runtime)
    {
        var now = DateTimeOffset.UtcNow;
        var description = new XElement(ServiceBus + "SubscriptionDescription",
            Element("LockDuration", XmlConvert.ToString(definition.LockDuration)),
            Element("RequiresSession", definition.RequiresSession.ToString().ToLowerInvariant()),
            definition.DefaultMessageTimeToLive is { } ttl ? Element("DefaultMessageTimeToLive", XmlConvert.ToString(ttl)) : null,
            Element("DeadLetteringOnMessageExpiration", "false"),
            Element("DeadLetteringOnFilterEvaluationExceptions", definition.DeadLetterOnFilterEvaluationExceptions.ToString().ToLowerInvariant()),
            Element("MaxDeliveryCount", definition.MaxDeliveryCount),
            Element("EnableBatchedOperations", "true"),
            Element("Status", definition.Status),
            definition.ForwardTo is not null ? Element("ForwardTo", definition.ForwardTo) : null);
        if (runtime is not null)
        {
            description.Add(
                Element("MessageCount", runtime.TotalMessageCount),
                Element("AccessedAt", now.ToString("O")),
                Element("CreatedAt", now.ToString("O")),
                Element("UpdatedAt", now.ToString("O")),
                new XElement(ServiceBus + "CountDetails",
                    Element("ActiveMessageCount", runtime.ActiveMessageCount),
                    Element("DeadLetterMessageCount", runtime.DeadLetterMessageCount),
                    Element("TransferMessageCount", runtime.TransferMessageCount),
                    Element("TransferDeadLetterMessageCount", runtime.TransferDeadLetterMessageCount)));
        }

        return description;
    }

    private static XElement RuleDescription(RuleDefinition definition)
    {
        var filterType = definition.FilterExpression switch
        {
            "1=1" => "TrueFilter",
            "1=0" => "FalseFilter",
            _ => "SqlFilter",
        };
        return new XElement(ServiceBus + "RuleDescription",
            new XElement(ServiceBus + "Filter",
                new XAttribute(SchemaInstance + "type", filterType),
                new XElement(ServiceBus + "SqlExpression", definition.FilterExpression),
                filterType == "SqlFilter" ? new XElement(ServiceBus + "CompatibilityLevel", 20) : null),
            definition.ActionExpression is null
                ? new XElement(ServiceBus + "Action", new XAttribute(SchemaInstance + "type", "EmptyRuleAction"))
                : new XElement(ServiceBus + "Action",
                    new XAttribute(SchemaInstance + "type", "SqlRuleAction"),
                    new XElement(ServiceBus + "SqlExpression", definition.ActionExpression),
                    new XElement(ServiceBus + "CompatibilityLevel", 20)),
            Element("Name", definition.Name));
    }

    private static XDocument Entry(string title, XElement description) =>
        new(new XElement(Atom + "entry",
            new XElement(Atom + "title", title),
            new XElement(Atom + "content", new XAttribute("type", "application/xml"), description)));

    private static XElement Description(XDocument document, string localName) =>
        document.Descendants(ServiceBus + localName).SingleOrDefault()
        ?? throw new FormatException($"The ATOM body does not contain {localName}.");

    private static XElement Element(string name, object value) => new(ServiceBus + name, value);

    private static string? Value(XElement parent, string name) => parent.Element(ServiceBus + name)?.Value;

    private static bool Boolean(XElement parent, string name, bool defaultValue = false) =>
        bool.TryParse(Value(parent, name), out var value) ? value : defaultValue;

    private static long? Long(XElement parent, string name) =>
        long.TryParse(Value(parent, name), System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

    private static TimeSpan? Duration(XElement parent, string name) =>
        Value(parent, name) is { Length: > 0 } value ? XmlConvert.ToTimeSpan(value) : null;

    private static BrokerEntityStatus Status(XElement parent) =>
        Enum.TryParse<BrokerEntityStatus>(Value(parent, "Status"), true, out var status) ? status : BrokerEntityStatus.Active;

    private static string? NormalizeForwardTo(string? forwardTo)
    {
        if (string.IsNullOrEmpty(forwardTo))
        {
            return null;
        }

        return Uri.TryCreate(forwardTo, UriKind.Absolute, out var uri) ? uri.Segments[^1].TrimEnd('/') : forwardTo;
    }
}
