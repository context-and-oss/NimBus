using System;

namespace NimBus.Core.CloudEvents;

/// <summary>
/// Configurable mapping between NimBus identity/metadata and CloudEvents
/// attributes. Applied symmetrically on publish (native → CloudEvent) and on
/// consume (CloudEvent → native), so a mapping override is honored in both
/// directions.
/// <para>
/// The core attributes (<c>id</c> ← MessageId, <c>type</c> ← event contract,
/// <c>source</c> ← originating endpoint, <c>data</c> ← domain-event payload,
/// <c>datacontenttype</c> ← content type, <c>dataschema</c> ← schema ref) are
/// fixed. This type controls where the two NimBus concepts that have no natural
/// CloudEvents core attribute — <c>CorrelationId</c> and <c>SessionId</c> — are
/// carried.
/// </para>
/// </summary>
public sealed class CloudEventMapping
{
    /// <summary>Sentinel value selecting the CloudEvents <c>subject</c> core attribute.</summary>
    public const string SubjectAttribute = "subject";

    /// <summary>
    /// Attribute carrying <c>CorrelationId</c>. Defaults to the extension
    /// attribute <c>correlationid</c>; set to <see cref="SubjectAttribute"/> to
    /// use the CloudEvents <c>subject</c>, or any other name for a custom
    /// extension.
    /// </summary>
    public string CorrelationIdAttribute { get; set; } = "correlationid";

    /// <summary>
    /// Attribute carrying <c>SessionId</c>. Defaults to the extension attribute
    /// <c>sessionid</c>; set to <see cref="SubjectAttribute"/> to use the
    /// CloudEvents <c>subject</c>, or any other name for a custom extension.
    /// </summary>
    public string SessionIdAttribute { get; set; } = "sessionid";

    /// <summary>Writes <c>CorrelationId</c> onto the CloudEvent per the configured mapping.</summary>
    public void WriteCorrelationId(CloudEvent cloudEvent, string? value) =>
        Write(cloudEvent, CorrelationIdAttribute, value);

    /// <summary>Writes <c>SessionId</c> onto the CloudEvent per the configured mapping.</summary>
    public void WriteSessionId(CloudEvent cloudEvent, string? value) =>
        Write(cloudEvent, SessionIdAttribute, value);

    /// <summary>Reads <c>CorrelationId</c> from the CloudEvent per the configured mapping.</summary>
    public string? ReadCorrelationId(CloudEvent cloudEvent) =>
        Read(cloudEvent, CorrelationIdAttribute);

    /// <summary>Reads <c>SessionId</c> from the CloudEvent per the configured mapping.</summary>
    public string? ReadSessionId(CloudEvent cloudEvent) =>
        Read(cloudEvent, SessionIdAttribute);

    private static void Write(CloudEvent? cloudEvent, string attribute, string? value)
    {
        if (cloudEvent is null || string.IsNullOrEmpty(value) || string.IsNullOrEmpty(attribute))
            return;

        if (string.Equals(attribute, SubjectAttribute, StringComparison.OrdinalIgnoreCase))
            cloudEvent.Subject = value;
        else
            cloudEvent.Extensions[attribute] = value;
    }

    private static string? Read(CloudEvent? cloudEvent, string attribute)
    {
        if (cloudEvent is null || string.IsNullOrEmpty(attribute))
            return null;

        if (string.Equals(attribute, SubjectAttribute, StringComparison.OrdinalIgnoreCase))
            return cloudEvent.Subject;

        return cloudEvent.Extensions.TryGetValue(attribute, out var value) ? value : null;
    }
}
