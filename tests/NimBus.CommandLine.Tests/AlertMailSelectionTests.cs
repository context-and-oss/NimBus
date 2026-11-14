#pragma warning disable CA1707, CA1861, CA2007

using McMaster.Extensions.CommandLineUtils;
using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>The <c>--alert-mail-*</c> options shared by <c>nb infra apply</c> and <c>nb setup</c>.</summary>
public sealed class AlertMailSelectionTests
{
    private static AlertMailDeployment? Parse(
        string? smtpHost = null,
        string? from = null,
        string? smtpPort = null,
        string? smtpUseSsl = null,
        string? smtpUsername = null,
        string? webAppUrl = null,
        string? smtpPassword = null)
        => AlertMailSelection.Parse(smtpHost, from, smtpPort, smtpUseSsl, smtpUsername, webAppUrl, smtpPassword);

    [Fact]
    public void No_options_leave_alert_mail_off()
        => Assert.Null(Parse());

    [Fact]
    public void A_password_alone_does_not_turn_alert_mail_on()
        => Assert.Null(Parse(smtpPassword: "secret"));

    [Fact]
    public void Host_and_sender_turn_alert_mail_on_with_template_defaults()
    {
        var mail = Parse(smtpHost: " smtp.azurecomm.net ", from: "alerts@contoso.example");

        Assert.Equal(new AlertMailDeployment("smtp.azurecomm.net", "alerts@contoso.example"), mail);
    }

    [Fact]
    public void Every_option_is_carried()
    {
        var mail = Parse("smtp.example.com", "alerts@contoso.example", "2525", "false", "nimbus", "https://ops.contoso.example", "secret");

        Assert.Equal(
            new AlertMailDeployment("smtp.example.com", "alerts@contoso.example", 2525, false, "nimbus", "secret", "https://ops.contoso.example"),
            mail);
    }

    [Theory]
    [InlineData("smtp.example.com", null)]
    [InlineData(null, "alerts@contoso.example")]
    public void Host_and_sender_are_required_together(string? smtpHost, string? from)
    {
        var error = Assert.Throws<CommandException>(() => Parse(smtpHost: smtpHost, from: from));

        Assert.Contains("--alert-mail-smtp-host", error.Message, StringComparison.Ordinal);
        Assert.Contains("--alert-mail-from", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2525", null, null, null)]
    [InlineData(null, "true", null, null)]
    [InlineData(null, null, "nimbus", null)]
    [InlineData(null, null, null, "https://ops.contoso.example")]
    public void Other_alert_mail_options_need_a_host_and_sender(string? smtpPort, string? smtpUseSsl, string? smtpUsername, string? webAppUrl)
        => Assert.Throws<CommandException>(() => Parse(smtpPort: smtpPort, smtpUseSsl: smtpUseSsl, smtpUsername: smtpUsername, webAppUrl: webAppUrl));

    [Theory]
    [InlineData("not an address")]
    [InlineData("alerts@")]
    public void An_invalid_sender_is_rejected(string from)
    {
        var error = Assert.Throws<CommandException>(() => Parse(smtpHost: "smtp.example.com", from: from));

        Assert.Contains("--alert-mail-from", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("smtp")]
    public void An_invalid_port_is_rejected(string port)
        => Assert.Throws<CommandException>(() => Parse(smtpHost: "smtp.example.com", from: "alerts@contoso.example", smtpPort: port));

    [Theory]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    public void Use_ssl_accepts_true_or_false(string value, bool expected)
        => Assert.Equal(expected, Parse(smtpHost: "smtp.example.com", from: "alerts@contoso.example", smtpUseSsl: value)!.SmtpUseSsl);

    [Fact]
    public void Use_ssl_rejects_anything_else()
        => Assert.Throws<CommandException>(() => Parse(smtpHost: "smtp.example.com", from: "alerts@contoso.example", smtpUseSsl: "yes"));

    [Theory]
    [InlineData("/relative")]
    [InlineData("ftp://ops.contoso.example")]
    public void A_WebApp_url_must_be_absolute_http(string url)
        => Assert.Throws<CommandException>(() => Parse(smtpHost: "smtp.example.com", from: "alerts@contoso.example", webAppUrl: url));
}
