#pragma warning disable CA1707, CA1861, CA2007

using System.Text.Json;
using McMaster.Extensions.CommandLineUtils;
using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// The endpoint alert webhook settings: the signing key from the environment, the
/// <c>--alert-webhook-token</c> option, and how both reach the two Bicep templates.
/// </summary>
public sealed class AlertWebhookDeploymentTests
{
    private const string SigningKey = "0123456789abcdef0123456789abcdef";

    // ── Parsing ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Without_a_key_or_a_token_alert_webhooks_are_off()
        => Assert.Null(AlertWebhookSelection.Parse([], signingKey: null));

    [Fact]
    public void A_key_alone_turns_alert_webhooks_on()
    {
        var webhook = AlertWebhookSelection.Parse([], SigningKey);

        Assert.NotNull(webhook);
        Assert.Equal(SigningKey, webhook.SigningKey);
        Assert.Empty(webhook.BearerTokens);
    }

    [Fact]
    public void A_short_key_is_rejected_without_being_printed()
    {
        var exception = Assert.Throws<CommandException>(() => AlertWebhookSelection.Parse([], "short-key-marker"));

        Assert.Contains(DeploymentSecrets.AlertWebhookSigningKeyEnvironmentVariable, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("short-key-marker", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_without_a_key_is_rejected()
    {
        var exception = Assert.Throws<CommandException>(
            () => AlertWebhookSelection.Parse(["https://agent.example.com/api/=https://azuresre.dev"], signingKey: null));

        Assert.Contains(DeploymentSecrets.AlertWebhookSigningKeyEnvironmentVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tokens_are_split_into_a_prefix_and_an_audience()
    {
        var webhook = AlertWebhookSelection.Parse(
            [" https://agent.example.com/api/triggers=https://azuresre.dev ", "https://other.example.com/=59f0a04a-b322-4310-adc9-39ac41e9631e"],
            SigningKey);

        Assert.Equal(
            [
                new AlertWebhookBearerTokenDeployment("https://agent.example.com/api/triggers", "https://azuresre.dev"),
                new AlertWebhookBearerTokenDeployment("https://other.example.com/", "59f0a04a-b322-4310-adc9-39ac41e9631e"),
            ],
            webhook!.BearerTokens);
    }

    [Theory]
    [InlineData("https://agent.example.com/api/")]
    [InlineData("https://agent.example.com/api/=")]
    [InlineData("=https://azuresre.dev")]
    [InlineData("http://agent.example.com/api/=https://azuresre.dev")]
    [InlineData("agent.example.com=https://azuresre.dev")]
    [InlineData("https://agent.example.com/api/?code=1=https://azuresre.dev")]
    [InlineData("https://user@agent.example.com/api/=https://azuresre.dev")]
    [InlineData("https://agent.example.com/api/=two words")]
    public void A_malformed_token_is_rejected(string token)
    {
        var exception = Assert.Throws<CommandException>(() => AlertWebhookSelection.Parse([token], SigningKey));

        Assert.Contains(AlertWebhookSelection.TokenOption, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_signing_key_is_read_from_and_removed_from_the_environment()
    {
        var environment = new Dictionary<string, string?>
        {
            [DeploymentSecrets.AlertWebhookSigningKeyEnvironmentVariable] = SigningKey,
            ["PATH"] = "expected-path",
        };

        var secrets = DeploymentSecrets.Load(name => environment.GetValueOrDefault(name));
        DeploymentSecrets.RemoveFrom(environment);

        Assert.Equal(SigningKey, secrets.AlertWebhookSigningKey);
        Assert.DoesNotContain(DeploymentSecrets.AlertWebhookSigningKeyEnvironmentVariable, environment.Keys);
        Assert.Equal("expected-path", environment["PATH"]);
    }

    // ── What reaches the templates ─────────────────────────────────────────────────────

    private static async Task<(RecordedDeployment Core, RecordedDeployment WebApp)> DeployAsync(AlertWebhookDeployment? alertWebhook)
    {
        var azureCli = new RecordingAzureCliRunner();
        var deployer = new InfrastructureDeployer(new CommandContext(Path.GetTempPath()), azureCli);
        var options = new InfrastructureOptions(
            "nimbus",
            "dev",
            "rg-nimbus-dev",
            ResourceNamePostFix: null,
            Location: null,
            WebAppVersion: "test",
            AlertWebhook: alertWebhook);

        await deployer.ApplyAsync(options, CancellationToken.None);

        var core = azureCli.Deployments.Single(d => d.Arguments.Any(a => a.EndsWith("deploy.core.bicep", StringComparison.Ordinal)));
        var webApp = azureCli.Deployments.Single(d => d.Arguments.Any(a => a.EndsWith("deploy.webapp.bicep", StringComparison.Ordinal)));
        return (core, webApp);
    }

    [Fact]
    public async Task Without_alert_webhooks_no_alert_webhook_parameter_is_passed()
    {
        var (core, webApp) = await DeployAsync(alertWebhook: null);

        Assert.DoesNotContain(core.Arguments, argument => argument.StartsWith("alertWebhook", StringComparison.Ordinal));
        Assert.DoesNotContain(core.SecureParameters.Keys, name => name.StartsWith("alertWebhook", StringComparison.Ordinal));
        Assert.DoesNotContain(webApp.Arguments, argument => argument.StartsWith("alertWebhook", StringComparison.Ordinal));
        Assert.DoesNotContain(webApp.SecureParameters.Keys, name => name.StartsWith("alertWebhook", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_signing_key_reaches_both_templates_and_only_securely()
    {
        var (core, webApp) = await DeployAsync(new AlertWebhookDeployment(SigningKey, []));

        Assert.Equal(SigningKey, core.SecureParameters["alertWebhookSigningKey"]);
        Assert.Equal(SigningKey, webApp.SecureParameters["alertWebhookSigningKey"]);
        Assert.DoesNotContain(core.Arguments, argument => argument.Contains(SigningKey, StringComparison.Ordinal));
        Assert.DoesNotContain(webApp.Arguments, argument => argument.Contains(SigningKey, StringComparison.Ordinal));
        Assert.DoesNotContain(core.Arguments, argument => argument.StartsWith("alertWebhookBearerTokens", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_token_entries_reach_the_core_template_as_json()
    {
        var (core, webApp) = await DeployAsync(new AlertWebhookDeployment(
            SigningKey,
            [new AlertWebhookBearerTokenDeployment("https://agent.example.com/api/triggers", "https://azuresre.dev")]));

        const string name = "alertWebhookBearerTokens=";
        var argument = core.Arguments.Single(a => a.StartsWith(name, StringComparison.Ordinal));
        using var json = JsonDocument.Parse(argument[name.Length..]);
        var entry = json.RootElement.EnumerateArray().Single();
        Assert.Equal("https://agent.example.com/api/triggers", entry.GetProperty("urlPrefix").GetString());
        Assert.Equal("https://azuresre.dev", entry.GetProperty("audience").GetString());
        Assert.DoesNotContain(webApp.Arguments, a => a.StartsWith(name, StringComparison.Ordinal));
    }

    [Fact]
    public void Both_templates_declare_the_parameters_and_keep_the_key_secure()
    {
        var root = FindRepositoryRoot();
        var core = ReadNormalized(Path.Combine(root, "deploy", "bicep", "deploy.core.bicep"));
        var webApp = ReadNormalized(Path.Combine(root, "deploy", "bicep", "deploy.webapp.bicep"));

        Assert.Contains("@secure()\nparam alertWebhookSigningKey string", core, StringComparison.Ordinal);
        Assert.Contains("param alertWebhookBearerTokens array", core, StringComparison.Ordinal);
        Assert.Contains("@secure()\nparam alertWebhookSigningKey string", webApp, StringComparison.Ordinal);

        // Only the secure settings objects may carry the key: a plain settings array is kept in
        // the nested deployment's history.
        Assert.Contains("NimBus__Alerts__Webhook__SigningKey: alertWebhookSigningKey", core, StringComparison.Ordinal);
        Assert.Contains("NimBus__Alerts__Webhook__SigningKey: alertWebhookSigningKey", webApp, StringComparison.Ordinal);
        Assert.DoesNotContain("value: alertWebhookSigningKey", core, StringComparison.Ordinal);
        Assert.DoesNotContain("value: alertWebhookSigningKey", webApp, StringComparison.Ordinal);
        Assert.Contains("  alertWebhookResolverSecretSettings,\n", core, StringComparison.Ordinal);
    }

    private static string ReadNormalized(string path) => File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "deploy", "bicep", "deploy.core.bicep")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
