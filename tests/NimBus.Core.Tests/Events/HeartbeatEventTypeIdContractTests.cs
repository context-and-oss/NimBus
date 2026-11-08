#pragma warning disable CA1707, CA1515, CA2007

using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;

namespace NimBus.Core.Tests.Events;

/// <summary>
/// The heartbeat wire contract. Three independently deployed sides agree on this
/// one string — the WebApp stamps it on the probe, the SDK's
/// <c>StrictMessageHandler</c> matches it to auto-answer, and the Resolver matches
/// it to divert the traffic away from event persistence.
/// </summary>
[TestClass]
public class HeartbeatEventTypeIdContractTests
{
    [TestMethod]
    public void EventTypeId_IsTheReservedLiteral_NotAConsumersUsingAlias()
    {
        // Regression from the fork parent: the WebApp and the Resolver both import
        // this type under a `using CoreHeartbeat = ...` alias to disambiguate it from
        // the message-store Heartbeat, and both derived the id with nameof. C# returns
        // the *alias* from nameof, so "CoreHeartbeat" went on the wire. The Resolver
        // matched it (same alias, same mistake) so the divert worked and the feature
        // looked healthy — but the SDK matched the real type name and never fired,
        // leaving every adapter reporting Unsupported instead of On.
        //
        // Asserting the literal is the point: re-deriving it from the type would
        // reintroduce exactly the coupling that hid the bug.
        Assert.AreEqual("NimBus.Platform.Heartbeat", Heartbeat.EventTypeId);
    }

    [TestMethod]
    public void EventTypeId_CanNeverCollideWithAnApplicationEventName()
    {
        // Application EventTypeIds are unqualified CLR type names, global to the
        // namespace. If the probe travelled under a plain type-name-shaped id, an
        // application event class with that name would be silently swallowed: the
        // SDK would answer and complete it before its registered handler ran, and
        // the Resolver would divert it from the audit trail. The dot makes the id
        // unspellable as an unqualified type name, so the collision is impossible.
        StringAssert.Contains(Heartbeat.EventTypeId, ".", StringComparison.Ordinal);
        Assert.AreNotEqual(Heartbeat.EventTypeId, typeof(Heartbeat).Name);
    }
}
