using System.Globalization;
using System.Net.Mail;
using McMaster.Extensions.CommandLineUtils;

namespace NimBus.CommandLine;

/// <summary>
/// Endpoint alert mail settings for <c>deploy.core.bicep</c>. Null optional values leave the
/// template defaults in place.
/// </summary>
internal sealed record AlertMailDeployment(
    string SmtpHost,
    string From,
    int? SmtpPort = null,
    bool? SmtpUseSsl = null,
    string? SmtpUsername = null,
    string? SmtpPassword = null,
    string? WebAppUrl = null);

/// <summary>
/// Parses the <c>--alert-mail-*</c> options. Alert mail is on only with both a host and a sender;
/// any other alert mail option without them is a mistake, so it fails before deploying anything.
/// The password comes from <see cref="DeploymentSecrets.AlertMailSmtpPasswordEnvironmentVariable"/>
/// and is ignored while alert mail is off.
/// </summary>
internal static class AlertMailSelection
{
    internal const string SmtpHostOption = "--alert-mail-smtp-host";
    internal const string FromOption = "--alert-mail-from";
    internal const string SmtpPortOption = "--alert-mail-smtp-port";
    internal const string SmtpUseSslOption = "--alert-mail-smtp-use-ssl";
    internal const string SmtpUsernameOption = "--alert-mail-smtp-username";
    internal const string WebAppUrlOption = "--alert-mail-webapp-url";

    public static AlertMailDeployment? Parse(
        string? smtpHost,
        string? from,
        string? smtpPort,
        string? smtpUseSsl,
        string? smtpUsername,
        string? webAppUrl,
        string? smtpPassword)
    {
        var host = Trimmed(smtpHost);
        var sender = Trimmed(from);
        var port = Trimmed(smtpPort);
        var useSsl = Trimmed(smtpUseSsl);
        var username = Trimmed(smtpUsername);
        var url = Trimmed(webAppUrl);

        if (host is null && sender is null)
        {
            if (port is not null || useSsl is not null || username is not null || url is not null)
            {
                throw new CommandException(
                    $"The alert mail options need {SmtpHostOption} and {FromOption}; without them alert mail is off.");
            }

            return null;
        }

        if (host is null || sender is null)
        {
            throw new CommandException($"Alert mail needs both {SmtpHostOption} and {FromOption}.");
        }

        if (!MailAddress.TryCreate(sender, out var address) || !string.Equals(address.Address, sender, StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandException($"Invalid {FromOption} value '{sender}'. Expected a mail address such as alerts@contoso.com.");
        }

        return new AlertMailDeployment(
            host,
            sender,
            ParsePort(port),
            ParseUseSsl(useSsl),
            username,
            string.IsNullOrWhiteSpace(smtpPassword) ? null : smtpPassword,
            ParseWebAppUrl(url));
    }

    private static int? ParsePort(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535)
        {
            return port;
        }

        throw new CommandException($"Invalid {SmtpPortOption} value '{value}'. Expected an integer from 1 to 65535.");
    }

    private static bool? ParseUseSsl(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (bool.TryParse(value, out var useSsl))
        {
            return useSsl;
        }

        throw new CommandException($"Invalid {SmtpUseSslOption} value '{value}'. Expected true or false.");
    }

    private static string? ParseWebAppUrl(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp))
        {
            return value;
        }

        throw new CommandException($"Invalid {WebAppUrlOption} value '{value}'. Expected an absolute http or https URL.");
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
