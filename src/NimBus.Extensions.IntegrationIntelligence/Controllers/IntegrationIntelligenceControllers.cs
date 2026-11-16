using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace NimBus.Extensions.IntegrationIntelligence.Controllers;

/// <summary>Returns feature capability for a specific endpoint.</summary>
[ApiController]
[Authorize]
[Route("api/integration-intelligence/status")]
public sealed class IntegrationIntelligenceStatusController : ControllerBase
{
    private readonly IntegrationIntelligenceStatusService _status;

    /// <summary>Creates the status controller.</summary>
    public IntegrationIntelligenceStatusController(IntegrationIntelligenceStatusService status) => _status = status;

    /// <summary>Gets capability for an endpoint.</summary>
    [HttpGet]
    public async Task<IActionResult> GetStatus([FromQuery] string? endpointId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpointId)) return BadRequest(new { code = "EndpointRequired" });
        try
        {
            var status = await _status.GetAsync(endpointId, cancellationToken).ConfigureAwait(false);
            return status is null ? NotFound(new { code = "EndpointNotFound" }) : Ok(status);
        }
        catch (ClassificationServiceException exception)
        {
            return StatusCode(exception.StatusCode, new { code = exception.Code });
        }
        catch (Exception) { return StatusCode(503, new { code = "ClassificationUnavailable" }); }
    }
}

/// <summary>
/// Serves saved classification results. Available whenever classification storage is readable,
/// including read-only mode (spec 043). Agents record new results over MCP.
/// </summary>
[ApiController]
[Authorize]
[Route("api/integration-intelligence/failures/{eventId}/{messageId}/classification")]
public sealed class IntegrationIntelligenceReadController : ControllerBase
{
    private readonly FailureClassificationReader _reader;

    /// <summary>Creates the read controller.</summary>
    public IntegrationIntelligenceReadController(FailureClassificationReader reader) => _reader = reader;

    /// <summary>Returns the latest classification.</summary>
    [HttpGet]
    public async Task<IActionResult> GetClassification(string eventId, string messageId, CancellationToken cancellationToken)
        => await ClassificationResults.ExecuteAsync(() => _reader.GetLatestAsync(eventId, messageId, cancellationToken)).ConfigureAwait(false);

    /// <summary>Returns all classification revisions.</summary>
    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(string eventId, string messageId, CancellationToken cancellationToken)
        => await ClassificationResults.ExecuteAsync(() => _reader.GetHistoryAsync(eventId, messageId, cancellationToken)).ConfigureAwait(false);
}

/// <summary>Maps classification outcomes to the routes' stable error bodies.</summary>
internal static class ClassificationResults
{
    internal static async Task<IActionResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            var value = await action().ConfigureAwait(false);
            return value is null ? new NotFoundObjectResult(new { code = "ClassificationNotFound" }) : new OkObjectResult(value);
        }
        catch (ClassificationServiceException exception)
        {
            return new ObjectResult(new { code = exception.Code }) { StatusCode = exception.StatusCode };
        }
        catch (Exception) { return new ObjectResult(new { code = "ClassificationUnavailable" }) { StatusCode = 503 }; }
    }
}
