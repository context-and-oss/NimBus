namespace CrmErpDemo.Contracts.Http;

/// <summary>
/// One page of a list in the demo APIs. A list endpoint takes <c>?page=</c> (1-based) and an
/// optional <c>?pageSize=</c>, answers with that page as a JSON array, and puts the number of
/// rows the query matches in the <see cref="TotalCountHeader"/> response header.
/// </summary>
/// <param name="Page">The 1-based page number.</param>
/// <param name="PageSize">The number of rows on a page, 1 to <see cref="MaxPageSize"/>.</param>
public readonly record struct PageRequest(int Page, int PageSize)
{
    /// <summary>The default page size, and the most a caller can ask for.</summary>
    public const int MaxPageSize = 100;

    /// <summary>The response header that carries the number of rows the query matches.</summary>
    public const string TotalCountHeader = "X-Total-Count";

    // Keeps (Page - 1) * PageSize inside an int.
    private const int MaxPage = 1_000_000;

    /// <summary>The number of rows before this page.</summary>
    public int Skip => (Page - 1) * PageSize;

    /// <summary>
    /// Reads the query parameters. Null when the caller sent no <c>page</c>: the list is then
    /// returned whole, as it was before paging, which the e2e helpers rely on.
    /// </summary>
    public static PageRequest? From(int? page, int? pageSize) =>
        page is null
            ? null
            : new PageRequest(Math.Clamp(page.Value, 1, MaxPage), Math.Clamp(pageSize ?? MaxPageSize, 1, MaxPageSize));

    /// <summary>The number of pages <paramref name="total"/> rows fill.</summary>
    public static int PageCount(int total, int pageSize) =>
        total <= 0 ? 0 : (total + pageSize - 1) / pageSize;
}
