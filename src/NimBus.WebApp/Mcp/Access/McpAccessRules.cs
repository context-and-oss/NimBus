using System.Net.Mail;
using NimBus.MessageStore.States;
using NimBus.WebApp.RateLimiting;

namespace NimBus.WebApp.Mcp.Access;

/// <summary>One difference between two MCP access policies, as the review and the audit log show it.</summary>
/// <param name="Text">What changed, in a sentence.</param>
/// <param name="Widens">True when the change lets agents do more.</param>
public sealed record McpAccessChange(string Text, bool Widens);

/// <summary>Validation and change classification for the MCP access policy (Spec 037 §5.2, §6.4).</summary>
public static class McpAccessRules
{
    /// <summary>Most principals on the people allowlist.</summary>
    public const int MaxPrincipals = 100;

    /// <summary>Most approved clients.</summary>
    public const int MaxClients = 50;

    /// <summary>Most endpoint ids per list.</summary>
    public const int MaxEndpoints = 200;

    /// <summary>
    /// Trims and de-duplicates <paramref name="settings"/> in place, puts endpoint ids in the
    /// catalog's spelling and returns the validation errors; empty when the policy is valid.
    /// </summary>
    public static IReadOnlyList<string> Normalize(McpAccessSettings settings, IReadOnlyCollection<string> catalogEndpoints, RateLimitOptions rateLimits)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(catalogEndpoints);
        ArgumentNullException.ThrowIfNull(rateLimits);

        var errors = new List<string>();
        settings.Id = McpAccessSettings.SingletonId;
        settings.Capabilities ??= new McpCapabilitySettings();
        settings.People ??= new McpPeopleSettings();
        settings.Clients ??= new McpClientSettings();
        settings.Endpoints ??= new McpEndpointSettings();
        settings.Limits ??= new McpLimitSettings();

