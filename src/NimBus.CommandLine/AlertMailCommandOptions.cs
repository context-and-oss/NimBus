using McMaster.Extensions.CommandLineUtils;

namespace NimBus.CommandLine;

/// <summary>
/// The endpoint alert mail options shared by <c>nb infra apply</c> and <c>nb setup</c>,
/// registered once so both commands describe and parse them the same way.
/// </summary>
internal sealed class AlertMailCommandOptions
{
    private readonly CommandOption _smtpHost;
    private readonly CommandOption _from;
    private readonly CommandOption _smtpPort;
    private readonly CommandOption _smtpUseSsl;
    private readonly CommandOption _smtpUsername;
    private readonly CommandOption _webAppUrl;

    private AlertMailCommandOptions(CommandLineApplication command)
    {
        _smtpHost = command.Option($"{AlertMailSelection.SmtpHostOption} <HOST>",
            "SMTP server the Resolver mails endpoint alert subscribers (the WebApp's Alerts tab) through, e.g. smtp.azurecomm.net. " +
            $"Alert mail is on only with this and {AlertMailSelection.FromOption}, and only for this deployment: the Resolver's settings are replaced on every run, so pass the alert mail options every time.",
            CommandOptionType.SingleValue);
        _from = command.Option($"{AlertMailSelection.FromOption} <ADDRESS>",
            "Sender address of endpoint alert mail.",
            CommandOptionType.SingleValue);
        _smtpPort = command.Option($"{AlertMailSelection.SmtpPortOption} <N>",
            "SMTP port (1-65535). Defaults to 587.",
            CommandOptionType.SingleValue);
        _smtpUseSsl = command.Option($"{AlertMailSelection.SmtpUseSslOption} <BOOL>",
            "Whether the SMTP connection uses TLS: true | false. Defaults to true.",
            CommandOptionType.SingleValue);
        _smtpUsername = command.Option($"{AlertMailSelection.SmtpUsernameOption} <NAME>",
            $"SMTP user name. The password is read from {DeploymentSecrets.AlertMailSmtpPasswordEnvironmentVariable}.",
            CommandOptionType.SingleValue);
        _webAppUrl = command.Option($"{AlertMailSelection.WebAppUrlOption} <URL>",
            "Public URL of the management WebApp, linked from each alert mail. Defaults to https://webapp-{solution-id}-{environment}-management.azurewebsites.net.",
            CommandOptionType.SingleValue);
    }

    public static AlertMailCommandOptions Register(CommandLineApplication command) => new(command);

    /// <summary>Returns the alert mail settings, or null when alert mail is off.</summary>
    public AlertMailDeployment? Build(DeploymentSecrets secrets) => AlertMailSelection.Parse(
        _smtpHost.Value(),
        _from.Value(),
        _smtpPort.Value(),
        _smtpUseSsl.Value(),
        _smtpUsername.Value(),
        _webAppUrl.Value(),
        secrets.AlertMailSmtpPassword);
}
