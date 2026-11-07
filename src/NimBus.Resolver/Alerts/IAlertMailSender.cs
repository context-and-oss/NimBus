using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Resolver.Alerts;

/// <summary>One composed endpoint alert mail.</summary>
/// <param name="To">The recipient address.</param>
/// <param name="Subject">The subject line, free of line breaks.</param>
/// <param name="HtmlBody">The HTML body, with every value encoded.</param>
/// <param name="TextBody">The plain-text body.</param>
public sealed record AlertMail(string To, string Subject, string HtmlBody, string TextBody);

/// <summary>Delivers endpoint alert mail.</summary>
public interface IAlertMailSender
{
    /// <summary>Sends <paramref name="mail"/>; throws when delivery fails.</summary>
    /// <param name="mail">The mail to send.</param>
    /// <param name="cancellationToken">Cancels the delivery.</param>
    Task SendAsync(AlertMail mail, CancellationToken cancellationToken = default);
}
