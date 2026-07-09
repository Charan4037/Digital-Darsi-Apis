namespace DOSApi.Helpers;

/// <summary>
/// Generates Laravel-style pagination response with links and meta.
/// </summary>
public static class PaginationHelper
{
    public static object Paginate<T>(IEnumerable<T> data, int total, int page, int perPage, string path)
    {
        var lastPage = total == 0 ? 1 : (int)Math.Ceiling((double)total / perPage);
        var from = total == 0 ? (int?)null : (page - 1) * perPage + 1;
        var to = total == 0 ? (int?)null : Math.Min(page * perPage, total);

        var metaLinks = new List<object>();
        metaLinks.Add(new { url = page > 1 ? $"{path}?page={page - 1}" : (string?)null, label = "&laquo; Previous", active = false });
        for (int i = 1; i <= lastPage; i++)
            metaLinks.Add(new { url = $"{path}?page={i}", label = i.ToString(), active = i == page });
        metaLinks.Add(new { url = page < lastPage ? $"{path}?page={page + 1}" : (string?)null, label = "Next &raquo;", active = false });

        return new
        {
            data,
            links = new
            {
                first = $"{path}?page=1",
                last = $"{path}?page={lastPage}",
                prev = page > 1 ? $"{path}?page={page - 1}" : (string?)null,
                next = page < lastPage ? $"{path}?page={page + 1}" : (string?)null
            },
            meta = new
            {
                current_page = page,
                from,
                last_page = lastPage,
                links = metaLinks,
                path,
                per_page = perPage,
                to,
                total
            }
        };
    }
}
