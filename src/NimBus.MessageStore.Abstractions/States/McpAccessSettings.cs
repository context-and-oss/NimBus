using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace NimBus.MessageStore.States;

/// <summary>
/// The site Owner's policy for the operator MCP endpoint (Spec 037). It can only narrow what the
/// deployment, Entra scopes and NimBus roles already allow. A single record: one row on SQL
/// Server, one document with a fixed id on Cosmos. A store that has never been written returns
/// these defaults with a null <see cref="Revision"/>, which reproduce the behaviour from before
/// the policy existed.
/// </summary>
public class McpAccessSettings
{
    /// <summary>Fixed id of the singleton record.</summary>
    public const string SingletonId = "McpAccessSettings";

    /// <summary>Record id. Always <see cref="SingletonId"/>.</summary>
    [JsonProperty(PropertyName = "id")]
    public string Id { get; set; } = SingletonId;

    /// <summary>A new GUID on every save; null when nothing was ever saved.</summary>
    public string? Revision { get; set; }

    /// <summary>Who saved this revision. Display only.</summary>
    public string? UpdatedBy { get; set; }

    /// <summary>When this revision was saved. Display only.</summary>
    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>Serve <c>/mcp</c>, within what the deployment allows.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Which optional capabilities agents may use. The read tools have no switch.</summary>
    public McpCapabilitySettings Capabilities { get; set; } = new();

    /// <summary>Accept app-only (workload) tokens for the read tools.</summary>
    public bool AllowWorkloads { get; set; } = true;

    /// <summary>
    /// Let workloads holding the <c>Nimbus.Classify</c> app role record failure classifications
    /// (Spec 044 §6.6). Effective only while <see cref="AllowWorkloads"/> is on. Default false.
    /// </summary>
    public bool AllowWorkloadClassification { get; set; }

    /// <summary>
    /// Let workloads holding the <c>Nimbus.Resubmit</c> app role resubmit failed messages
    /// unattended (Spec 044 Phase 2), within <see cref="WorkloadResubmitsPerDay"/> and
    /// <see cref="WorkloadResubmitsPerMessage"/>. Effective only while <see cref="AllowWorkloads"/>
    /// is on and the operation journal is available. There is no workload switch for skip.
    /// Default false.
    /// </summary>
    public bool AllowWorkloadResubmit { get; set; }

    /// <summary>Resubmits each workload client may make in a rolling 24 hours, 0-500. Default 20.</summary>
    public int WorkloadResubmitsPerDay { get; set; } = DefaultWorkloadResubmitsPerDay;

    /// <summary>
    /// Unsuccessful workload resubmits of one message, across all agents, after which NimBus
    /// refuses another and the message stays with a person; 1-5. Default 2.
    /// </summary>
    public int WorkloadResubmitsPerMessage { get; set; } = DefaultWorkloadResubmitsPerMessage;

    /// <summary>Default for <see cref="WorkloadResubmitsPerDay"/>.</summary>
    public const int DefaultWorkloadResubmitsPerDay = 20;

    /// <summary>Highest <see cref="WorkloadResubmitsPerDay"/>.</summary>
    public const int MaximumWorkloadResubmitsPerDay = 500;

    /// <summary>Default for <see cref="WorkloadResubmitsPerMessage"/>.</summary>
    public const int DefaultWorkloadResubmitsPerMessage = 2;

    /// <summary>Highest <see cref="WorkloadResubmitsPerMessage"/>.</summary>
    public const int MaximumWorkloadResubmitsPerMessage = 5;

    /// <summary>Who may connect.</summary>
    public McpPeopleSettings People { get; set; } = new();

    /// <summary>Which client applications may connect.</summary>
    public McpClientSettings Clients { get; set; } = new();

    /// <summary>Which endpoints agents see and where they may change messages.</summary>
    public McpEndpointSettings Endpoints { get; set; } = new();

    /// <summary>Rate limits lower than the deployment's.</summary>
    public McpLimitSettings Limits { get; set; } = new();

    /// <summary>A deep copy.</summary>
    public McpAccessSettings Clone()
        => JsonConvert.DeserializeObject<McpAccessSettings>(JsonConvert.SerializeObject(this))!;
}

