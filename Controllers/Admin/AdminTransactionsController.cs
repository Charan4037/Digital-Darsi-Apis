using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Admin;
using DOSApi.Services;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin transactions controller.
/// Routes: /api/v1/admin/transactions
/// </summary>
[Route("api/v1/admin/transactions")]
[Tags("Admin � Transactions")]
public class AdminTransactionsController : AdminBaseController
{
    private readonly DOSDbContext _db;

    public AdminTransactionsController(DOSDbContext db, IConfiguration config) : base(db, config)
    {
        _db = db;
    }

    /// <summary>List all transactions across all vendors with summary</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20)
    {
        if (!await HasPermissionAsync("transactions")) return AdminUnauthorized();
        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 20;

        // Get all orders to create transaction records
        var query = _db.Orders
            .Include(o => o.Customer)
            .Include(o => o.Items).ThenInclude(i => i.Product)
            .Where(o => o.Status != "canceled")
            .AsNoTracking();

        var total = await query.CountAsync();
        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        // TODO: Implement actual transaction table if needed
        // For now, create transactions from orders
        var transactions = orders.Select((o, idx) => new TransactionDto
        {
            Id = o.Id,
            OrderId = o.IncrementId ?? $"#ORD-{o.Id}",
            VendorName = o.Items
                .Select(i => ProductService.ExtractVendorName(i.Product?.Additional, "en"))
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "",
            Date = o.CreatedAt ?? DateTime.UtcNow,
            Amount = o.GrandTotal ?? 0,
            IsCredit = o.Status != "pending",
            Status = o.Status switch
            {
                "completed" => "Settled",
                "pending" => "Pending",
                _ => "Settled"
            },
            PaymentMethod = "" // TODO: Get from order payment
        }).ToList();

        // Calculate summary across ALL transactions
        var allTransactions = await _db.Orders
            .Where(o => o.Status != "canceled")
            .ToListAsync();

        var totalSettled = allTransactions
            .Where(o => o.Status == "completed")
            .Sum(o => o.GrandTotal ?? 0);

        var totalPending = allTransactions
            .Where(o => o.Status == "pending")
            .Sum(o => o.GrandTotal ?? 0);

        var totalRefunded = await _db.Refunds
            .Where(r => r.State == "refunded")
            .SumAsync(r => r.GrandTotal ?? 0);

        return Ok(new TransactionListResponse
        {
            Data = transactions,
            Meta = new PaginationMeta
            {
                Total = total,
                CurrentPage = page,
                LastPage = (total + limit - 1) / limit,
                PerPage = limit
            },
            Summary = new TransactionSummary
            {
                TotalSettled = totalSettled,
                TotalPending = totalPending,
                TotalRefunded = totalRefunded
            }
        });
    }
}
