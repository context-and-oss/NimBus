#pragma warning disable CA1707

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using NimBus.Core.Events;
using NimBus.WebApp.Controllers;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The Event Types page shows the example authored on the event class. The
/// mapper carries it to the client as <c>examplePayload</c>; without it the
/// client can only render property type names.
/// </summary>
[TestClass]
public class EventTypeExamplePayloadMappingTests
{
    /// <summary>
    /// Declares a public const before its Example, which is what the old
    /// first-public-field lookup tripped over.
    /// </summary>
    public class ConstBeforeExampleEvent : NimBus.Core.Events.Event
    {
        public const string EventTypeId = "NimBus.Platform.ConstBeforeExample";

        public static readonly ConstBeforeExampleEvent Example = new()
        {
            InvoiceNumber = "INV-2026-04417",
        };

        public string InvoiceNumber { get; set; } = string.Empty;
    }

    public class AuthoredEvent : NimBus.Core.Events.Event
    {
        public static readonly AuthoredEvent Example = new()
        {
            CustomerId = Guid.Parse("9c1a6f2e-4c31-4f0e-9a1c-2f7d5b3e8a10"),
            Name = "Nordvest Logistik A/S",
        };

        public Guid CustomerId { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class UnauthoredEvent : NimBus.Core.Events.Event
    {
        public Guid CustomerId { get; set; }
    }

    [TestMethod]
    public void Authored_example_is_serialized_as_indented_json()
    {
        var mapped = Mapper.EventTypeFromIEventType(
            new EventType(typeof(AuthoredEvent)), includeExamplePayload: true);

        Assert.IsNotNull(mapped.ExamplePayload);
        var json = JObject.Parse(mapped.ExamplePayload);
        Assert.AreEqual("9c1a6f2e-4c31-4f0e-9a1c-2f7d5b3e8a10", (string?)json["CustomerId"]);
        Assert.AreEqual("Nordvest Logistik A/S", (string?)json["Name"]);
        Assert.IsTrue(mapped.ExamplePayload.Contains('\n', StringComparison.Ordinal), "expected indented output");
    }

    [TestMethod]
    public void Counted_overload_carries_the_example_when_asked()
    {
        var mapped = Mapper.EventTypeFromIEventType(
            new EventType(typeof(AuthoredEvent)), 1, 2, includeExamplePayload: true);

        Assert.IsNotNull(mapped.ExamplePayload);
    }

    [TestMethod]
    public void Examples_are_left_out_unless_asked_for()
    {
        // Only the single event-type details route reads the example. The same
        // mappers feed the catalog list, the per-endpoint grouping, topology and
        // the command palette, which would otherwise carry every event's full
        // example — repeatedly — for nothing.
        Assert.IsNull(Mapper.EventTypeFromIEventType(new EventType(typeof(AuthoredEvent))).ExamplePayload);
        Assert.IsNull(Mapper.EventTypeFromIEventType(new EventType(typeof(AuthoredEvent)), 1, 2).ExamplePayload);
    }

    [TestMethod]
    public void Example_resolves_whatever_its_position_among_the_fields()
    {
        // The lookup used to take the first public field and cast it, so a const
        // declared above Example was returned instead, the cast threw, and the
        // page silently fell back to its type-name placeholder.
        var mapped = Mapper.EventTypeFromIEventType(
            new EventType(typeof(ConstBeforeExampleEvent)), includeExamplePayload: true);

        Assert.IsNotNull(mapped.ExamplePayload);
        Assert.AreEqual("INV-2026-04417", (string?)JObject.Parse(mapped.ExamplePayload)["InvoiceNumber"]);
    }

    [TestMethod]
    public void Missing_example_maps_to_null()
    {
        var mapped = Mapper.EventTypeFromIEventType(
            new EventType(typeof(UnauthoredEvent)), includeExamplePayload: true);

        Assert.IsNull(mapped.ExamplePayload);
    }
}