/// <summary>Switches for the MCP capabilities beyond the read tools.</summary>
public class McpCapabilitySettings
{
    /// <summary><c>nimbus_get_message</c> with <c>includePayload=true</c>.</summary>
    public bool Payloads { get; set; } = true;

    /// <summary><c>nimbus_set_message_reported</c>.</summary>
    public bool Report { get; set; } = true;

    /// <summary><c>nimbus_classify_failure</c>.</summary>
    public bool Classify { get; set; } = true;

    /// <summary><c>nimbus_resubmit_message</c>, and <c>nimbus_prepare_action</c> for it.</summary>
    public bool Resubmit { get; set; } = true;

    /// <summary><c>nimbus_skip_message</c>, and <c>nimbus_prepare_action</c> for it.</summary>
    public bool Skip { get; set; } = true;
}

/// <summary>Who may connect.</summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum McpPeopleMode
{
    /// <summary>Everyone the NimBus roles admit.</summary>
    All,

    /// <summary>Only the listed users and groups.</summary>
    Listed,
}

/// <summary>The people allowlist.</summary>
public class McpPeopleSettings
{
    /// <summary>Whether the list applies.</summary>
    public McpPeopleMode Mode { get; set; } = McpPeopleMode.All;

    /// <summary>Users and groups, in the forms Access Control grants use.</summary>
    public List<McpPrincipal> Principals { get; set; } = new();
}

/// <summary>One allowed user or group.</summary>
public class McpPrincipal
{
    /// <summary>An email address, a user object id or a group object id.</summary>
    public string Principal { get; set; } = string.Empty;

    /// <summary>Optional display label.</summary>
    public string? Label { get; set; }
}

/// <summary>Which client applications may connect.</summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum McpClientMode
{
    /// <summary>Any client Entra lets obtain a token.</summary>
    Any,

    /// <summary>Only the approved clients.</summary>
    Approved,
}

/// <summary>The client allowlist.</summary>
public class McpClientSettings
{
    /// <summary>Whether the list applies.</summary>
    public McpClientMode Mode { get; set; } = McpClientMode.Any;

    /// <summary>Approved clients.</summary>
    public List<McpApprovedClient> Approved { get; set; } = new();
}

/// <summary>One approved client application.</summary>
public class McpApprovedClient
{
    /// <summary>Application (client) id, matched against the <c>azp</c> claim, then <c>appid</c>.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether the client may use the tools that change messages.</summary>
    public bool MayChange { get; set; }
}

/// <summary>Which endpoints agents can see.</summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum McpEndpointVisibility
{
    /// <summary>Every endpoint the caller can read.</summary>
    All,

    /// <summary>Every endpoint the caller can read except the hidden ones.</summary>
    AllExcept,
}

/// <summary>Where agents may change messages.</summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum McpChangeScope
{
    /// <summary>Every visible endpoint.</summary>
    AllVisible,

    /// <summary>Only the listed endpoints.</summary>
    Listed,
}

/// <summary>Endpoint rules.</summary>
public class McpEndpointSettings
{
    /// <summary>Which endpoints agents can see.</summary>
    public McpEndpointVisibility Visibility { get; set; } = McpEndpointVisibility.All;

    /// <summary>Hidden endpoint ids, used with <see cref="McpEndpointVisibility.AllExcept"/>.</summary>
    public List<string> Hidden { get; set; } = new();

    /// <summary>Where agents may change messages.</summary>
    public McpChangeScope Changes { get; set; } = McpChangeScope.AllVisible;

    /// <summary>Endpoint ids, used with <see cref="McpChangeScope.Listed"/>.</summary>
    public List<string> ChangeOn { get; set; } = new();
}

/// <summary>Rate limits lower than the deployment's; null means the deployment's value.</summary>
public class McpLimitSettings
{
    /// <summary>MCP requests per window, per caller.</summary>
    public int? RequestsPerWindow { get; set; }

    /// <summary>Message changes per window, per caller.</summary>
    public int? MutationsPerWindow { get; set; }
}
