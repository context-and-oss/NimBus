using System;

namespace NimBus.Core.Events;

/// <summary>
/// Enriches the AsyncAPI message that <c>nb asyncapi export</c> (and the back-compatible
/// <c>nb catalog asyncapi</c>) generates for an event contract. Optional: when absent, the
/// exporter falls back to the type name and any
/// <see cref="System.ComponentModel.DescriptionAttribute"/>. Apply to an event class:
/// <code>[AsyncApiMessage(Title = "Customer created", Summary = "Published when a customer is created", Tags = new[] { "CRM" })]</code>
/// <para>
/// Fluent per-publish configuration (<c>AddNimBusPublisher(endpoint, b =&gt; b.Publish&lt;T&gt;(o =&gt; o.AsyncApi…))</c>)
/// can supply the same values; when both are present the fluent value wins for scalars, tags and
/// examples are unioned, and <see cref="Deprecated"/> is OR-ed.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class AsyncApiMessageAttribute : Attribute
{
    /// <summary>Human-friendly message title (AsyncAPI <c>message.title</c>). Defaults to the type name.</summary>
    public string? Title { get; set; }

    /// <summary>Short one-line summary (AsyncAPI <c>message.summary</c>). Defaults to the <c>[Description]</c>.</summary>
    public string? Summary { get; set; }

    /// <summary>Longer description (AsyncAPI <c>message.description</c>).</summary>
    public string? Description { get; set; }

    /// <summary>Governance tags (AsyncAPI <c>message.tags</c>), e.g. owning team or business capability.</summary>
    public string[]? Tags { get; set; }

    /// <summary>
    /// Custom message/schema name. Surfaces as AsyncAPI <c>message.name</c> and as the payload
    /// schema's JSON-Schema <c>title</c>. Distinct from <see cref="Title"/> (a display label →
    /// <c>message.title</c>). The component key stays the event id so <c>$ref</c>s never dangle.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Owning individual/role (governance). Surfaces via <c>x-nimbus-governance.owner</c>.</summary>
    public string? Owner { get; set; }

    /// <summary>Owning team (governance). Surfaces via <c>x-nimbus-governance.team</c>.</summary>
    public string? Team { get; set; }

    /// <summary>Business capability this event belongs to (governance). Surfaces via <c>x-nimbus-governance.businessCapability</c>.</summary>
    public string? BusinessCapability { get; set; }

    /// <summary>Contract version (governance). Surfaces via <c>x-nimbus-governance.version</c>.</summary>
    public string? Version { get; set; }

    /// <summary>
    /// Marks the contract deprecated. Sets the JSON-Schema/AsyncAPI <c>deprecated</c> marker on the
    /// payload Schema Object and mirrors it as <c>x-nimbus-governance.deprecated</c> on the message.
    /// </summary>
    public bool Deprecated { get; set; }

    /// <summary>External documentation URL (AsyncAPI <c>message.externalDocs.url</c>).</summary>
    public string? ExternalDocsUrl { get; set; }

    /// <summary>External documentation description (AsyncAPI <c>message.externalDocs.description</c>).</summary>
    public string? ExternalDocsDescription { get; set; }
}
