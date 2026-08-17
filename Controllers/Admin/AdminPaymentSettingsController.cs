using Microsoft.AspNetCore.Mvc;
using DOSApi.Data;
using DOSApi.Services;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin control over which payment methods are offered (COD/online) and the
/// Razorpay credentials used to actually process online payments. Follows
/// AdminDeliveryTypesController/AdminSettingsController's pattern exactly —
/// see PaymentSettingsService for the underlying core_config storage.
/// Routes: /api/v1/admin/settings/payment
/// </summary>
[Route("api/v1/admin/settings/payment")]
[Tags("Admin – Payment Settings")]
public class AdminPaymentSettingsController : AdminBaseController
{
    private const string Resource = "payment_settings";
    private readonly PaymentSettingsService _paymentSettings;

    public AdminPaymentSettingsController(DOSDbContext db, IConfiguration config, PaymentSettingsService paymentSettings) : base(db, config)
    {
        _paymentSettings = paymentSettings;
    }

    public record MethodsRequest(bool CodEnabled, bool OnlineEnabled);
    public record RazorpayKeysRequest(string? KeyId, string? KeySecret, string? WebhookSecret);

    /// <summary>Whether COD / online payment are currently offered at checkout.</summary>
    [HttpGet("methods")]
    public async Task<IActionResult> GetMethods()
    {
        if (!await HasPermissionAsync(Resource)) return AdminUnauthorized();

        var settings = await _paymentSettings.GetSettingsAsync();
        return Ok(new
        {
            success = true,
            data = new
            {
                codEnabled = settings.CodEnabled,
                onlineEnabled = settings.OnlineEnabled,
                razorpayKeysConfigured = settings.RazorpayKeysConfigured,
            }
        });
    }

    /// <summary>Turn COD / online payment on or off. Rejects enabling online
    /// payment before Razorpay keys have actually been saved.</summary>
    [HttpPatch("methods")]
    public async Task<IActionResult> UpdateMethods([FromBody] MethodsRequest req)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        if (req.OnlineEnabled)
        {
            var settings = await _paymentSettings.GetSettingsAsync();
            if (!settings.RazorpayKeysConfigured)
                return BadRequest(new { success = false, message = "Save Razorpay Key ID and Key Secret before enabling online payment." });
        }

        await _paymentSettings.SetMethodsEnabledAsync(req.CodEnabled, req.OnlineEnabled);
        return Ok(new { success = true, message = "Payment methods updated." });
    }

    /// <summary>Current Razorpay credential status — never returns the
    /// actual secret values, only whether they're set.</summary>
    [HttpGet("razorpay-keys")]
    public async Task<IActionResult> GetRazorpayKeys()
    {
        if (!await HasPermissionAsync(Resource)) return AdminUnauthorized();

        var settings = await _paymentSettings.GetSettingsAsync();
        return Ok(new
        {
            success = true,
            data = new
            {
                keyId = settings.RazorpayKeyId ?? "",
                hasSecret = !string.IsNullOrWhiteSpace(settings.RazorpayKeySecret),
                hasWebhookSecret = !string.IsNullOrWhiteSpace(settings.RazorpayWebhookSecret),
            }
        });
    }

    /// <summary>Save Razorpay credentials. Blank/omitted secret fields leave
    /// the previously stored value untouched — the admin can update just the
    /// key id without re-pasting secrets that are never sent back to them.</summary>
    [HttpPatch("razorpay-keys")]
    public async Task<IActionResult> UpdateRazorpayKeys([FromBody] RazorpayKeysRequest req)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        await _paymentSettings.SetRazorpayKeysAsync(req.KeyId, req.KeySecret, req.WebhookSecret);
        return Ok(new { success = true, message = "Razorpay credentials updated." });
    }
}
