namespace Crm.Api;

/// <summary>
/// Demo toggle: whether partner-portal publishes its recurring <c>PartnerLeadSubmitted</c>
/// leads. crm-web flips it and partner-portal asks for it before each lead. Kept in memory,
/// so a crm-api restart returns it to the <c>PARTNER_PUBLISH_LEADS</c> setting.
/// </summary>
public sealed class PartnerLeadsState(bool enabledAtStart)
{
    private readonly object _gate = new();
    private bool _enabled = enabledAtStart;
    private DateTimeOffset _changedAt = DateTimeOffset.UtcNow;

    /// <summary>Leads start on unless <c>PARTNER_PUBLISH_LEADS</c> is <c>false</c>.</summary>
    public static bool EnabledAtStart(IConfiguration configuration) =>
        !bool.TryParse(configuration["PARTNER_PUBLISH_LEADS"], out var enabled) || enabled;

    public (bool Enabled, DateTimeOffset ChangedAt) Snapshot()
    {
        lock (_gate)
        {
            return (_enabled, _changedAt);
        }
    }

    public (bool Enabled, DateTimeOffset ChangedAt) Set(bool enabled)
    {
        lock (_gate)
        {
            if (_enabled != enabled)
            {
                _enabled = enabled;
                _changedAt = DateTimeOffset.UtcNow;
            }

            return (_enabled, _changedAt);
        }
    }
}
