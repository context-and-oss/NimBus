using System.Globalization;
using CrmErpDemo.Contracts.Http;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Endpoints;

internal static class PagedList
{
    /// <summary>
    /// Answers a list request with a JSON array. Without <c>?page=</c> that is every row, as
    /// before paging. With it, it is one page, and the X-Total-Count header carries the number
    /// of rows the query matches. Order the query first.
    /// </summary>
    public static async Task<IResult> ToListResultAsync<T>(
        this IQueryable<T> rows, int? page, int? pageSize, HttpResponse response, CancellationToken cancellationToken)
    {
        if (PageRequest.From(page, pageSize) is not { } request)
        {
            return Results.Ok(await rows.ToListAsync(cancellationToken));
        }

        var total = await rows.CountAsync(cancellationToken);
        response.Headers[PageRequest.TotalCountHeader] = total.ToString(CultureInfo.InvariantCulture);
        return Results.Ok(await rows.Skip(request.Skip).Take(request.PageSize).ToListAsync(cancellationToken));
    }
}
