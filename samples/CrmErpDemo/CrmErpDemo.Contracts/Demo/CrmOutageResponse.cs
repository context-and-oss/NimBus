namespace CrmErpDemo.Contracts.Demo;

/// <summary>
/// What the CRM adapter's HTTP client receives while crm-web's <em>CRM API outage</em> switch is on.
/// </summary>
/// <remarks>
/// The reason phrase and body end up in the handler's exception message, which NimBus records as
/// the failure evidence. They read as a gateway reporting a real, temporary outage and never name
/// the demo switch: evidence that says the outage is a setting someone turned on is classified as
/// a failure only a person can end, so neither an operator nor an agent would retry it.
/// <para>
/// The response carries no <c>Retry-After</c>. The adapter's HTTP client retries a 503 inside a
/// 30-second budget and honours that header, so a long one makes the client wait the budget out
/// and fail with its own timeout, and the text here never reaches the failure evidence.
/// </para>
/// </remarks>
public static class CrmOutageResponse
{
    /// <summary>The standard reason phrase of a 503.</summary>
    public const string ReasonPhrase = "Service Unavailable";

    /// <summary>The response body, as a gateway in front of an unreachable service writes it.</summary>
    public const string Body =
        "The CRM API is temporarily unavailable: upstream connect error or disconnect/reset before headers " +
        "(reset reason: connection failure). Retry the request later.";
}
