#pragma warning disable CA1707, CA2007
using NimBus.Adapter.Mcp.Tests.Infrastructure;

namespace NimBus.Adapter.Mcp.Tests;

[TestClass]
public sealed class AdapterTokenValidatorTests
{
    private static Task<AdapterTokenValidationResult> ValidateAsync(string? authorization, AdapterTokenValidator? validator = null)
        => (validator ?? TestTokens.Validator()).ValidateAsync(authorization, CancellationToken.None);

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("Bearer")]
    [DataRow("Bearer ")]
    [DataRow("Basic dXNlcjpwYXNz")]
    public async Task No_bearer_token_is_missing(string? authorization)
    {
        var result = await ValidateAsync(authorization);

        Assert.AreEqual(AdapterTokenStatus.Missing, result.Status);
        Assert.IsNull(result.Principal);
    }

    [TestMethod]
    public async Task A_token_for_the_adapter_client_id_with_the_role_is_valid()
    {
        var result = await ValidateAsync("Bearer " + TestTokens.Create());

        Assert.AreEqual(AdapterTokenStatus.Valid, result.Status);
        Assert.IsNotNull(result.Principal);
        Assert.AreEqual(TestTokens.CallerObjectId, result.ObjectId);
        Assert.AreEqual(TestTokens.CallerAppId, result.ApplicationId);
    }

    [TestMethod]
    public async Task A_token_for_the_adapter_app_id_uri_is_valid()
    {
        var result = await ValidateAsync("Bearer " + TestTokens.Create(audience: TestTokens.AdapterAppIdUri));

        Assert.AreEqual(AdapterTokenStatus.Valid, result.Status);
    }

    [TestMethod]
    public async Task The_app_id_uri_defaults_to_api_scheme_and_client_id()
    {
        var options = TestTokens.Options();
        options.ApplicationIdUri = null;
        var validator = new AdapterTokenValidator(options, TestTokens.Metadata());

        var result = await ValidateAsync("Bearer " + TestTokens.Create(audience: $"api://{TestTokens.AdapterClientId}"), validator);

        Assert.AreEqual(AdapterTokenStatus.Valid, result.Status);
    }

    [TestMethod]
    public async Task A_v1_token_from_the_tenant_is_valid_and_names_its_client_by_appid()
    {
        var result = await ValidateAsync("Bearer " + TestTokens.Create(v1: true));

        Assert.AreEqual(AdapterTokenStatus.Valid, result.Status);
        Assert.AreEqual(TestTokens.CallerAppId, result.ApplicationId);
    }

    [TestMethod]
    public async Task The_scheme_name_is_case_insensitive()
    {
        var result = await ValidateAsync("bearer " + TestTokens.Create());

        Assert.AreEqual(AdapterTokenStatus.Valid, result.Status);
    }

    [TestMethod]
    public async Task A_token_for_NimBus_is_refused()
    {
        var result = await ValidateAsync("Bearer " + TestTokens.Create(audience: TestTokens.NimBusClientId));

        Assert.AreEqual(AdapterTokenStatus.Invalid, result.Status);
        Assert.IsNull(result.Principal);
    }

    [TestMethod]
    [DataRow("api://nimbus-operator")]
    [DataRow("https://graph.microsoft.com")]
    [DataRow("00000000-0000-0000-0000-000000000000")]
    public async Task A_token_for_another_audience_is_refused(string audience)
    {
        var result = await ValidateAsync("Bearer " + TestTokens.Create(audience: audience));

        Assert.AreEqual(AdapterTokenStatus.Invalid, result.Status);
    }

    [TestMethod]
    public async Task A_v2_token_from_another_tenant_is_refused()
    {
        var result = await ValidateAsync("Bearer " + TestTokens.Create(issuer: TestTokens.V2Issuer(TestTokens.OtherTenantId)));

        Assert.AreEqual(AdapterTokenStatus.Invalid, result.Status);
    }

    [TestMethod]
    public async Task A_v1_token_from_another_tenant_is_refused()
    {
        var result = await ValidateAsync("Bearer " + TestTokens.Create(issuer: TestTokens.V1Issuer(TestTokens.OtherTenantId), v1: true));

        Assert.AreEqual(AdapterTokenStatus.Invalid, result.Status);
    }

    [TestMethod]
    public async Task A_token_from_another_issuer_is_refused()
    {
        var result = await ValidateAsync("Bearer " + TestTokens.Create(issuer: "https://idp.example/"));

        Assert.AreEqual(AdapterTokenStatus.Invalid, result.Status);
    }

    [TestMethod]
    public async Task A_token_signed_with_an_unknown_key_is_refused()
    {
        var result = await ValidateAsync("Bearer " + TestTokens.Create(untrustedKey: true));

        Assert.AreEqual(AdapterTokenStatus.Invalid, result.Status);
    }

    [TestMethod]
    public async Task An_expired_token_is_refused()
    {
        var result = await ValidateAsync("Bearer " + TestTokens.Create(expires: DateTime.UtcNow.AddHours(-1)));

        Assert.AreEqual(AdapterTokenStatus.Invalid, result.Status);
    }

    [TestMethod]
    public async Task A_malformed_token_is_refused()
    {
        var result = await ValidateAsync("Bearer not-a-jwt");

        Assert.AreEqual(AdapterTokenStatus.Invalid, result.Status);
    }

    [TestMethod]
    public async Task A_valid_token_without_the_read_role_is_forbidden()
    {
        var result = await ValidateAsync("Bearer " + TestTokens.Create(roles: ["Nimbus.Observe"]));

        Assert.AreEqual(AdapterTokenStatus.Forbidden, result.Status);
        Assert.AreEqual(TestTokens.CallerObjectId, result.ObjectId);
    }

    [TestMethod]
    public async Task A_valid_token_with_no_roles_is_forbidden()
    {
        var result = await ValidateAsync("Bearer " + TestTokens.Create(roles: []));

        Assert.AreEqual(AdapterTokenStatus.Forbidden, result.Status);
    }

    [TestMethod]
    public async Task The_read_role_is_configurable()
    {
        var options = TestTokens.Options();
        options.ReadRole = "Erp.Read";
        var validator = new AdapterTokenValidator(options, TestTokens.Metadata());

        Assert.AreEqual(AdapterTokenStatus.Valid, (await ValidateAsync("Bearer " + TestTokens.Create(roles: ["Erp.Read"]), validator)).Status);
        Assert.AreEqual(AdapterTokenStatus.Forbidden, (await ValidateAsync("Bearer " + TestTokens.Create(), validator)).Status);
    }

    [TestMethod]
    public void The_defaults_match_the_spec()
    {
        var options = new AdapterMcpOptions();

        Assert.AreEqual("AdapterMcp", AdapterMcpOptions.SectionName);
        Assert.AreEqual("Adapter.Read", options.ReadRole);
        Assert.AreEqual("https://login.microsoftonline.com/", options.Instance);
    }

    [TestMethod]
    public void A_validator_without_tenant_or_audience_is_refused_at_construction()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => new AdapterTokenValidator(new AdapterMcpOptions { Audience = TestTokens.AdapterClientId }, TestTokens.Metadata()));
        Assert.ThrowsExactly<InvalidOperationException>(() => new AdapterTokenValidator(new AdapterMcpOptions { TenantId = TestTokens.TenantId }, TestTokens.Metadata()));
    }
}
