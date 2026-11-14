namespace NimBus.WebApp.Versioning;

/// <summary>
/// Marks an endpoint reached through a deprecated, unversioned <c>/api/…</c> route. The same
/// operation is served at <c>/api/v1/…</c>; the unversioned routes are removed in the next major
/// release (see <c>docs/versioning.md</c>).
/// </summary>
public sealed class LegacyApiRouteMetadata
{
    /// <summary>The single instance every legacy selector carries.</summary>
    public static LegacyApiRouteMetadata Instance { get; } = new();

    private LegacyApiRouteMetadata()
    {
    }
}
