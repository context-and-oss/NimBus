using System;
using System.Collections.Generic;
using System.Globalization;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// How long the message store keeps each kind of data, in whole days (Spec 050). A period of
/// <see cref="Unlimited"/> keeps the data forever.
/// </summary>
/// <param name="OpenEventsDays">Tracking rows in Pending, Failed, Deferred, DeadLettered or Unsupported.</param>
/// <param name="ResolvedEventsDays">Completed and Skipped rows, archived failures, and removed or purged rows.</param>
/// <param name="MessageHistoryDays">Every stored message: the Flow tab and message details.</param>
/// <param name="AuditTrailDays">Audit entries.</param>
public sealed record RetentionPeriods(
    int OpenEventsDays,
    int ResolvedEventsDays,
    int MessageHistoryDays,
    int AuditTrailDays)
{
    /// <summary>The value of a period that never expires.</summary>
    public const int Unlimited = -1;

    /// <summary>The longest bounded period.</summary>
    public const int MaxDays = 3650;

    /// <summary>The shortest bounded audit trail.</summary>
    public const int MinAuditTrailDays = 30;

    /// <summary>Every period unlimited: nothing expires.</summary>
    public static RetentionPeriods AllUnlimited { get; } = new(Unlimited, Unlimited, Unlimited, Unlimited);

    /// <summary>True when no period is bounded.</summary>
    public bool IsAllUnlimited =>
        OpenEventsDays == Unlimited && ResolvedEventsDays == Unlimited
        && MessageHistoryDays == Unlimited && AuditTrailDays == Unlimited;

    /// <summary>
    /// The reasons these periods are invalid; empty when they are valid. A period is
    /// <see cref="Unlimited"/> or 1 to <see cref="MaxDays"/> days, the audit trail at least
    /// <see cref="MinAuditTrailDays"/>, and a bounded audit trail at least as long as every other
    /// bounded period, so the audit trail outlives the data it describes.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        CheckRange(errors, nameof(OpenEventsDays), OpenEventsDays, 1);
        CheckRange(errors, nameof(ResolvedEventsDays), ResolvedEventsDays, 1);
        CheckRange(errors, nameof(MessageHistoryDays), MessageHistoryDays, 1);
        CheckRange(errors, nameof(AuditTrailDays), AuditTrailDays, MinAuditTrailDays);

        if (AuditTrailDays != Unlimited)
        {
            foreach (var (name, days) in new[]
            {
                (nameof(OpenEventsDays), OpenEventsDays),
                (nameof(ResolvedEventsDays), ResolvedEventsDays),
                (nameof(MessageHistoryDays), MessageHistoryDays),
            })
            {
                if (days != Unlimited && days > AuditTrailDays)
                {
                    errors.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{nameof(AuditTrailDays)} ({AuditTrailDays}) must be at least {name} ({days}), so the audit trail outlives the data it describes."));
                }
            }
        }

        return errors;
    }

    private static void CheckRange(List<string> errors, string name, int days, int min)
    {
        if (days != Unlimited && (days < min || days > MaxDays))
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture,
                $"{name} must be {Unlimited} (unlimited) or between {min} and {MaxDays} days; was {days}."));
        }
    }
}

/// <summary>The rows <see cref="IMessageTrackingStore.PurgeExpiredData"/> deleted, per period.</summary>
/// <param name="OpenEvents">Open tracking rows deleted.</param>
/// <param name="ResolvedEvents">Resolved tracking rows deleted.</param>
/// <param name="MessageHistory">Message-history entries deleted.</param>
/// <param name="AuditTrail">Audit entries deleted.</param>
public sealed record RetentionPurgeResult(int OpenEvents, int ResolvedEvents, int MessageHistory, int AuditTrail)
{
    /// <summary>Nothing deleted.</summary>
    public static RetentionPurgeResult None { get; } = new(0, 0, 0, 0);

    /// <summary>The sum of two results.</summary>
    public RetentionPurgeResult Add(RetentionPurgeResult other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new RetentionPurgeResult(
            OpenEvents + other.OpenEvents,
            ResolvedEvents + other.ResolvedEvents,
            MessageHistory + other.MessageHistory,
            AuditTrail + other.AuditTrail);
    }
}

/// <summary>
/// The effective retention periods for a deployment (Spec 050): a period pinned in host
/// configuration (<c>NimBus:Retention:OpenEventsDays</c>, <c>ResolvedEventsDays</c>,
/// <c>MessageHistoryDays</c>, <c>AuditTrailDays</c>) wins over the provider's default.
/// Periods saved from the WebApp's Settings → Retention page are a later phase of Spec 050.
/// </summary>
public sealed class RetentionPolicy
{
    /// <summary>The configuration section the pins are read from.</summary>
    public const string SectionName = "NimBus:Retention";

    /// <summary>
    /// Creates the policy from the provider's <paramref name="defaults"/> and the pins read through
    /// <paramref name="readPin"/>, which returns the configured value of a key under
    /// <see cref="SectionName"/> (for example <c>OpenEventsDays</c>), or null when it is not set.
    /// A pin is a whole number of days, <c>-1</c> or <c>unlimited</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">A pin is not a number, or the effective periods are invalid.</exception>
    public RetentionPolicy(RetentionPeriods defaults, Func<string, string?> readPin)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(readPin);

        Current = new RetentionPeriods(
            Pin(readPin, nameof(RetentionPeriods.OpenEventsDays)) ?? defaults.OpenEventsDays,
            Pin(readPin, nameof(RetentionPeriods.ResolvedEventsDays)) ?? defaults.ResolvedEventsDays,
            Pin(readPin, nameof(RetentionPeriods.MessageHistoryDays)) ?? defaults.MessageHistoryDays,
            Pin(readPin, nameof(RetentionPeriods.AuditTrailDays)) ?? defaults.AuditTrailDays);

        var errors = Current.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Invalid retention configuration in '{SectionName}': {string.Join(" ", errors)}");
        }
    }

    /// <summary>The effective periods.</summary>
    public RetentionPeriods Current { get; }

    private static int? Pin(Func<string, string?> readPin, string key)
    {
        var raw = readPin(key);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = raw.Trim();
        if (string.Equals(value, "unlimited", StringComparison.OrdinalIgnoreCase))
            return RetentionPeriods.Unlimited;

        if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var days))
            return days;

        throw new InvalidOperationException(
            $"Invalid retention configuration: '{SectionName}:{key}' must be a number of days, -1 or 'unlimited'; was '{raw}'.");
    }
}
