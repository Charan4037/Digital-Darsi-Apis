namespace BagistoApi.GraphQL.Types;

/// <summary>
/// Generic cursor-based connection matching Bagisto's edges/node pattern.
/// </summary>
public class Connection<T>
{
    public List<Edge<T>> Edges { get; set; } = new();
    public PageInfo PageInfo { get; set; } = new();
    public int TotalCount { get; set; }
}

public class Edge<T>
{
    public T? Node { get; set; }
    public string Cursor { get; set; } = "";
}

public class PageInfo
{
    public string StartCursor { get; set; } = "";
    public string EndCursor { get; set; } = "";
    public bool HasNextPage { get; set; }
    public bool HasPreviousPage { get; set; }
}

public static class ConnectionHelper
{
    public static Connection<T> ToConnection<T>(IEnumerable<T> items, int totalCount, int offset, int limit)
    {
        var list = items.ToList();
        return new Connection<T>
        {
            Edges = list.Select((item, idx) => new Edge<T>
            {
                Node = item,
                Cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes((offset + idx).ToString()))
            }).ToList(),
            PageInfo = new PageInfo
            {
                StartCursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(offset.ToString())),
                EndCursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes((offset + list.Count - 1).ToString())),
                HasNextPage = offset + list.Count < totalCount,
                HasPreviousPage = offset > 0
            },
            TotalCount = totalCount
        };
    }

    public static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return 0;
        try
        {
            var bytes = Convert.FromBase64String(cursor);
            var str = System.Text.Encoding.UTF8.GetString(bytes);
            return int.TryParse(str, out var val) ? val + 1 : 0; // +1 because "after" means after that cursor
        }
        catch { return 0; }
    }
}
