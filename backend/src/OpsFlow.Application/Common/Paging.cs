namespace OpsFlow.Application.Common;

public static class Paging
{
    /// <summary>Highest accepted page number; with the maximum page size the skip offset always fits an int.</summary>
    public const int MaxPage = 1_000_000;

    /// <summary>Clamps untrusted page/pageSize so <c>(page - 1) * pageSize</c> can never overflow.</summary>
    public static (int Page, int PageSize, int Skip) Normalize(
        int page,
        int pageSize,
        int maxPageSize
    )
    {
        var p = Math.Clamp(page, 1, MaxPage);
        var s = Math.Clamp(pageSize, 1, maxPageSize);
        return (p, s, (p - 1) * s);
    }
}
