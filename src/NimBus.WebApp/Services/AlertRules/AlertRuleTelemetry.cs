using System.Diagnostics.Metrics;

namespace NimBus.WebApp.Services.AlertRules;

/// <summary>The alert rule evaluator's meters (Spec 053).</summary>
public static class AlertRuleTelemetry
{
    /// <summary>Meter name.</summary>
    public const string Name = "NimBus.AlertRules";

    /// <summary>Meter.</summary>
    public static readonly Meter Meter = new(Name);

    /// <summary>Rules evaluated, one per rule per tick.</summary>
    public static readonly Counter<long> Evaluated = Meter.CreateCounter<long>("nimbus.alert_rules.evaluated");

    /// <summary>Transitions, tagged with the status the rule moved to.</summary>
    public static readonly Counter<long> Transitions = Meter.CreateCounter<long>("nimbus.alert_rules.transitions");

    /// <summary>Rules not evaluated in a tick, by reason (kind_unavailable, classification_unavailable, classification_scan_limit).</summary>
    public static readonly Counter<long> Skipped = Meter.CreateCounter<long>("nimbus.alert_rules.skipped");

    /// <summary>Notice deliveries that failed, tagged with the channel.</summary>
    public static readonly Counter<long> NoticeFailures = Meter.CreateCounter<long>("nimbus.alert_rules.notice_failures");

    /// <summary>Notices dropped after 24 hours without a successful delivery.</summary>
    public static readonly Counter<long> NoticesDropped = Meter.CreateCounter<long>("nimbus.alert_rules.notices_dropped");
}
