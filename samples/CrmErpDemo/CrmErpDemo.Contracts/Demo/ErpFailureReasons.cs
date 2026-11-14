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
/// NimBus records only the exception's type name and message as the failure evidence
/// (<c>ErrorContent.ErrorType</c> / <c>ErrorText</c>, no stack trace), so each reason's message
/// carries the cues a real failure of that kind would: status codes, identifiers, the
/// downstream system, and what would have to change. That is what Integration Intelligence
/// classifies. Reason ids match the classification category they are meant to land in.
/// </remarks>
public static class ErpFailureReasons
{
    /// <summary>The original, generic error-mode failure. Selected when no reason has been chosen.</summary>
    public const string Default = "handler_exception";

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
            "contract_schema",
            "Schema mismatch (contract)",
            "The payload fails deserialization: CountryCode carries a full country name instead of an ISO 3166-1 alpha-2 code. It is not dead-lettered: that needs a permanent-failure classifier, which the ERP adapter does not register.",
            "contract_schema",
            "Failed"),
        new(
            "business_rule",
            "Customer closed (business rule)",
            "Technically valid data rejected by a domain rule: the ERP customer is in status Closed and cannot be modified. Someone must reopen the customer before a replay can succeed.",
            "business_rule",
            "Failed"),
        new(
            "missing_reference_data",
            "Customer not found (missing reference)",
            "The CRM account referenced by the message has no ERP customer yet, typically because the CrmAccountCreated event has not been processed. Replaying after the reference exists succeeds.",
            "missing_reference_data",
            "Failed"),
        new(
            "application_defect",
            "Null reference (application defect)",
            "A programming error inside the adapter's mapping code: a NullReferenceException where an address was assumed to be present. Only a code change fixes it.",
            "application_defect",
            "Failed"),
    ];

    /// <summary>Looks a reason up by id, case-insensitively. Null when unknown.</summary>
    public static ErpFailureReason? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Creates the exception the adapter throws for <paramref name="reasonId"/>. Unknown ids
    /// fall back to <see cref="Default"/> so a stale selection can never turn error mode off.
    /// </summary>
    /// <param name="reasonId">The selected reason.</param>
    /// <param name="eventType">The inbound event type, quoted in messages that mention the payload.</param>
    public static Exception CreateException(string? reasonId, string eventType)
    {
        var reason = Find(reasonId) ?? All[0];
        return reason.Id switch
        {
            "transient_dependency" => new TimeoutException(
                "ERP pricing service did not respond within 30s: HTTP 503 Service Unavailable from https://erp-pricing.internal/api/v2/customers " +
                "(socket error: connection reset by peer). The service was reachable a moment ago and is usually back within minutes."),

            "authentication_configuration" => new UnauthorizedAccessException(
                "ERP API rejected the request with HTTP 401 Unauthorized (WWW-Authenticate: Bearer error=\"invalid_token\", error_description=\"The client secret has expired\"). " +
                "The client secret for app registration 'erp-adapter' expired on 2026-09-01; renew it and update ErpApi:ClientSecret."),

            "contract_schema" => new FormatException(
                $"Could not deserialize {eventType}: property 'CountryCode' must be an ISO 3166-1 alpha-2 code (2 characters) but was 'Denmark'. " +
                "The producer appears to send contract version 2.1 while this consumer expects 2.0."),

            "business_rule" => new InvalidOperationException(
                "ERP rejected the change: customer C-100245 is in status Closed and cannot be modified (rule ERP-CUST-017). " +
                "Reopen the customer in ERP before replaying this message."),

            "missing_reference_data" => new KeyNotFoundException(
                "ERP customer for CRM account 6f1c0a5e-4b2d-4e8a-9c3f-1a2b3c4d5e01 was not found. " +
                "The CrmAccountCreated event for this account has not been processed yet, so there is no Customer row to attach the change to."),

#pragma warning disable CA2201 // The demo deliberately reproduces a real NullReferenceException as failure evidence.
            "application_defect" => new NullReferenceException(
                "Object reference not set to an instance of an object. " +
                "(Erp.Adapter.Functions.Mapping.CustomerMapper.MapAddress: dto.Address was null; the mapper assumes every customer has a billing address)"),
#pragma warning restore CA2201

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
