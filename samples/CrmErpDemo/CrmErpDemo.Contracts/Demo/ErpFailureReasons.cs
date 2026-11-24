namespace CrmErpDemo.Contracts.Demo;

/// <summary>
/// One selectable failure the ERP adapter can simulate while error mode is on.
/// </summary>
/// <param name="Id">Stable identifier sent over the admin API and stored in <c>ErrorModeState</c>.</param>
/// <param name="Title">Short label for the erp-web dropdown.</param>
/// <param name="Description">What an operator sees and why the failure looks the way it does.</param>
/// <param name="ExpectedCategory">
/// The Integration Intelligence <c>failure_category</c> this failure is designed to be
/// classified as. Advisory: the provider decides, this is what the evidence points at.
/// </param>
/// <param name="Disposition">
/// How NimBus treats the thrown exception: <c>Failed</c> (session blocked, retryable,
/// resubmit from the WebApp) or <c>DeadLettered</c> (permanent, straight to the DLQ).
/// </param>
public sealed record ErpFailureReason(
    string Id,
    string Title,
    string Description,
    string ExpectedCategory,
    string Disposition);

/// <summary>
/// The catalog of failure reasons behind the erp-web <em>Error mode</em> toggle, shared by
/// <c>Erp.Api</c> (which stores and validates the selection), <c>Erp.Adapter.Functions</c>
/// (which throws the matching exception from every handler) and, over the admin API, the
/// erp-web SPA (which renders the dropdown).
/// </summary>
/// <remarks>
/// <para>
/// NimBus records only the exception's type name and message as the failure evidence
/// (<c>ErrorContent.ErrorType</c> / <c>ErrorText</c>, no stack trace), so each reason's message
/// carries the cues a real failure of that kind would: status codes, identifiers, the
/// downstream system, and what would have to change. That is what Integration Intelligence
/// classifies. Reason ids match the classification category they are meant to land in.
/// </para>
/// <para>
/// A written message must never contradict the message it fails: whoever classifies the failure,
/// a person or an agent, compares the two and stops trusting the evidence when they disagree. So
/// no reason here quotes a field of the payload. Failures that depend on the data are real ones
/// instead: ERP rejects a country that is not an ISO code (<c>Erp.Api</c>'s <c>CountryCodes</c>),
/// and <see cref="ApplicationDefect"/> runs a real defect in the adapter.
/// </para>
/// </remarks>
public static class ErpFailureReasons
{
    /// <summary>The original, generic error-mode failure. Selected when no reason has been chosen.</summary>
    public const string Default = "handler_exception";

    /// <summary>
    /// The one reason that is not a thrown, written exception: the adapter runs a mapping step
    /// with a real defect, which fails only for the messages the defect mishandles.
    /// </summary>
    public const string ApplicationDefect = "application_defect";

    public static readonly IReadOnlyList<ErpFailureReason> All =
    [
        new(
            Default,
            "Generic handler exception",
            "The original error-mode behaviour: every handler throws a bare demo exception with no diagnostic detail, which is deliberately hard to classify.",
            "unknown",
            "Failed"),
        new(
            "transient_dependency",
            "Downstream timeout (transient)",
            "The ERP pricing service is unreachable: HTTP 503 after a 30s timeout with a connection reset. Nothing about the message needs to change; a retry later is expected to succeed.",
            "transient_dependency",
            "Failed"),
        new(
            "authentication_configuration",
            "Expired credentials (auth/config)",
            "The ERP API answers 401 Unauthorized because the adapter's client secret expired. Retrying is pointless until the secret is renewed in configuration.",
            "authentication_configuration",
            "Failed"),
        new(
            "business_rule",
            "Customer closed (business rule)",
            "Technically valid data rejected by a domain rule: the ERP customer is in status Closed and cannot be modified. Someone must reopen the customer before a replay can succeed.",
            "business_rule",
            "Failed"),
        new(
            ApplicationDefect,
            "Missing posting group (application defect)",
            "Runs a real mapping step in the adapter that has a real defect: its country table has no entry for Finland, so accounts in FI " +
            "(and any country outside the table) fail with a KeyNotFoundException that quotes the country. Other messages go through. Only a code change fixes it.",
            "application_defect",
            "Failed"),
    ];

    /// <summary>The selection that throws a different catalog failure, picked at random, for each failing message.</summary>
    public const string Random = "random";

    /// <summary>
    /// The dropdown entry for <see cref="Random"/>. It is not in <see cref="All"/>: it is a way
    /// to choose among the failures there, never a failure that is thrown.
    /// </summary>
    public static readonly ErpFailureReason RandomReason = new(
        Random,
        "Random (any failure type)",
        "Each failing message throws one of the other failure types, picked at random, so NimBus sees a mix of error types to classify.",
        "varies",
        "Failed");

