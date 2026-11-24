using System.Net;
using System.Net.Http.Headers;
using CrmErpDemo.Contracts.Demo;

namespace Crm.Adapter.Clients;

/// <summary>
/// Circuit-breaker showcase: when the crm-api error-mode flag is on, every
/// ICrmApiClient call short-circuits to a synthetic 503 before leaving the
/// process, written as a real outage would be (see <see cref="CrmOutageResponse"/>).
/// All CRM handlers then fail through the authentic
/// CrmApiClient.EnsureSuccessOrThrowAsync path — a sustained, retry-classified
/// failure spike with zero handler edits, exactly what opens the circuit.
/// Sits below the handler (not in the NimBus pipeline) because the circuit
/// recorder is the innermost pipeline wrapper: only failures thrown inside
/// handler execution count toward the breaker.
/// </summary>
public sealed class CrmOutageSimulationHandler(IErrorModeClient errorMode) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (await errorMode.IsErrorModeEnabledAsync(cancellationToken))
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                RequestMessage = request,
                ReasonPhrase = CrmOutageResponse.ReasonPhrase,
                Content = new StringContent(CrmOutageResponse.Body),
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(CrmOutageResponse.RetryAfter);
            return response;
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
