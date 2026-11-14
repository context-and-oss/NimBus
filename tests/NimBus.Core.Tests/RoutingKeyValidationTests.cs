#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;

namespace NimBus.Core.Tests;

/// <summary>Spec 045 §7 and §8: the routing-key rules of <see cref="PlatformValidation"/>.</summary>
[TestClass]
public class RoutingKeyValidationTests
{
    [RoutingKey(nameof(Instance))]
    private sealed class Routed : Event
    {
        public string Instance { get; set; } = "";
    }

    [RoutingKey(nameof(Instance), DropUnmatched = true)]
    private sealed class Dropping : Event
    {
        public string Instance { get; set; } = "";
    }

    private sealed class Unrouted : Event
    {
        public string Instance { get; set; } = "";
    }

    [RoutingKey(nameof(Number))]
    private sealed class NumberRouted : Event
    {
        public int Number { get; set; }
    }

    [RoutingKey(nameof(Instance))]
    private sealed class SensitiveRouted : Event
    {
        [Sensitive]
        public string Instance { get; set; } = "";
    }

    [RoutingKey(nameof(Instance))]
    private sealed class RoutedCommand : Command
    {
        public string Instance { get; set; } = "";
    }

    [RoutingKey(nameof(Instance), DropUnmatched = true)]
    private sealed class DroppingCommand : Command
    {
        public string Instance { get; set; } = "";
    }

    /// <summary>An endpoint whose declarations are given as data, so one class covers every case.</summary>
    private sealed class Declared : IEndpoint
    {
        private readonly Dictionary<string, string[]> _keys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _unmatched = new(StringComparer.Ordinal);
        private readonly List<IEventType> _consumed = [];

        public Declared(string id) => Id = id;

        public Declared Keys<TEvent>(params string[] keys) where TEvent : IEvent
        {
            var type = new EventType<TEvent>();
            _consumed.Add(type);
            _keys[type.Id] = keys;
            return this;
        }

        public Declared Unmatched<TEvent>(bool alsoKeys = false) where TEvent : IEvent
        {
            var type = new EventType<TEvent>();
            if (!alsoKeys)
                _consumed.Add(type);
            _unmatched.Add(type.Id);
            return this;
        }

        public Declared All<TEvent>() where TEvent : IEvent
        {
            _consumed.Add(new EventType<TEvent>());
            return this;
        }

        public string Id { get; }
        public string Name => Id;
        public string? Description => null;
        public string? Namespace => null;
        public string SecurityGroupName => "";
        public ISystem? System => null;
        public IEnumerable<IEventType> EventTypesProduced => [];
        public IEnumerable<IEventType> EventTypesConsumed => _consumed;
        public IEnumerable<IRoleAssignment> RoleAssignments => [];

        public IReadOnlyCollection<string> GetRoutingKeys(IEventType eventType) =>
            _keys.TryGetValue(eventType.Id, out var keys) ? keys : [];

        public bool IsUnmatchedConsumer(IEventType eventType) => _unmatched.Contains(eventType.Id);
    }

    private sealed class TestPlatform : Platform
    {
        public TestPlatform(params IEndpoint[] endpoints)
        {
            foreach (var endpoint in endpoints)
                AddEndpoint(endpoint);
        }
    }

    private static IReadOnlyList<string> Errors(params IEndpoint[] endpoints) =>
        PlatformValidation.ValidateRoutingKeys(new TestPlatform(endpoints));

    private static IReadOnlyList<string> CommandErrors(params IEndpoint[] endpoints) =>
        PlatformValidation.ValidateCommandConsumers(new TestPlatform(endpoints));

    [TestMethod]
    public void AValidKeyedPlatform_HasNoErrors()
    {
        var errors = Errors(
            new Declared("Erp01").Keys<Routed>("01"),
            new Declared("Erp02").Keys<Routed>("02", "03"),
            new Declared("Unrouted").Unmatched<Routed>(),
            new Declared("Reporting").All<Routed>());

        Assert.AreEqual(0, errors.Count, string.Join(System.Environment.NewLine, errors));
    }

    [TestMethod]
    public void AnInvalidKey_IsAnError()
    {
        var errors = Errors(new Declared("Erp01").Keys<Routed>("o'clock"), new Declared("Unrouted").Unmatched<Routed>());

        Assert.IsTrue(errors.Any(error => error.Contains("'o'clock'", StringComparison.Ordinal)), string.Join(System.Environment.NewLine, errors));
    }

    [TestMethod]
    public void KeysDifferingOnlyByCase_AreAnError()
    {
        var errors = Errors(
            new Declared("Erp01").Keys<Routed>("dk"),
            new Declared("Erp02").Keys<Routed>("DK"),
            new Declared("Unrouted").Unmatched<Routed>());

        Assert.IsTrue(errors.Any(error => error.Contains("differ only by case", StringComparison.Ordinal)), string.Join(System.Environment.NewLine, errors));
    }

    [TestMethod]
    public void KeysAndUnmatchedOnOneEndpoint_AreAnError()
    {
        var errors = Errors(new Declared("Erp01").Keys<Routed>("01").Unmatched<Routed>(alsoKeys: true));

        Assert.IsTrue(errors.Any(error => error.Contains("declares routing keys and unmatched", StringComparison.Ordinal)), string.Join(System.Environment.NewLine, errors));
    }

