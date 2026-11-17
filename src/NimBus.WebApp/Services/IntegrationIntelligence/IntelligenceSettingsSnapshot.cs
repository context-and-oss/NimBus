using Microsoft.Extensions.Configuration;

namespace NimBus.WebApp.Services.IntegrationIntelligence;

/// <summary>Non-secret settings captured at registration, never mutated by Admin saves or configuration reloads.</summary>
public sealed record IntelligenceSettingsSnapshot(IntelligenceAdminSettings Active, string Revision, bool LoadFailed)
{
    public static IntelligenceSettingsSnapshot Create(IConfiguration configuration)
    {
        IntelligenceAdminSettings active;
        var failed = string.Equals(configuration[IntelligenceSettingsBootstrap.FailureKey], "true", StringComparison.OrdinalIgnoreCase);
        try { active = IntelligenceAdminSettings.FromConfiguration(configuration); }
        catch (Exception) { active = new(); failed = true; }
        return new(active, configuration[IntelligenceSettingsBootstrap.RevisionKey] ?? "none", failed);
    }
}
