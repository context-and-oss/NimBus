#pragma warning disable CA1707, CA2007
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Extensions;

namespace NimBus.SDK.Tests
{
    /// <summary>
    /// Regression coverage for the dedupe key on <see cref="NimBusSubscriberBuilder"/>.
    /// The runtime dispatch table (EventContextHandler) keys handlers on
    /// <c>EventTypeId</c> — which is the unqualified CLR type name — so the builder
    /// must reject collisions on the same key rather than accept both registrations
    /// and let one silently overwrite the other at runtime.
    /// </summary>
    [TestClass]
    public class SubscriberBuilderEventTypeIdTests
    {
        [TestMethod]
        public void AddHandler_TwoEventTypesWithSameNameInDifferentNamespaces_Throws()
        {
            var builder = new NimBusSubscriberBuilder(new ServiceCollection());

            builder.AddHandler<NamespaceA.OrderPlaced, NamespaceA.OrderPlacedHandler>();

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
                builder.AddHandler<NamespaceB.OrderPlaced, NamespaceB.OrderPlacedHandler>());

            // Error must call out both CLR types and the shared wire id so the
            // operator can rename one without spelunking through stack traces.
            StringAssert.Contains(ex.Message, "EventTypeId", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, "OrderPlaced", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, typeof(NamespaceA.OrderPlaced).FullName ?? "", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, typeof(NamespaceB.OrderPlaced).FullName ?? "", StringComparison.Ordinal);
        }

        [TestMethod]
        public void AddHandler_SameEventTypeTwice_IsIdempotent()
        {
            // Re-registering the *same* explicit handler should still be a no-op —
            // this is the legacy guard the dedupe check protects, and the fix to
            // key on EventTypeId must not break it.
            var builder = new NimBusSubscriberBuilder(new ServiceCollection());

            builder.AddHandler<NamespaceA.OrderPlaced, NamespaceA.OrderPlacedHandler>();
            builder.AddHandler<NamespaceA.OrderPlaced, NamespaceA.OrderPlacedHandler>();

            // Reaching this point without throwing is the contract; no assertion
            // on registration count because the field is internal.
        }
    }
}

namespace NimBus.SDK.Tests.NamespaceA
{
    public class OrderPlaced : Event
    {
        public string Id { get; set; } = "";
    }

    public class OrderPlacedHandler : IEventHandler<OrderPlaced>
    {
        public Task Handle(OrderPlaced message, IEventHandlerContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

namespace NimBus.SDK.Tests.NamespaceB
{
    public class OrderPlaced : Event
    {
        public string Id { get; set; } = "";
    }

    public class OrderPlacedHandler : IEventHandler<OrderPlaced>
    {
        public Task Handle(OrderPlaced message, IEventHandlerContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
