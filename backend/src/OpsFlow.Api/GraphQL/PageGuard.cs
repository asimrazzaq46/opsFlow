using OpsFlow.Application.Common;

namespace OpsFlow.Api.GraphQL;

/// <summary>GraphQL rejects out-of-range paging with a validation error (REST silently clamps); the services still re-normalize as defence in depth.</summary>
internal static class PageGuard
{
    public const int MaxPageSize = 100;

    public static int Page(int page) => page is >= 1 and <= Paging.MaxPage ? page : throw AppException.Validation("page", $"page must be between 1 and {Paging.MaxPage}.");

    public static int Size(int size, int max = MaxPageSize) => size >= 1 && size <= max ? size : throw AppException.Validation("pageSize", $"pageSize must be between 1 and {max}.");

    public static TicketFilterInput Check(TicketFilterInput filter)
    {
        Page(filter.Page);
        Size(filter.PageSize);
        return filter;
    }
}
