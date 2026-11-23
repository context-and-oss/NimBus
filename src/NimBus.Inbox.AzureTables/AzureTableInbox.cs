using System.Globalization;
using Azure;
using Azure.Data.Tables;
using NimBus.Core.Inbox;

namespace NimBus.Inbox.AzureTables;

internal sealed class AzureTableInbox(TableClient table) : IInboxStore
{
    private const int PurgeLimit = 1000;
    private const string ExpiryPrefix = "E";

    public async Task<bool> HasProcessedAsync(string endpointId, string messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await table.GetEntityAsync<TableEntity>(PartitionKey(endpointId), ReceiptKey(endpointId, messageId),
                cancellationToken: cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (IsMissingEntity(ex))
        {
            return false;
        }
    }

    public async Task RecordProcessedAsync(string endpointId, string messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var partition = PartitionKey(endpointId);
        var receipt = new TableEntity(partition, ReceiptKey(endpointId, messageId));
        var expiry = new TableEntity(partition, $"{ExpiryKey(DateTimeOffset.UtcNow)}-{receipt.RowKey}");
        try
        {
            await table.SubmitTransactionAsync([
                new TableTransactionAction(TableTransactionActionType.Add, receipt),
                new TableTransactionAction(TableTransactionActionType.Add, expiry),
            ], cancellationToken);
        }
        catch (TableTransactionFailedException ex) when (ex.ErrorCode == "EntityAlreadyExists" && ex.FailedTransactionActionIndex == 0)
        {
            // A concurrent or retried insert already committed both entities.
        }
    }

    public async Task<int> PurgeExpiredAsync(string endpointId, DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var partition = PartitionKey(endpointId);
        var upperBound = ExpiryKey(olderThan);
        var filter = TableClient.CreateQueryFilter(
            $"PartitionKey eq {partition} and RowKey ge {ExpiryPrefix} and RowKey lt {upperBound}");
        var deleted = 0;
        var examined = 0;
        await foreach (var expiry in table.QueryAsync<TableEntity>(filter, maxPerPage: PurgeLimit, cancellationToken: cancellationToken))
        {
            var receipt = new TableEntity(partition, expiry.RowKey[(expiry.RowKey.IndexOf('-', StringComparison.Ordinal) + 1)..]);
            try
            {
                // Guard the pair with the observed expiry ETag. A stale cleaner must never
                // delete a fresh receipt for an identity purged and subsequently re-recorded.
                await table.SubmitTransactionAsync([
                    new TableTransactionAction(TableTransactionActionType.Delete, expiry, expiry.ETag),
                    new TableTransactionAction(TableTransactionActionType.Delete, receipt, ETag.All),
                ], cancellationToken);
                deleted++;
            }
            catch (TableTransactionFailedException ex) when (ex.FailedTransactionActionIndex == 0 &&
                (IsMissingEntity(ex) || ex.ErrorCode == "UpdateConditionNotSatisfied"))
            {
                // Another cleanup already removed this generation.
            }

            if (++examined == PurgeLimit)
                break;
        }

        return deleted;
    }

    private static bool IsMissingEntity(RequestFailedException ex) =>
        ex.Status == 404 && ex.ErrorCode is "ResourceNotFound" or "EntityNotFound";

    private static string PartitionKey(string endpointId) =>
        Convert.ToHexString(InboxIdentity.ComputeHash(endpointId, string.Empty));

    private static string ReceiptKey(string endpointId, string messageId) =>
        "R" + Convert.ToHexString(InboxIdentity.ComputeHash(endpointId, messageId));

    // Fixed-width UTC ticks sort chronologically. Using the bare prefix as the upper
    // bound excludes every receipt created at exactly the cutoff.
    private static string ExpiryKey(DateTimeOffset time) =>
        ExpiryPrefix + time.UtcTicks.ToString("D19", CultureInfo.InvariantCulture);
}
