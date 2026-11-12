using System.Text.Json;

namespace FailureAgent.Runner;

/// <summary>The outcome of checking <c>nimbus_get_capabilities</c> before a run.</summary>
/// <param name="Errors">Problems that stop the run.</param>
/// <param name="Warnings">Limits the run works within, for the log.</param>
/// <param name="CanRetry">Whether the workload may resubmit unattended.</param>
public sealed record CapabilityCheckResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, bool CanRetry)
{
    /// <summary>Whether the run may start.</summary>
    public bool Ok => Errors.Count == 0;
}

/// <summary>Checks that the identity the runner signed in with can do the work.</summary>
public static class CapabilityCheck
{
    /// <summary>Checks the structured result of <c>nimbus_get_capabilities</c>.</summary>
    /// <param name="capabilities">The tool's structured content.</param>
    /// <param name="authentication">How the runner signed in.</param>
    public static CapabilityCheckResult Evaluate(JsonElement capabilities, RunnerAuthentication authentication)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        var isWorkload = capabilities.TryGetProperty("caller", out var caller)
            && caller.TryGetProperty("isWorkload", out var workload) && workload.ValueKind == JsonValueKind.True;
        if (authentication == RunnerAuthentication.Entra && !isWorkload)
            errors.Add("The token is not an app-only workload token. Use the runner's own identity, not a person's.");

        var permitted = capabilities.TryGetProperty("permittedActions", out var actions) && actions.ValueKind == JsonValueKind.Array
            ? actions.EnumerateArray().Select(a => a.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : [];
        if (!permitted.Contains("classify"))
            errors.Add("classify is not in permittedActions: give the runner the Nimbus.Classify app role, Contributor on the endpoints, and turn on Workloads may record classifications.");

        var recordable = capabilities.TryGetProperty("features", out var features)
            && features.TryGetProperty("canRecordClassifications", out var record) && record.ValueKind == JsonValueKind.True;
        if (!recordable)
            errors.Add("This deployment cannot record classifications: turn on failure classification with Provider None.");

        var readable = capabilities.TryGetProperty("readableEndpoints", out var endpoints) && endpoints.ValueKind == JsonValueKind.Array
            ? endpoints.GetArrayLength()
            : 0;
        if (readable == 0)
            errors.Add("The runner can read no endpoints: grant its object id Reader or Contributor in Access Control.");

        var canRetry = permitted.Contains("resubmit");
        if (!canRetry)
            warnings.Add("resubmit is not permitted: transient failures are handed to a person instead of retried.");
        if (!permitted.Contains("report"))
            warnings.Add("report is not permitted: reports and bugs are filed but the failures are not marked reported.");
        if (isWorkload && canRetry && capabilities.TryGetProperty("unattended", out var unattended) && unattended.ValueKind == JsonValueKind.Object)
        {
            var limit = unattended.GetProperty("resubmitsPerWindow").GetInt32();
            var used = unattended.GetProperty("resubmitsUsed").GetInt32();
            warnings.Add($"Resubmit budget: {limit - used} of {limit} left in the last {unattended.GetProperty("windowHours").GetInt32()} hours.");
        }

        return new CapabilityCheckResult(errors, warnings, canRetry);
    }
}
