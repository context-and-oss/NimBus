using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NimBus.Core.Extensions;
using NimBus.Core.Inbox;
using NimBus.Core.Messages;
using NimBus.SDK.Hosting;

namespace NimBus.SDK.Extensions;

internal static class InboxRegistration
{
    public static void AddServices(IServiceCollection services, string endpointId, InboxOptions? options)
    {
        if (options is null)
            return;

        services.TryAddSingleton<MessageLifecycleNotifier>();
        services.TryAddSingleton(TimeProvider.System);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(InboxRegistrationMarker)))
            return;

        services.AddSingleton<InboxRegistrationMarker>();
        services.AddSingleton<IHostedService>(serviceProvider =>
            new InboxPurgeHostedService(
                ResolveStore(serviceProvider, options),
                endpointId,
                options,
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetService<ILogger<InboxPurgeHostedService>>()));
    }

    public static IEventContextHandler Decorate(
        IServiceProvider serviceProvider,
        IEventContextHandler inner,
        InboxOptions? options,
        string endpointId)
    {
        if (options is null)
            return inner;

        // checkHandledUpstream: this composition also hands StrictMessageHandler the
        // pre-session-guard detector (CreateDuplicateDetector below), so the decorator is
        // record-only — a fresh delivery costs exactly one store check and one record.
        // endpointScope: the configured subscriber endpoint keeps the deduplication key
        // stable — an external CloudEvent has no user.To, so the transport would otherwise
        // substitute the mapped event type.
        return new InboxMiddleware(
            inner,
            ResolveStore(serviceProvider, options),
            serviceProvider.GetRequiredService<MessageLifecycleNotifier>(),
            serviceProvider.GetService<ILogger<InboxMiddleware>>(),
            checkHandledUpstream: true,
            endpointScope: endpointId);
    }

    /// <summary>
    /// Creates the duplicate detector handed to <c>StrictMessageHandler</c> so the inbox check
    /// runs before the session-state guards, or <see langword="null"/> when no inbox is configured.
    /// The detector is scoped to the configured subscriber endpoint so external CloudEvents
    /// (whose <c>To</c> is only the mapped event type) share the same stable key.
    /// </summary>
    public static InboxDuplicateDetector? CreateDuplicateDetector(
        IServiceProvider serviceProvider,
        InboxOptions? options,
        string endpointId)
    {
        if (options is null)
            return null;

        return new InboxDuplicateDetector(
            ResolveStore(serviceProvider, options),
            serviceProvider.GetRequiredService<MessageLifecycleNotifier>(),
            serviceProvider.GetService<ILogger<InboxDuplicateDetector>>(),
            endpointScope: endpointId);
    }

    private static IInboxStore ResolveStore(
        IServiceProvider serviceProvider,
        InboxOptions options)
    {
        var provider = options.DeduplicationStore!.Value;
        var store = serviceProvider.GetKeyedService<IInboxStore>(provider);
        if (store is not null)
            return store;

        var registration = provider switch
        {
            InboxStore.Cosmos => "services.AddNimBusCosmosInbox(...) from NimBus.MessageStore.CosmosDb",
            InboxStore.AzureTable => "services.AddNimBusAzureTableInbox(...) from NimBus.Inbox.AzureTables",
            InboxStore.SqlServer => "services.AddNimBusSqlServerInbox(...) from NimBus.Inbox.SqlServer",
            InboxStore.InMemory => "services.AddNimBusInMemoryInbox() from NimBus.Testing",
            _ => "the matching inbox provider extension",
        };
        throw new InvalidOperationException(
            $"Inbox provider '{provider}' is selected but no keyed {nameof(IInboxStore)} is registered " +
            $"for {nameof(InboxStore)}.{provider}. Register it with {registration} before resolving " +
            "the subscriber or hosted services.");
    }

    private sealed class InboxRegistrationMarker;
}
