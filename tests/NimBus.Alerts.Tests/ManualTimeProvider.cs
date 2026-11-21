#pragma warning disable CA1707, CA1515, CA2007
namespace NimBus.Alerts.Tests;

/// <summary>A clock the tests set and move by hand.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
