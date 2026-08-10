using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models;
using DOSApi.Services;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Small admin-editable business-rule settings that don't warrant their own
/// dedicated table — stored as rows in the pre-existing Bagisto
/// <c>core_config</c> table, same convention as
/// ShopCheckoutSettingsController's minimum-order-value setting. Starts with
/// just the refund window; add more keys here as they come up rather than
/// building a new controller per setting.
/// Routes: /api/v1/admin/settings
/// </summary>
[Route("api/v1/admin/settings")]
[Tags("Admin – Settings")]
public class AdminSettingsController : AdminBaseController
{
    private readonly DOSDbContext _db;
    private readonly AccountService _accountService;

    public AdminSettingsController(DOSDbContext db, AccountService accountService, IConfiguration config) : base(db, config)
    {
        _db = db;
        _accountService = accountService;
    }

    /// <summary>Get the current refund window (in days)</summary>
    /// <remarks>
    /// Once an order is delivered, the customer's "Request Refund" and the admin's
    /// "Issue Refund" buttons only stay available for this many days. Defaults to 7 when
    /// no admin override has been saved yet.
    /// </remarks>
    [HttpGet("refund-window")]
    public async Task<IActionResult> GetRefundWindow()
    {
        if (!await HasPermissionAsync("orders")) return AdminUnauthorized();

        var days = await _accountService.GetRefundWindowDaysAsync();
        return Ok(new { success = true, data = new { days } });
    }

    /// <summary>Update the refund window (in days)</summary>
    /// <param name="req">New window length, in whole days (must be 1 or more)</param>
    [HttpPatch("refund-window")]
    public async Task<IActionResult> UpdateRefundWindow([FromBody] UpdateRefundWindowRequest req)
    {
        if (!await HasPermissionAsync("orders", requireWrite: true)) return AdminForbidden("orders");
        if (req.Days < 1)
            return BadRequest(new { success = false, message = "days must be 1 or greater." });

        var now = DateTime.UtcNow;
        var row = await _db.CoreConfigs.FirstOrDefaultAsync(c => c.Code == AccountService.RefundWindowDaysConfigKey);
        if (row == null)
        {
            _db.CoreConfigs.Add(new CoreConfig
            {
                Code = AccountService.RefundWindowDaysConfigKey,
                Value = req.Days.ToString(),
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            row.Value = req.Days.ToString();
            row.UpdatedAt = now;
        }
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = $"Refund window set to {req.Days} day(s).",
            data = new { days = req.Days },
        });
    }

    public record UpdateRefundWindowRequest(int Days);
}
