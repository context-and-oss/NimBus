#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;

namespace NimBus.Core.Tests.Events;

[TestClass]
public class RoutingKeyTests
{
    [RoutingKey(nameof(Instance))]
    private class KeyedOrder : Event
    {
        public string? Instance { get; set; }
    }

    private sealed class DerivedKeyedOrder : KeyedOrder
    {
        public string Extra { get; set; } = "";
    }

    private sealed class ComputedOrder : Event
    {
        public int Country { get; set; }
        public override string? GetRoutingKey() => $"c{Country}";
    }

    private sealed class UnkeyedOrder : Event
    {
        public string Instance { get; set; } = "";
    }

    [RoutingKey("Missing")]
    private sealed class MissingPropertyOrder : Event;

    [RoutingKey(nameof(Number))]
    private sealed class NumberKeyedOrder : Event
    {
        public int Number { get; set; }
    }

    [RoutingKey(nameof(Instance))]
    private sealed class SensitiveKeyedOrder : Event
    {
        [Sensitive]
        public string Instance { get; set; } = "";
    }

    [Sensitive]
    [RoutingKey(nameof(Instance))]
    private sealed class SensitiveClassOrder : Event
    {
        public string Instance { get; set; } = "";
    }

    [RoutingKey(nameof(Instance), DropUnmatched = true)]
    private sealed class DroppingOrder : Event
    {
        public string Instance { get; set; } = "";
    }

