using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NimBus.Core.Inbox;

namespace NimBus.Inbox.AzureTables;

/// <summary>Registers the Azure Table Storage subscriber inbox.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Uses a pre-provisioned inbox table on the storage account's primary endpoint.</summary>
    /// <param name="services">The adapter's service collection.</param>
    /// <param name="table">A client authenticated for read, add and delete operations.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddNimBusAzureTableInbox(this IServiceCollection services, TableClient table)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(table);
        services.TryAddSingleton(_ => new AzureTableInbox(table));
        services.TryAddSingleton<IInboxStore>(sp => sp.GetRequiredService<AzureTableInbox>());
        services.TryAddKeyedSingleton<IInboxStore>(InboxStore.AzureTable, (sp, _) => sp.GetRequiredService<AzureTableInbox>());
        return services;
    }
}
