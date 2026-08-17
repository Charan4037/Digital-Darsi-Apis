using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models;

namespace DOSApi.Services;

/// <summary>
/// Admin-configurable payment settings — whether COD/online payment are
/// offered at all, and the Razorpay credentials used to actually process
/// online payments. Stored as `core_config` rows, same convention as
/// AccountService.RefundWindowDaysConfigKey / ShopCheckoutSettingsController's
/// minimum-order-value setting — no dedicated table needed for a handful of
/// scalar settings.
/// </summary>
public class PaymentSettingsService
{
    public const string CodEnabledKey = "payment.cod.enabled";
    public const string OnlineEnabledKey = "payment.online.enabled";
    public const string RazorpayKeyIdKey = "payment.razorpay.key_id";
    public const string RazorpayKeySecretKey = "payment.razorpay.key_secret";
    public const string RazorpayWebhookSecretKey = "payment.razorpay.webhook_secret";

    // COD defaults on (matches pre-existing behavior); online defaults off —
    // it has no effect until an admin actually saves Razorpay keys, so
    // there's no risk in defaulting it off rather than silently activating a
    // gateway nobody configured yet.
    private const bool DefaultCodEnabled = true;
    private const bool DefaultOnlineEnabled = false;

    private readonly DOSDbContext _db;

    public PaymentSettingsService(DOSDbContext db)
    {
        _db = db;
    }

    public class PaymentSettings
    {
        public bool CodEnabled { get; set; }
        public bool OnlineEnabled { get; set; }
        public string? RazorpayKeyId { get; set; }
        public string? RazorpayKeySecret { get; set; }
        public string? RazorpayWebhookSecret { get; set; }

        public bool RazorpayKeysConfigured =>
            !string.IsNullOrWhiteSpace(RazorpayKeyId) && !string.IsNullOrWhiteSpace(RazorpayKeySecret);

        /// <summary>Online payment is only actually usable when the admin has
        /// both turned it on AND saved working Razorpay credentials.</summary>
        public bool OnlineUsable => OnlineEnabled && RazorpayKeysConfigured;
    }

    public async Task<PaymentSettings> GetSettingsAsync()
    {
        var keys = new[] { CodEnabledKey, OnlineEnabledKey, RazorpayKeyIdKey, RazorpayKeySecretKey, RazorpayWebhookSecretKey };
        var rows = await _db.CoreConfigs
            .AsNoTracking()
            .Where(c => keys.Contains(c.Code))
            .ToDictionaryAsync(c => c.Code, c => c.Value);

        return new PaymentSettings
        {
            CodEnabled = ParseBool(rows.GetValueOrDefault(CodEnabledKey), DefaultCodEnabled),
            OnlineEnabled = ParseBool(rows.GetValueOrDefault(OnlineEnabledKey), DefaultOnlineEnabled),
            RazorpayKeyId = rows.GetValueOrDefault(RazorpayKeyIdKey),
            RazorpayKeySecret = rows.GetValueOrDefault(RazorpayKeySecretKey),
            RazorpayWebhookSecret = rows.GetValueOrDefault(RazorpayWebhookSecretKey),
        };
    }

    public async Task SetMethodsEnabledAsync(bool codEnabled, bool onlineEnabled)
    {
        // Sequential, not Task.WhenAll — both upserts share one DbContext,
        // which isn't safe for concurrent operations.
        await UpsertAsync(CodEnabledKey, codEnabled.ToString());
        await UpsertAsync(OnlineEnabledKey, onlineEnabled.ToString());
    }

    /// <summary>Null/blank secret fields leave the previously stored value
    /// untouched — lets an admin update just the key id without having to
    /// re-paste secrets that are never sent back to the client.</summary>
    public async Task SetRazorpayKeysAsync(string? keyId, string? keySecret, string? webhookSecret)
    {
        if (keyId != null) await UpsertAsync(RazorpayKeyIdKey, keyId.Trim());
        if (!string.IsNullOrWhiteSpace(keySecret)) await UpsertAsync(RazorpayKeySecretKey, keySecret.Trim());
        if (!string.IsNullOrWhiteSpace(webhookSecret)) await UpsertAsync(RazorpayWebhookSecretKey, webhookSecret.Trim());
    }

    private static bool ParseBool(string? value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;

    private async Task UpsertAsync(string code, string value)
    {
        var now = DateTime.UtcNow;
        var row = await _db.CoreConfigs.FirstOrDefaultAsync(c => c.Code == code);
        if (row == null)
        {
            _db.CoreConfigs.Add(new CoreConfig { Code = code, Value = value, CreatedAt = now, UpdatedAt = now });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = now;
        }
        await _db.SaveChangesAsync();
    }
}
