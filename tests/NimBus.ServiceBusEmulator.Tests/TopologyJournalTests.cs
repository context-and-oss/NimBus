#pragma warning disable CA1707, CA2007

using NimBus.ServiceBusEmulator.Broker;
using NimBus.ServiceBusEmulator.Storage;

namespace NimBus.ServiceBusEmulator.Tests;

[TestClass]
public sealed class TopologyJournalTests
{
    [TestMethod]
    public async Task Journal_round_trips_topology_without_messages()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "topology.json");
        var source = new BrokerNamespace(new BrokerOptions());
        source.CreateTopic(new TopicDefinition("events"));
        source.CreateSubscription("events", new SubscriptionDefinition("consumer") { RequiresSession = true });
        source.DeleteRule("events", "consumer", "$Default");
        source.CreateRule("events", "consumer", new RuleDefinition("orders", "user.EventTypeId = 'order'"));
        source.Publish("events", new BrokerMessage { MessageId = "volatile" });

        using (var journal = new TopologyJournal(path))
        {
            await journal.SaveAsync(source, CancellationToken.None);
        }

        var restored = new BrokerNamespace(new BrokerOptions());
        using (var journal = new TopologyJournal(path))
        {
            await journal.ReplayAsync(restored, CancellationToken.None);
        }

        Assert.IsTrue(restored.TopicExists("events"));
        Assert.IsTrue(restored.GetSubscriptionDefinition("events", "consumer").RequiresSession);
        Assert.AreEqual("user.EventTypeId = 'order'", restored.GetRule("events", "consumer", "orders").FilterExpression);
        Assert.AreEqual(0, restored.GetSubscriptionRuntimeProperties("events", "consumer").TotalMessageCount);
    }

    [TestMethod]
    public async Task Journal_replays_a_non_default_Default_rule_faithfully()
    {
        // A subscription whose $Default is not the seeded true filter must come back as it
        // was saved, not as $Default = 1=1, which would hand it everything on the topic.
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "topology.json");
        var source = new BrokerNamespace(new BrokerOptions());
        source.CreateTopic(new TopicDefinition("events"));

        source.CreateSubscription("events", new SubscriptionDefinition("closed"));
        source.DeleteRule("events", "closed", "$Default");
        source.CreateRule("events", "closed", new RuleDefinition("$Default", "user.Closed IS NULL"));

        source.CreateSubscription("events", new SubscriptionDefinition("explicit"));
        source.DeleteRule("events", "explicit", "$Default");
        source.CreateRule("events", "explicit", new RuleDefinition("$Default", "user.Label = 'x'", "SET user.To = 'y'"));

        source.CreateSubscription("events", new SubscriptionDefinition("none"));
        source.DeleteRule("events", "none", "$Default");
        source.CreateRule("events", "none", new RuleDefinition("orders", "user.EventTypeId = 'order'"));

        using (var journal = new TopologyJournal(path))
        {
            await journal.SaveAsync(source, CancellationToken.None);
        }

        var restored = new BrokerNamespace(new BrokerOptions());
        using (var journal = new TopologyJournal(path))
        {
            await journal.ReplayAsync(restored, CancellationToken.None);
        }

        foreach (var subscription in new[] { "closed", "explicit", "none" })
        {
            CollectionAssert.AreEquivalent(
                source.GetRules("events", subscription).ToArray(),
                restored.GetRules("events", subscription).ToArray(),
                $"Rules of '{subscription}' after replay: {string.Join(", ", restored.GetRules("events", subscription))}");
            Assert.IsFalse(
                restored.GetRules("events", subscription).Any(rule => rule.Name == "$Default" && rule.FilterExpression == "1=1"),
                $"'{subscription}' came back with a true-filter $Default.");
        }
    }

    [TestMethod]
    public async Task Journal_round_trips_a_subscription_created_closed()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "topology.json");
        var source = new BrokerNamespace(new BrokerOptions());
        source.CreateTopic(new TopicDefinition("events"));
        var prepared = source.PrepareCreateSubscription("events", new SubscriptionDefinition("closed"), new RuleDefinition("$Default", "1=0"));

        // The admin plane journals the prepared snapshot before applying it.
        using (var journal = new TopologyJournal(path))
        {
            await journal.SaveAsync(prepared.Snapshot, CancellationToken.None);
        }

        var restored = new BrokerNamespace(new BrokerOptions());
        using (var journal = new TopologyJournal(path))
        {
            await journal.ReplayAsync(restored, CancellationToken.None);
        }

        CollectionAssert.AreEqual(new[] { new RuleDefinition("$Default", "1=0") }, restored.GetRules("events", "closed").ToArray());
        restored.Publish("events", new BrokerMessage { MessageId = "after-restart" });
        Assert.AreEqual(0, restored.GetSubscriptionRuntimeProperties("events", "closed").TotalMessageCount);
    }

    [TestMethod]
    public async Task Corrupt_journal_is_renamed_and_broker_starts_empty()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "topology.json");
        await File.WriteAllTextAsync(path, "{ definitely-not-json");
        var broker = new BrokerNamespace(new BrokerOptions());

        using var journal = new TopologyJournal(path);
        await journal.ReplayAsync(broker, CancellationToken.None);

        Assert.IsFalse(File.Exists(path));
        Assert.HasCount(1, Directory.GetFiles(directory.Path, "topology.json.corrupt-*"));
        Assert.HasCount(0, broker.GetTopics());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nimbus-journal-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
