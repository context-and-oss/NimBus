using NimBus.MessageStore;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Mcp.Operations;

/// <summary>
/// One option a tool result offers the user (Spec 046 §3): which tool to call next and with which
/// arguments. A hint only: the tool it names authorizes, audits and rate-limits the call itself.
/// </summary>
/// <param name="Id">Stable within the result: investigate, resubmit, skip, report, classify, run, check, open_web_ui or resubmit_ticket.</param>
/// <param name="Label">Fixed text chosen by NimBus, for the user. Never contains data.</param>
/// <param name="Kind">read; change (changes NimBus state or sends a command); or destructive (skip).</param>
/// <param name="Tool">The tool to call; null for open_web_ui, which carries <paramref name="Url"/>.</param>
/// <param name="Arguments">Arguments NimBus already knows: ids, versions and tokens from the same result. Ids are data.</param>
/// <param name="Needs">Arguments you must add: reason, idempotencyKey or ticketId.</param>
/// <param name="Confirm">True when a person present must say yes before the change this option leads to runs.</param>
/// <param name="Url">For open_web_ui, the page in the NimBus Web UI.</param>
public sealed record NextAction(
    string Id, string Label, string Kind, string? Tool, IReadOnlyDictionary<string, object> Arguments,
    IReadOnlyList<string> Needs, bool Confirm, string? Url = null);

/// <summary>
/// Builds the <see cref="NextAction"/> lists of the operator tools (Spec 046 §3). Every input is an
/// id, a version, a token or an enum: there is no parameter through which error text, log text or
/// payload values could reach a label. Options come from checks the caller already passed
/// (<see cref="OperatorActionAccess.EligibleActionsAsync"/>); an option the caller can't run is
/// left out, not disabled.
/// </summary>
public static class OperatorNextActions
{
    /// <summary>Most options in one list.</summary>
    public const int MaxActions = 6;

    /// <summary>Option kinds.</summary>
    public static class Kinds
    {
        /// <summary>Reads only.</summary>
        public const string Read = "read";

        /// <summary>Changes NimBus state or sends a command.</summary>
        public const string Change = "change";

        /// <summary>Skips a message: it is never processed.</summary>
        public const string Destructive = "destructive";
    }

    /// <summary>The fixed labels; a label is always one of these.</summary>
    public static class Labels
    {
        /// <summary>Investigate a message.</summary>
        public const string Investigate = "Look at its history and errors";

        /// <summary>Investigate a group's example.</summary>
        public const string InvestigateExample = "Investigate an example";

        /// <summary>Read a message again after an operation.</summary>
        public const string ReadAgain = "Read the message again";

        /// <summary>Resubmit one message.</summary>
        public const string Resubmit = "Resubmit this message";

        /// <summary>Resubmit a group's example.</summary>
        public const string ResubmitExample = "Resubmit the example";

        /// <summary>Resubmit every failure under a ticket.</summary>
        public const string ResubmitTicket = "Resubmit every failure under its ticket";

        /// <summary>Skip one message.</summary>
        public const string Skip = "Skip this message (it is never processed)";

        /// <summary>Mark reported.</summary>
        public const string Report = "Mark it reported under a ticket";

        /// <summary>Classify the failure.</summary>
        public const string Classify = "Classify the failure";

        /// <summary>Run a prepared resubmit.</summary>
        public const string RunResubmit = "Run the resubmit";

        /// <summary>Run a prepared skip.</summary>
        public const string RunSkip = "Run the skip";

        /// <summary>Check an operation.</summary>
        public const string Check = "Check the outcome";

        /// <summary>Open the Web UI.</summary>
        public const string OpenWebUi = "Open it in the NimBus Web UI";

        /// <summary>Every label, for tests and documentation.</summary>
        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        {
            Investigate, InvestigateExample, ReadAgain, Resubmit, ResubmitExample, ResubmitTicket, Skip, Report, Classify,
            RunResubmit, RunSkip, Check, OpenWebUi,
        };
    }

    private const string RetryMayHelp = "RetryMayHelp";
    private static readonly string[] ChangeNeeds = ["reason", "idempotencyKey"];

