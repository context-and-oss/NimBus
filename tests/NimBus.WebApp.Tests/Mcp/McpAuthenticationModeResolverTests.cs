#pragma warning disable CA1707, CA2007
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Mcp;

namespace NimBus.WebApp.Tests.Mcp;

[TestClass]
public class McpAuthenticationModeResolverTests
{
    [TestMethod]
    public void Disabled_when_not_enabled_even_with_every_other_setting_present()
    {
        var options = EntraOptions();
        options.Enabled = false;

        var mode = McpAuthenticationModeResolver.Resolve(options, isDevelopment: true, localDevAuthenticationEnabled: true);

        Assert.AreEqual(McpAuthenticationMode.Disabled, mode);
    }

    [TestMethod]
    public void Local_development_when_the_local_dev_bypass_is_active()
    {
        var options = new McpOperatorOptions { Enabled = true };

        var mode = McpAuthenticationModeResolver.Resolve(options, isDevelopment: true, localDevAuthenticationEnabled: true);

        Assert.AreEqual(McpAuthenticationMode.LocalDevelopment, mode);
    }

    [TestMethod]
    public void Local_dev_bypass_wins_over_entra_configuration()
    {
        var mode = McpAuthenticationModeResolver.Resolve(EntraOptions(), isDevelopment: true, localDevAuthenticationEnabled: true);

        Assert.AreEqual(McpAuthenticationMode.LocalDevelopment, mode);
    }

    [TestMethod]
    public void Entra_when_the_mcp_app_registration_is_configured()
    {
        var mode = McpAuthenticationModeResolver.Resolve(EntraOptions(), isDevelopment: false, localDevAuthenticationEnabled: false);

        Assert.AreEqual(McpAuthenticationMode.Entra, mode);
    }

    [TestMethod]
    public void Local_dev_flag_outside_development_does_not_select_local_mode()
    {
        var mode = McpAuthenticationModeResolver.Resolve(EntraOptions(), isDevelopment: false, localDevAuthenticationEnabled: true);

        Assert.AreEqual(McpAuthenticationMode.Entra, mode);
    }

    [TestMethod]
    [DataRow(null, "11111111-1111-1111-1111-111111111111")]
    [DataRow("22222222-2222-2222-2222-222222222222", null)]
    [DataRow(null, null)]
    public void Enabled_without_local_dev_or_entra_fails_naming_the_missing_keys(string? tenantId, string? clientId)
    {
        var options = new McpOperatorOptions { Enabled = true };
        options.Entra.TenantId = tenantId;
        options.Entra.ClientId = clientId;

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            McpAuthenticationModeResolver.Resolve(options, isDevelopment: false, localDevAuthenticationEnabled: false));

        StringAssert.Contains(ex.Message, "NimBus:Mcp:Entra:TenantId", StringComparison.Ordinal);
        StringAssert.Contains(ex.Message, "NimBus:Mcp:Entra:ClientId", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Enable_for_local_development_turns_mcp_on_when_the_bypass_is_active()
    {
        var options = new McpOperatorOptions { EnableForLocalDevelopment = true };

        var mode = McpAuthenticationModeResolver.Resolve(options, isDevelopment: true, localDevAuthenticationEnabled: true);

        Assert.AreEqual(McpAuthenticationMode.LocalDevelopment, mode);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void Enable_for_local_development_leaves_mcp_off_without_the_bypass(bool isDevelopment, bool localDevAuthenticationEnabled)
    {
        var options = new McpOperatorOptions { EnableForLocalDevelopment = true };

        var mode = McpAuthenticationModeResolver.Resolve(options, isDevelopment, localDevAuthenticationEnabled);

        Assert.AreEqual(McpAuthenticationMode.Disabled, mode);
    }

    [TestMethod]
    public void Enable_for_local_development_does_not_enable_entra()
    {
        var options = EntraOptions();
        options.Enabled = false;
        options.EnableForLocalDevelopment = true;

        var mode = McpAuthenticationModeResolver.Resolve(options, isDevelopment: false, localDevAuthenticationEnabled: false);

        Assert.AreEqual(McpAuthenticationMode.Disabled, mode);
    }

    private static McpOperatorOptions EntraOptions()
    {
        var options = new McpOperatorOptions { Enabled = true };
        options.Entra.TenantId = "22222222-2222-2222-2222-222222222222";
        options.Entra.ClientId = "11111111-1111-1111-1111-111111111111";
        return options;
    }
}
