using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using NimBus.Core;
using NimBus.Manager;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.ManagementApi;
using CoreConstants = NimBus.Core.Messages.Constants;

namespace NimBus.WebApp.Services;

// Composition root for AdminService. The class implements the broad
// IAdminService surface, but the work is split topically across partial
// files in this same folder:
//   AdminService.Topology.cs   — SB rule/subscription audit + cleanup
//   AdminService.Resubmit.cs   — bulk resubmit, dead-letter recovery,
//                                deferred reprocess
//   AdminService.Purge.cs      — session/subscription/status-based delete
//                                and skip operations
//   AdminService.Copy.cs       — Cosmos-only cross-account data copy
//
// Adding a new operation? Put it in the partial file whose topic it fits;
// only the constructor, the storage capability guard, the shared field
// declarations, and the topology-comparison DTOs live in this file.
public partial class AdminService : IAdminService
{
    private readonly IPlatform _platform;
    private readonly IMessageTrackingStore _messageStore;
    // Cosmos-only: cross-account copy + bulk-delete-by-recipient operations use the raw
    // Cosmos client directly. Null when the active provider is not Cosmos.
    private readonly CosmosClient? _rawCosmosClient;
    private readonly IStorageProviderCapabilities _capabilities;
    private readonly ServiceBusAdministrationClient _sbAdmin;
    private readonly ServiceBusClient _sbClient;
    private readonly IManagerClient _managerClient;
    private readonly ISubscriptionAdminService _subscriptionAdmin;
    private readonly ILogger<AdminService> _logger;

    private const int PageSize = 20;
    private const int AgeThresholdMinutes = 10;
    private const string DatabaseId = "MessageDatabase";
    private const string MessagesContainer = "messages";

    public AdminService(
        IPlatform platform,
        IMessageTrackingStore messageStore,
        IStorageProviderCapabilities capabilities,
        ServiceBusAdministrationClient sbAdmin,
        ServiceBusClient sbClient,
        IManagerClient managerClient,
        ISubscriptionAdminService subscriptionAdmin,
        ILogger<AdminService> logger,
        CosmosClient? rawCosmosClient = null)
    {
        _platform = platform;
        _messageStore = messageStore;
        _capabilities = capabilities;
        _rawCosmosClient = rawCosmosClient;
        _sbAdmin = sbAdmin;
        _sbClient = sbClient;
        _managerClient = managerClient;
        _subscriptionAdmin = subscriptionAdmin;
        _logger = logger;
    }

    private void EnsureCosmosOnlyOperation(string operationName)
    {
        if (!_capabilities.SupportsCrossAccountCopy || _rawCosmosClient is null)
        {
            throw new NotSupportedException(
                $"{operationName} is supported only when the Cosmos DB storage provider is active. " +
                "Operators using the SQL Server provider should use SQL Server backup/restore tooling instead.");
        }
    }

    public Task<PlatformConfig> GetPlatformConfigAsync(IPlatform platform)
    {
        var config = new PlatformConfig
        {
            ResolverId = CoreConstants.ResolverId,
            ManagerId = CoreConstants.ManagerId,
            ContinuationId = CoreConstants.ContinuationId,
            EventId = CoreConstants.EventId,
            RetryId = CoreConstants.RetryId,
            Endpoints = platform.Endpoints.Select(ep => new PlatformEndpoint
            {
                Id = ep.Id,
                Name = ep.Name,
                EventTypesProduced = ep.EventTypesProduced.Select(et => new PlatformEventType
                {
                    Id = et.Id,
                    Name = et.Name
                }).ToList(),
                EventTypesConsumed = ep.EventTypesConsumed.Select(et => new PlatformEventType
                {
                    Id = et.Id,
                    Name = et.Name
                }).ToList()
            }).ToList()
        };

        return Task.FromResult(config);
    }

    /// <summary>
    /// Rewrites a session's stored state with <paramref name="clear"/> applied, so an operator
    /// action resets what it must and keeps the replay position the endpoint's ordering guard
    /// relies on. State that can't be read is cleared outright, as these actions always did.
    /// </summary>
    internal static async Task ClearSessionState(ServiceBusSessionReceiver receiver, Action<NimBus.Core.Messages.SessionState> clear)
    {
        var stored = await receiver.GetSessionStateAsync();
        BinaryData? cleared = null;
        try
        {
            var state = NimBus.Core.Messages.SessionState.FromBinaryData(stored);
            clear(state);
            cleared = state.ToBinaryData();
        }
        catch (System.Text.Json.JsonException)
        {
        }

        await receiver.SetSessionStateAsync(cleared);
    }

    // ─────────── Internal DTOs for topology comparison ───────────

    private sealed class TopologySnapshot
    {
        public string Name { get; set; } = string.Empty;
        public List<SubscriptionSnapshot> Subscriptions { get; set; } = new List<SubscriptionSnapshot>();
    }

    private sealed class SubscriptionSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public List<RuleSnapshot> Rules { get; set; } = new List<RuleSnapshot>();
        public bool IsDeprecated { get; set; }
    }

    private sealed class RuleSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public string SubscriptionName { get; set; } = string.Empty;
        public bool IsDeprecated { get; set; }
    }
}