    /// <summary>The options for one message (<c>nimbus_get_message</c>).</summary>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="eventId">The message's event id.</param>
    /// <param name="messageVersion">Its version, when it has an unresolved row.</param>
    /// <param name="eligibleActions">From <see cref="OperatorActionAccess.EligibleActionsAsync"/>.</param>
    /// <param name="status">Its resolution status.</param>
    /// <param name="pendingSubStatus">Its pending sub-status, for example Handoff.</param>
    /// <param name="webUiUrl">Its page in the Web UI.</param>
    /// <param name="isWorkload">True for an unattended caller: nothing waits for a yes.</param>
    /// <param name="guidance">The saved classification's guidance, when there is one.</param>
    public static IReadOnlyList<NextAction> ForMessage(string endpointId, string eventId, string? messageVersion,
        IReadOnlyList<string> eligibleActions, string? status, string? pendingSubStatus, string? webUiUrl, bool isWorkload, string? guidance)
    {
        ArgumentNullException.ThrowIfNull(eligibleActions);
        var investigate = Option("investigate", Labels.Investigate, Kinds.Read, "nimbus_get_message_history",
            Args(("endpointId", endpointId), ("eventId", eventId)));

        NextAction? resubmit = null, skip = null;
        if (messageVersion is not null && Has(eligibleActions, OperatorAction.Resubmit))
            resubmit = Prepare("resubmit", Labels.Resubmit, Kinds.Change, endpointId, eventId, messageVersion, isWorkload);
        // A workload never skips (Spec 044 Phase 2); its scope check already says so.
        if (messageVersion is not null && !isWorkload && Has(eligibleActions, OperatorAction.Skip))
            skip = Prepare("skip", Labels.Skip, Kinds.Destructive, endpointId, eventId, messageVersion, isWorkload: false);

        var classify = Has(eligibleActions, OperatorAction.Classify)
            ? Option("classify", Labels.Classify, Kinds.Change, "nimbus_get_failure_evidence", Args(("endpointId", endpointId), ("eventId", eventId)))
            : null;
        var report = Has(eligibleActions, OperatorAction.Report)
            ? Option("report", Labels.Report, Kinds.Change, "nimbus_set_message_reported",
                Args(("endpointId", endpointId), ("eventId", eventId), ("reported", true)), [.. ChangeNeeds, "ticketId"], confirm: !isWorkload)
            : null;
        // Deferred messages and pending handoffs are recovered in the Web UI, not over MCP.
        var webUi = webUiUrl is not null && (Is(status, ResolutionStatus.Deferred)
                || (Is(status, ResolutionStatus.Pending) && string.Equals(pendingSubStatus, "Handoff", StringComparison.OrdinalIgnoreCase)))
            ? WebUi(webUiUrl)
            : null;

        var first = resubmit is not null && string.Equals(guidance, RetryMayHelp, StringComparison.Ordinal) ? resubmit : investigate;
        return Collect(first, investigate, resubmit, classify, report, skip, webUi);
    }

    /// <summary>The options for a group of failures (<c>nimbus_get_worklist</c>), acting on its example.</summary>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="exampleEventId">The example's event id.</param>
    /// <param name="exampleVersion">The example's version.</param>
    /// <param name="eligibleActions">The example's eligible actions.</param>
    /// <param name="ticketId">The ticket every reported open message in the group shares, if any.</param>
    /// <param name="isWorkload">True for an unattended caller.</param>
    /// <param name="guidance">The example's saved guidance, when there is one.</param>
    public static IReadOnlyList<NextAction> ForGroup(string endpointId, string exampleEventId, string? exampleVersion,
        IReadOnlyList<string> eligibleActions, string? ticketId, bool isWorkload, string? guidance)
    {
        ArgumentNullException.ThrowIfNull(eligibleActions);
        var investigate = Option("investigate", Labels.InvestigateExample, Kinds.Read, "nimbus_get_message",
            Args(("endpointId", endpointId), ("eventId", exampleEventId)));

        var mayResubmit = Has(eligibleActions, OperatorAction.Resubmit);
        var resubmit = exampleVersion is not null && mayResubmit
            ? Prepare("resubmit", Labels.ResubmitExample, Kinds.Change, endpointId, exampleEventId, exampleVersion, isWorkload)
            : null;
        var byTicket = ticketId is not null && mayResubmit
            ? Option("resubmit_ticket", Labels.ResubmitTicket, Kinds.Change, "nimbus_prepare_action",
                Args(("action", "resubmit"), ("endpointId", endpointId), ("ticketId", ticketId)), [], confirm: !isWorkload)
            : null;
        var classify = Has(eligibleActions, OperatorAction.Classify)
            ? Option("classify", Labels.Classify, Kinds.Change, "nimbus_get_failure_evidence", Args(("endpointId", endpointId), ("eventId", exampleEventId)))
            : null;
        var report = ticketId is null && Has(eligibleActions, OperatorAction.Report)
            ? Option("report", Labels.Report, Kinds.Change, "nimbus_set_message_reported",
                Args(("endpointId", endpointId), ("eventId", exampleEventId), ("reported", true)), [.. ChangeNeeds, "ticketId"], confirm: !isWorkload)
            : null;

        var first = resubmit is not null && string.Equals(guidance, RetryMayHelp, StringComparison.Ordinal) ? resubmit : investigate;
        return Collect(first, investigate, resubmit, byTicket, classify, report);
    }

