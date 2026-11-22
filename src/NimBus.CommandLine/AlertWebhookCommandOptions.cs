using McMaster.Extensions.CommandLineUtils;

namespace NimBus.CommandLine;

/// <summary>
/// The endpoint alert webhook option shared by <c>nb infra apply</c> and <c>nb setup</c>,
/// registered once so both commands describe and parse it the same way.
/// </summary>
internal sealed class AlertWebhookCommandOptions
{
    private readonly CommandOption _tokens;

    private AlertWebhookCommandOptions(CommandLineApplication command)
    {
        _tokens = command.Option($"{AlertWebhookSelection.TokenOption} <URL_PREFIX>=<AUDIENCE>",
            "Alert webhooks to a URL under this https prefix also carry a bearer token of the sending app's identity (the Resolver for subscriptions, the WebApp for alert rules) for this audience, " +
            "e.g. https://my-agent.example.com/api/=https://azuresre.dev. Repeat for several prefixes. " +
            $"Alert webhooks are on only when {DeploymentSecrets.AlertWebhookSigningKeyEnvironmentVariable} holds a signing key (at least {AlertWebhookSelection.MinSigningKeyLength} characters), " +
            "and only for this deployment: pass the key and these options on every run.",
            CommandOptionType.MultipleValue);
    }

    public static AlertWebhookCommandOptions Register(CommandLineApplication command) => new(command);

    /// <summary>Returns the alert webhook settings, or null when alert webhooks are off.</summary>
    public AlertWebhookDeployment? Build(DeploymentSecrets secrets) =>
        AlertWebhookSelection.Parse(_tokens.Values, secrets.AlertWebhookSigningKey);
}
