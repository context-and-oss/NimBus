namespace NimBus.Core.Endpoints;

/// <summary>
/// Optional capability an <see cref="IEndpoint"/> MAY implement to declare that it
/// participates in CloudEvents 1.0 interoperability. This is the source of truth the
/// AsyncAPI exporter reads to reflect CloudEvents-enabled endpoints (content mode +
/// CloudEvents attribute headers). Endpoints that do not implement it produce
/// exactly the native AsyncAPI output, so existing catalogs are unchanged.
/// <para>
/// It is declared with primitive types (no dependency on the CloudEvents model) so
/// it can live alongside <see cref="IEndpoint"/> in the abstractions layer.
/// </para>
/// </summary>
public interface ICloudEventsAware
{
    /// <summary>
    /// CloudEvents content mode this endpoint uses: <c>"binary"</c> or
    /// <c>"structured"</c>.
    /// </summary>
    string CloudEventsContentMode { get; }

    /// <summary>
    /// Recommended CloudEvents <c>source</c> convention for this endpoint, or
    /// <c>null</c> when not declared.
    /// </summary>
    string? CloudEventsSource { get; }
}
