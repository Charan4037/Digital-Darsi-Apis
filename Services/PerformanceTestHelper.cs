using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Catalog;

namespace DOSApi.Services;

public class PerformanceTestHelper
{
    private readonly DOSDbContext _db;

    public PerformanceTestHelper(DOSDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Benchmark a query to measure performance before/after optimization.
    /// </summary>
    public async Task<(long elapsedMs, int resultCount)> BenchmarkQueryAsync(string description, Func<Task<List<Product>>> queryDelegate)
    {
        var sw = Stopwatch.StartNew();
        var results = await queryDelegate();
        sw.Stop();

        Console.WriteLine($"[{description}] Elapsed: {sw.ElapsedMilliseconds}ms, Results: {results.Count}");
        return (sw.ElapsedMilliseconds, results.Count);
    }

    /// <summary>
    /// Test pagination performance with QueryProductsAsync.
    /// </summary>
    public async Task TestPaginationPerformance(ProductService productService)
    {
        Console.WriteLine("═══ Pagination Performance Test ═══");

        var testCases = new[]
        {
            ("Page 1 (Offset 0)", 0, 20),
            ("Page 2 (Offset 20)", 20, 20),
            ("Page 5 (Offset 80)", 80, 20),
            ("Large Result (Offset 0, Limit 100)", 0, 100),
        };

        foreach (var (desc, offset, limit) in testCases)
        {
            var sw = Stopwatch.StartNew();
            var (items, total) = await productService.QueryProductsAsync(null, "TITLE", false, null, offset, limit);
            sw.Stop();

            Console.WriteLine($"  {desc}: {sw.ElapsedMilliseconds}ms (Total: {total}, Returned: {items.Count})");
        }
    }
}