#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using System;
using System.ComponentModel;

namespace NimBus.Core.Tests;

[TestClass]
public class SessionKeyAttributeTests
{
    // ── Test event classes ──────────────────────────────────────────

    [SessionKey(nameof(OrderId))]
    private class OrderWithAttribute : Event
    {
        public Guid OrderId { get; set; }
        public string Product { get; set; } = null!;
    }

    private sealed class OrderWithOverride : Event
    {
        public Guid OrderId { get; set; }
        public override string GetSessionId() => $"custom-{OrderId}";
    }

    private sealed class OrderWithNoSessionKey : Event
    {
        public Guid OrderId { get; set; }
    }

    [SessionKey("NonExistentProperty")]
    private sealed class OrderWithBadAttribute : Event
    {
        public Guid OrderId { get; set; }
    }

    [SessionKey(nameof(NullableField))]
    private sealed class OrderWithNullableField : Event
    {
        public string? NullableField { get; set; }
    }

    [SessionKey(nameof(CustomerId))]
    private sealed class DerivedOrder : OrderWithAttribute
    {
        public Guid CustomerId { get; set; }
    }

    // ── Tests ───────────────────────────────────────────────────────

    [TestMethod]
    public void GetSessionId_WithAttribute_ReturnsPropertyValue()
    {
        var orderId = Guid.NewGuid();
        var evt = new OrderWithAttribute { OrderId = orderId };

        Assert.AreEqual(orderId.ToString(), evt.GetSessionId());
    }

    [TestMethod]
    public void GetSessionId_WithOverride_ReturnsOverrideValue()
    {
        var orderId = Guid.NewGuid();
        var evt = new OrderWithOverride { OrderId = orderId };

        Assert.AreEqual($"custom-{orderId}", evt.GetSessionId());
    }

    [TestMethod]
    public void GetSessionId_NoAttributeNoOverride_ReturnsGuid()
    {
        var evt = new OrderWithNoSessionKey { OrderId = Guid.NewGuid() };
        var sessionId = evt.GetSessionId();

        Assert.IsTrue(Guid.TryParse(sessionId, out _), "Should return a valid GUID");
    }

    [TestMethod]
    public void GetSessionId_NoAttributeNoOverride_ReturnsDifferentGuidsEachCall()
    {
        var evt = new OrderWithNoSessionKey { OrderId = Guid.NewGuid() };
        var id1 = evt.GetSessionId();
        var id2 = evt.GetSessionId();

        Assert.AreNotEqual(id1, id2, "Each call should return a unique GUID");
    }

    [TestMethod]
    public void GetSessionId_AttributeReferencesNonExistentProperty_Throws()
    {
        var evt = new OrderWithBadAttribute { OrderId = Guid.NewGuid() };
        Assert.ThrowsExactly<InvalidOperationException>(() => evt.GetSessionId());
    }

    [TestMethod]
    public void GetSessionId_AttributePropertyIsNull_ReturnsGuid()
    {
        var evt = new OrderWithNullableField { NullableField = null! };
        var sessionId = evt.GetSessionId();

        Assert.IsTrue(Guid.TryParse(sessionId, out _), "Null property value should fall back to GUID");
    }

    [TestMethod]
    public void GetSessionId_AttributePropertyHasValue_ReturnsValue()
    {
        var evt = new OrderWithNullableField { NullableField = "my-session" };
        Assert.AreEqual("my-session", evt.GetSessionId());
    }

    [TestMethod]
    public void GetSessionId_DerivedClassOverridesAttribute_UsesDerivedAttribute()
    {
        var customerId = Guid.NewGuid();
        var evt = new DerivedOrder
        {
            OrderId = Guid.NewGuid(),
            CustomerId = customerId
        };

        Assert.AreEqual(customerId.ToString(), evt.GetSessionId());
    }

    [TestMethod]
    public void SessionKeyAttribute_ExposedInEventTypeMetadata()
    {
        var eventType = new EventType(typeof(OrderWithAttribute));
        Assert.AreEqual("OrderId", eventType.SessionKeyProperty);
    }

    [TestMethod]
    public void SessionKeyAttribute_NullForEventsWithoutAttribute()
    {
        var eventType = new EventType(typeof(OrderWithNoSessionKey));
        Assert.IsNull(eventType.SessionKeyProperty);
    }
}
