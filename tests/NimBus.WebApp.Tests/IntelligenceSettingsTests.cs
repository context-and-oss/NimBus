#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.WebApp.Services.IntegrationIntelligence;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class IntelligenceSettingsTests
{
    [TestMethod]
    public void Saved_Settings_Override_NonSecrets_Without_Changing_Other_Deployment_Settings()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // A key of the provider removed in v5.0.0: saved settings neither copy nor clear it.
            ["NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:ApiKey"] = "secret-must-stay-local",
            ["NimBus:IntegrationIntelligence:FailureClassification:AllowedEndpoints:0"] = "old-endpoint",
        }).Build();
        var settings = new IntelligenceAdminSettings { Enabled = true, IncludeEventPayload = true };
        var effective = settings.ApplyTo(configuration);
        Assert.IsTrue(effective.GetValue<bool>("NimBus:IntegrationIntelligence:FailureClassification:Data:IncludeEventPayload"));
        Assert.AreEqual("secret-must-stay-local", effective["NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:ApiKey"]);
        Assert.IsNull(effective["NimBus:IntegrationIntelligence:FailureClassification:AllowedEndpoints:0"]);
        Assert.AreEqual(0, IntelligenceAdminSettings.FromConfiguration(effective).AllowedEndpoints.Length);
        Assert.IsFalse(JsonConvert.SerializeObject(settings).Contains("secret-must-stay-local", StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(configuration["NimBus:IntegrationIntelligence:Enabled"]));
        configuration["Unrelated:Setting"] = "updated";
        Assert.AreEqual("updated", effective["Unrelated:Setting"], "Non-intelligence configuration must retain its original provider behavior.");
    }

    [TestMethod]
    public void A_Record_Saved_With_The_Removed_Provider_Fields_Still_Loads()
    {
        var legacy = JsonConvert.DeserializeObject<IntelligenceSettingsDocument>("""{"Settings":{"Enabled":true,"Model":"jev-1.13.0","IncludeEventPayload":false,"IncludeRecentFailureHistory":true,"MaximumHistoryItems":5,"AdditionalRedactedKeys":[],"AllowedEndpoints":[],"TimeoutSeconds":20,"MinimumCategoryConfidence":0.6,"RetryLikely":0.75,"ChangeRequired":0.75},"Revision":"9f1c2c3e-0000-4000-8000-000000000000","ProtectedApiKey":"CfDJ8-sealed"}""")!;

        Assert.IsTrue(legacy.Settings.Enabled);
        Assert.AreEqual(0, legacy.Settings.Validate().Count);
        Assert.IsFalse(JsonConvert.SerializeObject(legacy).Contains("CfDJ8-sealed", StringComparison.Ordinal), "The next save drops the sealed key.");
    }

    [TestMethod]
    public void Settings_Validate_Bounds_And_Default_To_No_Payload_Sharing()
    {
        var settings = new IntelligenceAdminSettings();
        Assert.IsFalse(settings.IncludeEventPayload);
        Assert.AreEqual(0, settings.Validate().Count);
        settings.MinimumCategoryConfidence = double.NaN;
        settings.MaximumHistoryItems = -1;
        settings.RetryLikely = 2;
        Assert.IsTrue(settings.Validate().Count >= 3);
    }
}