    [TestMethod]
    public void GetRoutingKey_WithAttribute_ReturnsPropertyValue()
    {
        Assert.AreEqual("01", new KeyedOrder { Instance = "01" }.GetRoutingKey());
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void GetRoutingKey_NullOrEmptyValue_ReturnsNull(string? value)
    {
        Assert.IsNull(new KeyedOrder { Instance = value }.GetRoutingKey());
    }

    [TestMethod]
    public void GetRoutingKey_IsInherited()
    {
        Assert.AreEqual("02", new DerivedKeyedOrder { Instance = "02" }.GetRoutingKey());
    }

    [TestMethod]
    public void GetRoutingKey_Override_IsUsed()
    {
        Assert.AreEqual("c45", new ComputedOrder { Country = 45 }.GetRoutingKey());
    }

    [TestMethod]
    public void GetRoutingKey_NoAttribute_ReturnsNull()
    {
        Assert.IsNull(new UnkeyedOrder { Instance = "01" }.GetRoutingKey());
    }

    [TestMethod]
    public void GetRoutingKey_UnknownProperty_Throws()
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => new MissingPropertyOrder().GetRoutingKey());
        StringAssert.Contains(error.Message, "does not exist");
    }

    [TestMethod]
    public void GetRoutingKey_NonStringProperty_Throws()
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => new NumberKeyedOrder { Number = 1 }.GetRoutingKey());
        StringAssert.Contains(error.Message, "must be a string");
    }

    [TestMethod]
    public void GetRoutingKey_SensitiveProperty_Throws()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => new SensitiveKeyedOrder { Instance = "01" }.GetRoutingKey());
    }

    [TestMethod]
    public void GetRoutingKey_SensitiveClass_Throws()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => new SensitiveClassOrder { Instance = "01" }.GetRoutingKey());
    }

    [TestMethod]
    public void EventType_ExposesRoutingKeyProperty()
    {
        IEventType keyed = new EventType<KeyedOrder>();
        IEventType unkeyed = new EventType<UnkeyedOrder>();

        Assert.AreEqual(nameof(KeyedOrder.Instance), keyed.RoutingKeyProperty);
        Assert.IsNull(unkeyed.RoutingKeyProperty);
    }

    [TestMethod]
    public void IsProvidedBy_AttributeOrOverride()
    {
        Assert.IsTrue(RoutingKeys.IsProvidedBy(typeof(KeyedOrder)));
        Assert.IsTrue(RoutingKeys.IsProvidedBy(typeof(ComputedOrder)));
        Assert.IsFalse(RoutingKeys.IsProvidedBy(typeof(UnkeyedOrder)));
        Assert.IsFalse(RoutingKeys.IsProvidedBy(null));
    }

    [TestMethod]
    public void DropUnmatched_IsReadFromTheAttribute()
    {
        Assert.IsTrue(RoutingKeys.GetAttribute(typeof(DroppingOrder))!.DropUnmatched);
        Assert.IsFalse(RoutingKeys.GetAttribute(typeof(KeyedOrder))!.DropUnmatched);
    }

    [TestMethod]
    [DataRow("01", true)]
    [DataRow("DK", true)]
    [DataRow("a.b_c-d", true)]
    [DataRow("", false)]
    [DataRow("has space", false)]
    [DataRow("quote'", false)]
    [DataRow("æøå", false)]
    public void IsValid_ChecksTheDeclaredKeyFormat(string key, bool expected)
    {
        Assert.AreEqual(expected, RoutingKeys.IsValid(key));
    }

    [TestMethod]
    public void IsValid_RejectsKeysOver64Characters()
    {
        Assert.IsTrue(RoutingKeys.IsValid(new string('a', 64)));
        Assert.IsFalse(RoutingKeys.IsValid(new string('a', 65)));
    }

    // ── Endpoint declarations ───────────────────────────────────────

    private sealed class Instance01 : Endpoint
    {
        public Instance01()
        {
            Consumes<KeyedOrder>("02", "01", "01");
        }
    }

    private sealed class Unrouted : Endpoint
    {
        public Unrouted()
        {
            ConsumesUnmatched<KeyedOrder>();
        }
    }

    private sealed class Reporting : Endpoint
    {
        public Reporting()
        {
            Consumes<KeyedOrder>();
            Consumes<UnkeyedOrder>();
        }
    }

    private sealed class EmptyKeys : Endpoint
    {
        public EmptyKeys() => Consumes<KeyedOrder>(Array.Empty<string>());
    }

    private sealed class BadKey : Endpoint
    {
        public BadKey() => Consumes<KeyedOrder>("o'clock");
    }

    private sealed class TwiceDeclared : Endpoint
    {
        public TwiceDeclared()
        {
            Consumes<KeyedOrder>("01");
            ConsumesUnmatched<KeyedOrder>();
        }
    }

    [TestMethod]
    public void Consumes_WithKeys_StoresThemCanonically()
    {
        var endpoint = new Instance01();
        var keyed = new EventType<KeyedOrder>();

        CollectionAssert.AreEqual(new[] { "01", "02" }, endpoint.GetRoutingKeys(keyed).ToArray());
        Assert.IsFalse(endpoint.IsUnmatchedConsumer(keyed));
        Assert.AreEqual(1, endpoint.EventTypesConsumed.Count());
    }

    [TestMethod]
    public void ConsumesUnmatched_MarksTheType()
    {
        var endpoint = new Unrouted();
        var keyed = new EventType<KeyedOrder>();

        Assert.IsTrue(endpoint.IsUnmatchedConsumer(keyed));
        Assert.AreEqual(0, endpoint.GetRoutingKeys(keyed).Count);
        Assert.AreEqual(1, endpoint.EventTypesConsumed.Count());
    }

    [TestMethod]
    public void Consumes_WithoutKeys_TakesEveryKey()
    {
        var endpoint = new Reporting();

        Assert.AreEqual(0, endpoint.GetRoutingKeys(new EventType<KeyedOrder>()).Count);
        Assert.IsFalse(endpoint.IsUnmatchedConsumer(new EventType<KeyedOrder>()));
        Assert.AreEqual(2, endpoint.EventTypesConsumed.Count());
    }

    [TestMethod]
    public void Consumes_WithAnEmptyKeyArray_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new EmptyKeys());
    }

    [TestMethod]
    public void Consumes_WithAMalformedKey_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new BadKey());
    }

    [TestMethod]
    public void DeclaringATypeTwice_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new TwiceDeclared());
    }

    // ── Platform ────────────────────────────────────────────────────

    private sealed class Instance02 : Endpoint
    {
        public Instance02() => Consumes<KeyedOrder>("03", "02");
    }

    private sealed class TestPlatform : Platform
    {
        public TestPlatform(params IEndpoint[] endpoints)
        {
            foreach (var endpoint in endpoints)
                AddEndpoint(endpoint);
        }
    }

    [TestMethod]
    public void GetClaimedRoutingKeys_IsTheOrdinalUnionOverConsumers()
    {
        IPlatform platform = new TestPlatform(new Instance01(), new Instance02(), new Unrouted(), new Reporting());

        CollectionAssert.AreEqual(
            new[] { "01", "02", "03" },
            platform.GetClaimedRoutingKeys(new EventType<KeyedOrder>()).ToArray());
        Assert.AreEqual(0, platform.GetClaimedRoutingKeys(new EventType<UnkeyedOrder>()).Count);
    }

    [TestMethod]
    public void IEndpointDefaults_MeanUnkeyed()
    {
        IEndpoint endpoint = new MinimalEndpoint();
        var type = new EventType<KeyedOrder>();

        Assert.AreEqual(0, endpoint.GetRoutingKeys(type).Count);
        Assert.IsFalse(endpoint.IsUnmatchedConsumer(type));
    }

    private sealed class MinimalEndpoint : IEndpoint
    {
        public string Id => "Minimal";
        public string Name => "Minimal";
        public string? Description => null;
        public string? Namespace => null;
        public string SecurityGroupName => "";
        public ISystem? System => null;
        public IEnumerable<IEventType> EventTypesProduced => [];
        public IEnumerable<IEventType> EventTypesConsumed => [new EventType<KeyedOrder>()];
        public IEnumerable<IRoleAssignment> RoleAssignments => [];
    }
}
