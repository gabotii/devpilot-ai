using Microsoft.EntityFrameworkCore;

namespace DevPilot.Application.Common;

public sealed record PageRequest(int Page = 1, int PageSize = 25)
{
    public const int MaxPageSize = 100;

    public int SafePage => Math.Max(1, Page);
    public int SafePageSize => Math.Clamp(PageSize, 1, MaxPageSize);
    public int Skip => (SafePage - 1) * SafePageSize;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public static class PagingExtensions
{
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(this IQueryable<T> query, PageRequest page, CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        var items = await query.Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        return new PagedResult<T>(items, page.SafePage, page.SafePageSize, total);
    }
}

/// <summary>Parses "field" / "-field" sort expressions against a whitelist.</summary>
public readonly record struct SortSpec(string Field, bool Descending)
{
    public static SortSpec Parse(string? sort, string defaultField, bool defaultDescending, IReadOnlySet<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(sort))
        {
            return new SortSpec(defaultField, defaultDescending);
        }

        var descending = sort.StartsWith('-');
        var field = sort.TrimStart('-', '+').Trim();
        if (!allowed.Contains(field))
        {
            throw new FluentValidation.ValidationException(
                [new FluentValidation.Results.ValidationFailure("sort", $"Unsupported sort field '{field}'. Allowed: {string.Join(", ", allowed)}.")]);
        }

        return new SortSpec(field, descending);
    }
}
