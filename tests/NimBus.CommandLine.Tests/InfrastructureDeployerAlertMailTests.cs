#pragma warning disable CA1707, CA1861, CA2007

using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>How the alert mail options reach <c>deploy.core.bicep</c>.</summary>
public sealed class InfrastructureDeployerAlertMailTests
{
    private static async Task<RecordedDeployment> DeployCoreAsync(AlertMailDeployment? alertMail)
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
            AlertMail: alertMail);

        await deployer.ApplyAsync(options, CancellationToken.None);

        return azureCli.Deployments[0];
    }

    [Fact]
    public async Task Without_alert_mail_no_alert_mail_parameter_is_passed()
    {
        var core = await DeployCoreAsync(alertMail: null);

        Assert.DoesNotContain(core.Arguments, argument => argument.StartsWith("alertMail", StringComparison.Ordinal));
        Assert.False(core.SecureParameters.ContainsKey("alertMailSmtpPassword"));
    }

    [Fact]
    public async Task Host_and_sender_alone_leave_the_rest_to_the_template_defaults()
    {
        var core = await DeployCoreAsync(new AlertMailDeployment("smtp.azurecomm.net", "alerts@contoso.example"));

        Assert.Contains("alertMailSmtpHost=smtp.azurecomm.net", core.Arguments);
        Assert.Contains("alertMailFrom=alerts@contoso.example", core.Arguments);
        Assert.Equal(2, core.Arguments.Count(argument => argument.StartsWith("alertMail", StringComparison.Ordinal)));
        Assert.False(core.SecureParameters.ContainsKey("alertMailSmtpPassword"));
    }

    [Fact]
    public async Task Every_alert_mail_option_is_passed_and_the_password_only_securely()
    {
        const string password = "smtp-password-marker";
        var core = await DeployCoreAsync(new AlertMailDeployment(
            "smtp.example.com", "alerts@contoso.example", 2525, false, "nimbus", password, "https://ops.contoso.example"));

        Assert.Contains("alertMailSmtpHost=smtp.example.com", core.Arguments);
        Assert.Contains("alertMailFrom=alerts@contoso.example", core.Arguments);
        Assert.Contains("alertMailSmtpPort=2525", core.Arguments);
        Assert.Contains("alertMailSmtpUseSsl=false", core.Arguments);
        Assert.Contains("alertMailSmtpUsername=nimbus", core.Arguments);
        Assert.Contains("alertMailWebAppUrl=https://ops.contoso.example", core.Arguments);
        Assert.DoesNotContain(core.Arguments, argument => argument.Contains(password, StringComparison.Ordinal));
        Assert.Equal(password, core.SecureParameters["alertMailSmtpPassword"]);
    }

    [Fact]
    public async Task Every_alert_mail_parameter_the_CLI_passes_is_declared_by_the_core_template()
    {
        var core = await DeployCoreAsync(new AlertMailDeployment(
            "smtp.example.com", "alerts@contoso.example", 2525, false, "nimbus", "secret", "https://ops.contoso.example"));
        var template = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "deploy", "bicep", "deploy.core.bicep"));

        var names = core.Arguments
            .Where(argument => argument.StartsWith("alertMail", StringComparison.Ordinal))
            .Select(argument => argument[..argument.IndexOf('=', StringComparison.Ordinal)])
            .Concat(core.SecureParameters.Keys.Where(name => name.StartsWith("alertMail", StringComparison.Ordinal)))
            .ToList();

        Assert.Equal(7, names.Count);
        Assert.All(names, name => Assert.Contains($"param {name} ", template, StringComparison.Ordinal));
    }

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