    /// <summary>
    /// The selection that throws the transient failure for most failing messages and one of the
    /// other catalog failures for the rest: the mix an integration sees on an ordinary bad day.
    /// </summary>
    public const string MostlyTransient = "mostly_transient";

    /// <summary>The share of <see cref="MostlyTransient"/>'s failures that are the transient one.</summary>
    public const double TransientShare = 0.7;

    private const string Transient = "transient_dependency";

    /// <summary>
    /// The dropdown entry for <see cref="MostlyTransient"/>. Like <see cref="RandomReason"/> it is
    /// not in <see cref="All"/>: it chooses among the failures there.
    /// </summary>
    public static readonly ErpFailureReason MostlyTransientReason = new(
        MostlyTransient,
        "Mostly transient (realistic mix)",
        "About 7 in 10 failing messages hit the downstream timeout, which a retry gets past; the rest draw one of the other failure types. " +
        "With a failure rate below 100% this is the mix where most failures can be recovered without a person.",
        "varies",
        "Failed");

    /// <summary>
    /// What erp-web offers: every failure in <see cref="All"/>, then the two selections that pick
    /// among them, <see cref="MostlyTransientReason"/> and <see cref="RandomReason"/>.
    /// </summary>
    public static readonly IReadOnlyList<ErpFailureReason> Selectable = [.. All, MostlyTransientReason, RandomReason];

    /// <summary>Looks a selectable reason up by id, case-insensitively. Null when unknown.</summary>
    public static ErpFailureReason? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Selectable.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Decides what one message does while error mode is on: null when it goes through, otherwise
    /// the failure to throw. A message fails when a roll in [0, 1) is below
    /// <paramref name="failureRate"/>, so 1 fails every message and 0 none. <see cref="Random"/>
    /// picks one failure from <see cref="All"/> evenly and <see cref="MostlyTransient"/> picks the
    /// transient one for <see cref="TransientShare"/> of them; an unknown id throws the generic failure.
    /// </summary>
    /// <param name="failureRate">The share of messages that fail, from 0 to 1.</param>
    /// <param name="reasonId">The reason selected in erp-web.</param>
    /// <param name="rng">The source of both the roll and the random pick.</param>
    public static ErpFailureReason? Decide(double failureRate, string? reasonId, System.Random rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        if (rng.NextDouble() >= failureRate)
            return null;

        return Resolve(Find(reasonId) ?? All[0], rng);
    }

    /// <summary>Turns a selection into the failure to throw: itself, or for a mix the failure it draws.</summary>
    private static ErpFailureReason Resolve(ErpFailureReason reason, System.Random rng)
    {
        switch (reason.Id)
        {
            case Random:
                return All[rng.Next(All.Count)];
            case MostlyTransient:
                if (rng.NextDouble() < TransientShare)
                    return All.First(r => r.Id == Transient);

                var others = All.Where(r => r.Id != Transient).ToList();
                return others[rng.Next(others.Count)];
            default:
                return reason;
        }
    }

    /// <summary>
    /// Creates the exception the adapter throws for <paramref name="reasonId"/>, the id of a
    /// failure in <see cref="All"/>. Unknown ids fall back to <see cref="Default"/> so a stale
    /// selection can never turn error mode off; so do the two mixes, which <see cref="Decide"/>
    /// resolves, and <see cref="ApplicationDefect"/>, which the adapter runs instead of throwing.
    /// </summary>
    /// <param name="reasonId">The reason <see cref="Decide"/> returned.</param>
    /// <param name="eventType">The inbound event type. Kept for callers; no message quotes the payload.</param>
    public static Exception CreateException(string? reasonId, string eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        var reason = Find(reasonId) ?? All[0];

        return reason.Id switch
        {
            Transient => new TimeoutException(
                "ERP pricing service did not respond within 30s: HTTP 503 Service Unavailable from https://erp-pricing.internal/api/v2/customers " +
                "(socket error: connection reset by peer). The service was reachable a moment ago and is usually back within minutes."),

            "authentication_configuration" => new UnauthorizedAccessException(
                "ERP API rejected the request with HTTP 401 Unauthorized (WWW-Authenticate: Bearer error=\"invalid_token\", error_description=\"The client secret has expired\"). " +
                "The client secret for app registration 'erp-adapter' expired on 2026-09-01; renew it and update ErpApi:ClientSecret."),

            "business_rule" => new InvalidOperationException(
                "ERP rejected the change: customer C-100245 is in status Closed and cannot be modified (rule ERP-CUST-017). " +
                "Reopen the customer in ERP before replaying this message."),

            _ => new HandlerErrorModeException(),
        };
    }
}

/// <summary>
/// The generic error-mode exception. Deliberately uninformative: it is the control case that
/// shows what Integration Intelligence does with no usable evidence.
/// </summary>
public sealed class HandlerErrorModeException()
    : Exception("ERP is in error mode — handlers are configured to throw for every inbound message.");
