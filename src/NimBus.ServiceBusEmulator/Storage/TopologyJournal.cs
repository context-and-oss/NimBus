using System.Text.Json;
using NimBus.ServiceBusEmulator.Broker;

namespace NimBus.ServiceBusEmulator.Storage;

internal sealed class TopologyJournal(string path) : IDisposable
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public static string DefaultPath(string resourceName) => Path.Combine(
        Path.GetTempPath(),
        "nimbus-sbemulator",
        Sanitize(resourceName),
        "topology.json");

    public async Task ReplayAsync(BrokerNamespace broker, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        VersionedTopologySnapshot snapshot;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            snapshot = await JsonSerializer.DeserializeAsync<VersionedTopologySnapshot>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Topology journal '{path}' is empty or invalid.");
            if (snapshot.Version != CurrentVersion)
            {
                throw new InvalidDataException($"Topology journal version {snapshot.Version} is not supported.");
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            var corruptPath = path + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            File.Move(path, corruptPath);
            return;
        }

        foreach (var topic in snapshot.Topics)
        {
            broker.CreateTopic(topic.Definition);
            foreach (var subscription in topic.Subscriptions)
            {
                // A journaled $Default is the subscription's initial rule, so one with a
                // non-true filter or an action comes back as saved, not as the seeded 1=1.
                var defaultRule = subscription.Rules.FirstOrDefault(rule => string.Equals(rule.Name, "$Default", StringComparison.OrdinalIgnoreCase));
                broker.CreateSubscription(topic.Definition.Name, subscription.Definition, defaultRule);
                foreach (var rule in subscription.Rules.Where(rule => !string.Equals(rule.Name, "$Default", StringComparison.OrdinalIgnoreCase)))
                {
                    broker.CreateRule(topic.Definition.Name, subscription.Definition.Name, rule);
                }

                if (defaultRule is null)
                {
                    broker.DeleteRule(topic.Definition.Name, subscription.Definition.Name, "$Default");
                }
            }
        }
    }

    public async Task SaveAsync(BrokerNamespace broker, CancellationToken cancellationToken)
    {
        await SaveAsync(broker.GetTopologySnapshot(), cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(TopologySnapshot snapshot, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The topology journal path has no directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 4096,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        new VersionedTopologySnapshot(CurrentVersion, snapshot.Topics),
                        SerializerOptions,
                        cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose() => _writeGate.Dispose();

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }

    private sealed record VersionedTopologySnapshot(int Version, IReadOnlyList<TopologyTopic> Topics);
}