    [TestMethod]
    public void AKeyedTypeWithoutAnUnmatchedConsumer_IsAnError()
    {
        var errors = Errors(new Declared("Erp01").Keys<Routed>("01"));

        Assert.IsTrue(errors.Any(error => error.Contains("no unmatched consumer", StringComparison.Ordinal)), string.Join(System.Environment.NewLine, errors));
    }

    [TestMethod]
    public void DropUnmatched_AcceptsAMissingUnmatchedConsumer()
    {
        var errors = Errors(new Declared("Erp01").Keys<Dropping>("01"));

        Assert.AreEqual(0, errors.Count, string.Join(System.Environment.NewLine, errors));
    }

    [TestMethod]
    public void KeysForATypeWithoutARoutingKey_AreAnError()
    {
        var errors = Errors(new Declared("Erp01").Keys<Unrouted>("01"), new Declared("Unrouted").Unmatched<Unrouted>());

        Assert.IsTrue(errors.Any(error => error.Contains("provides no routing key", StringComparison.Ordinal)), string.Join(System.Environment.NewLine, errors));
    }

    [TestMethod]
    public void ANonStringRoutingKeyProperty_IsAnError()
    {
        var errors = Errors(new Declared("Reporting").All<NumberRouted>());

        Assert.IsTrue(errors.Any(error => error.Contains("must be a string", StringComparison.Ordinal)), string.Join(System.Environment.NewLine, errors));
    }

    [TestMethod]
    public void ASensitiveRoutingKeyProperty_IsAnError()
    {
        var errors = Errors(new Declared("Reporting").All<SensitiveRouted>());

        Assert.IsTrue(errors.Any(error => error.Contains("sensitive", StringComparison.Ordinal)), string.Join(System.Environment.NewLine, errors));
    }

    [TestMethod]
    public void TwoEventConsumersMayClaimTheSameKey()
    {
        var errors = Errors(
            new Declared("Erp01").Keys<Routed>("01"),
            new Declared("Reporting01").Keys<Routed>("01"),
            new Declared("Unrouted").Unmatched<Routed>());

        Assert.AreEqual(0, errors.Count, string.Join(System.Environment.NewLine, errors));
    }

    // ── Commands (§7) ──────────────────────────────────────────────

    [TestMethod]
    public void AKeyedCommand_WithOneConsumerPerKeyAndOneUnmatched_IsValid()
    {
        var errors = CommandErrors(
            new Declared("Erp01").Keys<RoutedCommand>("01"),
            new Declared("Erp02").Keys<RoutedCommand>("02"),
            new Declared("Unrouted").Unmatched<RoutedCommand>());

        Assert.AreEqual(0, errors.Count, string.Join(System.Environment.NewLine, errors));
    }

    [TestMethod]
    public void AKeyedCommand_KeyClaimedTwice_IsAnError()
    {
        var errors = CommandErrors(
            new Declared("Erp01").Keys<RoutedCommand>("01"),
            new Declared("Erp01b").Keys<RoutedCommand>("01"),
            new Declared("Unrouted").Unmatched<RoutedCommand>());

        Assert.IsTrue(errors.Any(error => error.Contains("is claimed by Erp01, Erp01b", StringComparison.Ordinal)), string.Join(System.Environment.NewLine, errors));
    }

    [TestMethod]
    public void AKeyedCommand_WithoutAnUnmatchedConsumer_IsAnError()
    {
        var errors = CommandErrors(new Declared("Erp01").Keys<RoutedCommand>("01"), new Declared("Erp02").Keys<RoutedCommand>("02"));

        Assert.IsTrue(errors.Any(error => error.Contains("0 unmatched consumers", StringComparison.Ordinal)), string.Join(System.Environment.NewLine, errors));
    }

    [TestMethod]
    public void AKeyedCommand_MixingAnUnkeyedConsumer_IsAnError()
    {
        var errors = CommandErrors(
            new Declared("Erp01").Keys<RoutedCommand>("01"),
            new Declared("Everything").All<RoutedCommand>(),
            new Declared("Unrouted").Unmatched<RoutedCommand>());

        Assert.IsTrue(errors.Any(error => error.Contains("mixes keyed and unkeyed", StringComparison.Ordinal)), string.Join(System.Environment.NewLine, errors));
    }

    [TestMethod]
    public void AKeyedCommand_WithDropUnmatched_IsAnError()
    {
        var errors = CommandErrors(
            new Declared("Erp01").Keys<DroppingCommand>("01"),
            new Declared("Unrouted").Unmatched<DroppingCommand>());

        Assert.IsTrue(errors.Any(error => error.Contains("DropUnmatched", StringComparison.Ordinal)), string.Join(System.Environment.NewLine, errors));
    }

    [TestMethod]
    public void AnUnkeyedCommandWithTwoConsumers_IsStillAnError()
    {
        var errors = CommandErrors(new Declared("A").All<RoutedCommand>(), new Declared("B").All<RoutedCommand>());

        Assert.AreEqual(1, errors.Count);
    }

    [TestMethod]
    public void EnsureValid_RunsBothRuleSets()
    {
        var platform = new TestPlatform(new Declared("Erp01").Keys<Routed>("01"), new Declared("A").All<RoutedCommand>(), new Declared("B").All<RoutedCommand>());

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => PlatformValidation.EnsureValid(platform));

        StringAssert.Contains(error.Message, "no unmatched consumer");
        StringAssert.Contains(error.Message, "2 consumers");
    }
}
