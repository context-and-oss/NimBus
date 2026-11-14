namespace NimBus.WebApp.Services;

/// <summary>What the dead-letter queue of an endpoint's own subscription holds.</summary>
/// <param name="Total">Every message in the dead-letter queue.</param>
/// <param name="MatchingStoredEvents">
/// The messages whose EventId matches a stored DeadLettered event: the ones Delete dead-lettered
/// removes. The rest are left for Admin → Subscriptions → Purge dead letters.
/// </param>
public sealed record DeadLetterQueueCounts(long Total, int MatchingStoredEvents);
