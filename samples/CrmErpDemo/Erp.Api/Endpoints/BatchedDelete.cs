using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Endpoints;

/// <summary>
/// Deletes a table in short committed batches. One unbounded DELETE over a large table
/// runs past the 30-second command timeout on a Basic (5 DTU) Azure SQL database, rolls
/// back and deletes nothing; batches keep each statement short and keep the progress
/// made so far when the request is cut off.
/// </summary>
internal static class BatchedDelete
{
    /// <summary>Rows deleted per statement; the raw SQL in AdminEndpoints repeats it as TOP (1000).</summary>
    public const int BatchSize = 1000;

    /// <summary>Deletes every row of <paramref name="rows"/>, <see cref="BatchSize"/> at a time.</summary>
    public static Task<int> AllAsync<T>(IQueryable<T> rows, CancellationToken cancellationToken)
        where T : class =>
        RepeatAsync(ct => rows.Take(BatchSize).ExecuteDeleteAsync(ct), cancellationToken);

    /// <summary>
    /// Runs <paramref name="deleteBatch"/>, which deletes at most <see cref="BatchSize"/> rows,
    /// until a batch deletes fewer, and returns the total.
    /// </summary>
    public static async Task<int> RepeatAsync(
        Func<CancellationToken, Task<int>> deleteBatch, CancellationToken cancellationToken)
    {
        var total = 0;
        int deleted;
        do
        {
            // A raw statement that deletes nothing (such as a skipped IF) reports -1.
            deleted = Math.Max(0, await deleteBatch(cancellationToken).ConfigureAwait(false));
            total += deleted;
        }
        while (deleted == BatchSize);

        return total;
    }
}