    /// <summary>The option after <c>nimbus_prepare_action</c>: run it.</summary>
    /// <param name="action">resubmit or skip.</param>
    /// <param name="actionToken">The token it returned.</param>
    /// <param name="isWorkload">True for an unattended caller.</param>
    public static IReadOnlyList<NextAction> ForPrepared(string action, string actionToken, bool isWorkload)
    {
        var skip = string.Equals(action, "skip", StringComparison.Ordinal);
        return
        [
            Option("run", skip ? Labels.RunSkip : Labels.RunResubmit, skip ? Kinds.Destructive : Kinds.Change,
                skip ? "nimbus_skip_message" : "nimbus_resubmit_message", Args(("actionToken", actionToken)), ChangeNeeds, confirm: !isWorkload),
        ];
    }

    /// <summary>The option after a resubmit or skip: check its operation. Empty without one.</summary>
    /// <param name="operationId">The operation, when the journal recorded one.</param>
    public static IReadOnlyList<NextAction> ForSent(string? operationId)
        => operationId is null ? [] : [Check(operationId)];

    /// <summary>The options for an operation (<c>nimbus_get_operation</c>), by its status.</summary>
    /// <param name="status">Its status.</param>
    /// <param name="parked">True when the endpoint parked the message for external work.</param>
    /// <param name="operationId">The operation.</param>
    /// <param name="endpointId">Its endpoint.</param>
    /// <param name="eventId">Its message's event id.</param>
    /// <param name="webUiUrl">The message's page in the Web UI.</param>
    public static IReadOnlyList<NextAction> ForOperation(OperatorOperationStatus status, bool parked, string operationId,
        string endpointId, string eventId, string? webUiUrl)
    {
        var readAgain = Option("investigate", Labels.ReadAgain, Kinds.Read, "nimbus_get_message", Args(("endpointId", endpointId), ("eventId", eventId)));
        return status switch
        {
            OperatorOperationStatus.Succeeded or OperatorOperationStatus.Refused => [],
            // The message read again offers what may be done now, through the same checks.
            OperatorOperationStatus.Failed or OperatorOperationStatus.NotSent => [readAgain],
            // A person decides; never offer a resubmit (Spec 044 Phase 2).
            OperatorOperationStatus.OutcomeUnknown => webUiUrl is null ? [] : [WebUi(webUiUrl)],
            OperatorOperationStatus.Dispatched when parked => [Check(operationId), readAgain],
            _ => [Check(operationId)],
        };
    }

    private static NextAction Check(string operationId)
        => Option("check", Labels.Check, Kinds.Read, "nimbus_get_operation", Args(("operationId", operationId)));

    private static NextAction WebUi(string url)
        => new("open_web_ui", Labels.OpenWebUi, Kinds.Read, null, Args(), [], false, url);

    private static NextAction Prepare(string action, string label, string kind, string endpointId, string eventId, string messageVersion, bool isWorkload)
        => Option(action, label, kind, "nimbus_prepare_action",
            Args(("action", action), ("endpointId", endpointId), ("eventId", eventId), ("messageVersion", messageVersion)), [], confirm: !isWorkload);

    private static NextAction Option(string id, string label, string kind, string tool, Dictionary<string, object> arguments,
        IReadOnlyList<string>? needs = null, bool confirm = false)
        => new(id, label, kind, tool, arguments, needs ?? [], confirm);

    private static Dictionary<string, object> Args(params (string Name, object Value)[] arguments)
        => arguments.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal);

    private static bool Has(IReadOnlyList<string> eligible, OperatorAction action)
        => eligible.Contains(OperatorActionAccess.NameOf(action), StringComparer.Ordinal);

    private static bool Is(string? status, ResolutionStatus expected)
        => string.Equals(status, expected.ToString(), StringComparison.OrdinalIgnoreCase);

    // The recommended option first, then the rest in their fixed order, without repeats.
    private static List<NextAction> Collect(NextAction first, params NextAction?[] rest)
    {
        var list = new List<NextAction> { first };
        list.AddRange(rest.OfType<NextAction>().Where(option => !ReferenceEquals(option, first)));
        return list.Take(MaxActions).ToList();
    }
}
