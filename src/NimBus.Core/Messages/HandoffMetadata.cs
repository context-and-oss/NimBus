using System;

namespace NimBus.Core.Messages;

/// <summary>
/// Metadata supplied by the handler when signalling
/// <see cref="HandlerOutcome.PendingHandoff"/>. <paramref name="ExpectedBy"/>
/// is a duration; the subscriber converts it to an absolute UTC deadline
/// when constructing the outgoing PendingHandoffResponse.
/// </summary>
public sealed record HandoffMetadata(string Reason, string? ExternalJobId, TimeSpan? ExpectedBy);