        NormalizePeople(settings.People, errors);
        NormalizeClients(settings.Clients, errors);
        NormalizeEndpoints(settings.Endpoints, catalogEndpoints, errors);
        CheckLimits(settings.Limits, rateLimits, errors);
        if (settings.WorkloadResubmitsPerDay is < 0 or > McpAccessSettings.MaximumWorkloadResubmitsPerDay)
            errors.Add($"Workload resubmits per day must be between 0 and {McpAccessSettings.MaximumWorkloadResubmitsPerDay}.");
        if (settings.WorkloadResubmitsPerMessage is < 1 or > McpAccessSettings.MaximumWorkloadResubmitsPerMessage)
            errors.Add($"Unsuccessful workload resubmits per message must be between 1 and {McpAccessSettings.MaximumWorkloadResubmitsPerMessage}.");
        return errors;
    }

    /// <summary>The differences from <paramref name="before"/> to <paramref name="after"/>.</summary>
    public static IReadOnlyList<McpAccessChange> Changes(McpAccessSettings before, McpAccessSettings after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changes = new List<McpAccessChange>();
        void Add(string text, bool widens) => changes.Add(new McpAccessChange(text, widens));

        if (before.Enabled != after.Enabled)
            Add(after.Enabled ? "Turn the MCP endpoint on" : "Turn the MCP endpoint off", after.Enabled);

        foreach (var (name, was, now) in new[]
        {
            ("raw payloads", before.Capabilities.Payloads, after.Capabilities.Payloads),
            ("marking messages reported", before.Capabilities.Report, after.Capabilities.Report),
            ("classifying failures", before.Capabilities.Classify, after.Capabilities.Classify),
            ("resubmitting messages", before.Capabilities.Resubmit, after.Capabilities.Resubmit),
            ("skipping messages", before.Capabilities.Skip, after.Capabilities.Skip),
        })
        {
            if (was != now)
                Add($"{(now ? "Allow" : "Stop")} {name}", now);
        }

        if (before.AllowWorkloads != after.AllowWorkloads)
            Add(after.AllowWorkloads ? "Accept workload (app-only) tokens" : "Refuse workload (app-only) tokens", after.AllowWorkloads);

        if (before.AllowWorkloadClassification != after.AllowWorkloadClassification)
            Add(after.AllowWorkloadClassification ? "Let workloads record failure classifications" : "Stop workloads recording failure classifications",
                after.AllowWorkloadClassification);

        if (before.AllowWorkloadResubmit != after.AllowWorkloadResubmit)
            Add(after.AllowWorkloadResubmit ? "Let workloads resubmit failed messages unattended" : "Stop workloads resubmitting messages",
                after.AllowWorkloadResubmit);

        if (before.AllowWorkloadAnnotation != after.AllowWorkloadAnnotation)
            Add(after.AllowWorkloadAnnotation ? "Let workloads mark messages reported" : "Stop workloads marking messages reported",
                after.AllowWorkloadAnnotation);

        if (before.WorkloadResubmitsPerDay != after.WorkloadResubmitsPerDay)
            Add($"Workload resubmits per day: {before.WorkloadResubmitsPerDay} → {after.WorkloadResubmitsPerDay}",
                after.WorkloadResubmitsPerDay > before.WorkloadResubmitsPerDay);

        if (before.WorkloadResubmitsPerMessage != after.WorkloadResubmitsPerMessage)
            Add($"Unsuccessful workload resubmits per message: {before.WorkloadResubmitsPerMessage} → {after.WorkloadResubmitsPerMessage}",
                after.WorkloadResubmitsPerMessage > before.WorkloadResubmitsPerMessage);

        // People
        if (before.People.Mode != after.People.Mode)
        {
            Add(after.People.Mode == McpPeopleMode.All
                ? "Let everyone with a NimBus role connect"
                : $"Only listed users and groups may connect ({string.Join(", ", after.People.Principals.Select(p => p.Principal))})",
                after.People.Mode == McpPeopleMode.All);
        }
        else if (after.People.Mode == McpPeopleMode.Listed)
        {
            ListChanges(Names(before.People.Principals.Select(p => p.Principal)), Names(after.People.Principals.Select(p => p.Principal)),
                added => Add($"Let {added} connect", true), removed => Add($"Stop {removed} connecting", false));
        }

        // Clients
        if (before.Clients.Mode != after.Clients.Mode)
        {
            Add(after.Clients.Mode == McpClientMode.Any
                ? "Accept any client pre-authorized in Entra"
                : $"Accept only approved clients ({string.Join(", ", after.Clients.Approved.Select(c => c.Name))})",
                after.Clients.Mode == McpClientMode.Any);
        }
        else if (after.Clients.Mode == McpClientMode.Approved)
        {
            var was = before.Clients.Approved.ToDictionary(c => c.ClientId, StringComparer.OrdinalIgnoreCase);
            var now = after.Clients.Approved.ToDictionary(c => c.ClientId, StringComparer.OrdinalIgnoreCase);
            foreach (var client in after.Clients.Approved)
            {
                if (!was.TryGetValue(client.ClientId, out var old))
                    Add($"Approve client {client.Name}{(client.MayChange ? ", which may change messages" : string.Empty)}", true);
                else if (old.MayChange != client.MayChange)
                    Add(client.MayChange ? $"Let client {client.Name} change messages" : $"Stop client {client.Name} changing messages", client.MayChange);
            }

            foreach (var client in before.Clients.Approved.Where(c => !now.ContainsKey(c.ClientId)))
                Add($"Remove client {client.Name}", false);
        }

        // Endpoints
        if (before.Endpoints.Visibility != after.Endpoints.Visibility)
        {
            Add(after.Endpoints.Visibility == McpEndpointVisibility.All
                ? "Show agents every endpoint they can read"
                : $"Hide {string.Join(", ", after.Endpoints.Hidden)} from agents",
                after.Endpoints.Visibility == McpEndpointVisibility.All);
        }
        else if (after.Endpoints.Visibility == McpEndpointVisibility.AllExcept)
        {
            ListChanges(Names(before.Endpoints.Hidden), Names(after.Endpoints.Hidden),
                added => Add($"Hide {added} from agents", false), removed => Add($"Show {removed} to agents again", true));
        }

        if (before.Endpoints.Changes != after.Endpoints.Changes)
        {
            Add(after.Endpoints.Changes == McpChangeScope.AllVisible
                ? "Allow changes on every visible endpoint"
                : $"Allow changes only on {string.Join(", ", after.Endpoints.ChangeOn)}",
                after.Endpoints.Changes == McpChangeScope.AllVisible);
        }
        else if (after.Endpoints.Changes == McpChangeScope.Listed)
        {
            ListChanges(Names(before.Endpoints.ChangeOn), Names(after.Endpoints.ChangeOn),
                added => Add($"Allow changes on {added}", true), removed => Add($"Stop changes on {removed}", false));
        }

        // Limits
        LimitChange("Tool calls per window", before.Limits.RequestsPerWindow, after.Limits.RequestsPerWindow, Add);
        LimitChange("Message changes per window", before.Limits.MutationsPerWindow, after.Limits.MutationsPerWindow, Add);

        return changes;
    }

    private static void NormalizePeople(McpPeopleSettings people, List<string> errors)
    {
        people.Principals = (people.Principals ?? [])
            .Where(p => p is not null)
            .Select(p => new McpPrincipal
            {
                Principal = (p.Principal ?? string.Empty).Trim(),
                Label = string.IsNullOrWhiteSpace(p.Label) ? null : p.Label.Trim(),
            })
            .GroupBy(p => p.Principal, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (people.Principals.Count > MaxPrincipals)
            errors.Add($"At most {MaxPrincipals} users and groups.");
        foreach (var principal in people.Principals)
        {
            if (principal.Principal.Length is 0 or > 256 || !(IsEmail(principal.Principal) || Guid.TryParse(principal.Principal, out _)))
                errors.Add($"'{Clip(principal.Principal)}' is not an email address or an object id.");
            if (principal.Label?.Length > 100)
                errors.Add($"The label for '{Clip(principal.Principal)}' is longer than 100 characters.");
        }

        if (people.Mode == McpPeopleMode.Listed && people.Principals.Count == 0)
            errors.Add("Add at least one user or group, or let everyone with a NimBus role connect.");
    }

    private static void NormalizeClients(McpClientSettings clients, List<string> errors)
    {
        clients.Approved = (clients.Approved ?? [])
            .Where(c => c is not null)
            .Select(c => new McpApprovedClient
            {
                ClientId = (c.ClientId ?? string.Empty).Trim(),
                Name = (c.Name ?? string.Empty).Trim(),
                MayChange = c.MayChange,
            })
            .ToList();

        if (clients.Approved.Count > MaxClients)
            errors.Add($"At most {MaxClients} approved clients.");
        foreach (var client in clients.Approved)
        {
            if (!Guid.TryParse(client.ClientId, out var id))
                errors.Add($"'{Clip(client.ClientId)}' is not an application (client) id.");
            else
                client.ClientId = id.ToString();
            if (client.Name.Length is 0 or > 100)
                errors.Add("Every approved client needs a name of 1-100 characters.");
        }

        if (clients.Approved.GroupBy(c => c.ClientId, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            errors.Add("Each client can be approved once.");
        if (clients.Mode == McpClientMode.Approved && clients.Approved.Count == 0)
            errors.Add("Approve at least one client, or accept any client pre-authorized in Entra.");
    }

    private static void NormalizeEndpoints(McpEndpointSettings endpoints, IReadOnlyCollection<string> catalog, List<string> errors)
    {
        endpoints.Hidden = Canonical(endpoints.Hidden, catalog, errors);
        endpoints.ChangeOn = Canonical(endpoints.ChangeOn, catalog, errors);

        if (endpoints.Hidden.Count > MaxEndpoints || endpoints.ChangeOn.Count > MaxEndpoints)
            errors.Add($"At most {MaxEndpoints} endpoints per list.");
        if (endpoints.Visibility == McpEndpointVisibility.AllExcept && endpoints.Hidden.Count == 0)
            errors.Add("Pick at least one endpoint to hide, or show all endpoints.");
        if (endpoints.Changes == McpChangeScope.Listed)
        {
            if (endpoints.ChangeOn.Count == 0)
                errors.Add("Pick at least one endpoint where agents may change messages.");
            if (endpoints.Visibility == McpEndpointVisibility.AllExcept)
            {
                foreach (var hidden in endpoints.ChangeOn.Intersect(endpoints.Hidden, StringComparer.OrdinalIgnoreCase))
                    errors.Add($"'{hidden}' is hidden, so agents cannot change messages on it.");
            }
        }
    }

    private static void CheckLimits(McpLimitSettings limits, RateLimitOptions rateLimits, List<string> errors)
    {
        if (!rateLimits.Enabled)
        {
            if (limits.RequestsPerWindow is not null || limits.MutationsPerWindow is not null)
                errors.Add("Rate limiting is off in this deployment, so the limits cannot be set.");
            return;
        }

        if (limits.RequestsPerWindow is { } requests && (requests < 1 || requests > rateLimits.Mcp.PermitLimit))
            errors.Add($"Tool calls per window must be between 1 and {rateLimits.Mcp.PermitLimit}.");
        if (limits.MutationsPerWindow is { } mutations && (mutations < 1 || mutations > rateLimits.McpMutations.PermitLimit))
            errors.Add($"Message changes per window must be between 1 and {rateLimits.McpMutations.PermitLimit}.");
    }

    private static List<string> Canonical(List<string>? ids, IReadOnlyCollection<string> catalog, List<string> errors)
    {
        var result = new List<string>();
        foreach (var raw in ids ?? [])
        {
            var id = (raw ?? string.Empty).Trim();
            var known = catalog.FirstOrDefault(c => string.Equals(c, id, StringComparison.OrdinalIgnoreCase));
            if (known is null)
                errors.Add($"'{Clip(id)}' is not an endpoint in the catalog.");
            else if (!result.Contains(known, StringComparer.Ordinal))
                result.Add(known);
        }

        return result;
    }

    private static void ListChanges(HashSet<string> before, HashSet<string> after, Action<string> added, Action<string> removed)
    {
        foreach (var name in after.Where(n => !before.Contains(n)).Order(StringComparer.OrdinalIgnoreCase))
            added(name);
        foreach (var name in before.Where(n => !after.Contains(n)).Order(StringComparer.OrdinalIgnoreCase))
            removed(name);
    }

    private static HashSet<string> Names(IEnumerable<string> names) => new(names, StringComparer.OrdinalIgnoreCase);

    private static void LimitChange(string name, int? before, int? after, Action<string, bool> add)
    {
        if (before == after)
            return;

        // Null is the deployment's value, the highest a limit can be.
        var widens = after is null || (before is not null && after > before);
        add($"{name}: {before?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "deployment value"} → {after?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "deployment value"}", widens);
    }

    private static bool IsEmail(string value) => value.Contains('@', StringComparison.Ordinal) && MailAddress.TryCreate(value, out _);

    private static string Clip(string value) => value.Length <= 64 ? value : value[..64] + "…";
}
