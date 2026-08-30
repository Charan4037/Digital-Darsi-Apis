using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models;

namespace DOSApi.Services;

/// <summary>
/// Admin-configurable app update policy, per platform (Android/iOS).
/// Stored as `core_config` rows, same convention as PaymentSettingsService —
/// no dedicated table needed for a handful of scalar settings.
///
/// Force vs. skip is derived from two thresholds rather than a separate
/// flag: an installed version below MinVersion is force-blocked (no
/// dismiss); at or above MinVersion but below LatestVersion gets a
/// dismissible "update available" nudge; at or above LatestVersion, nothing
/// is shown. Setting MinVersion == LatestVersion forces everyone onto the
/// latest immediately; leaving MinVersion low only forces genuinely broken
/// old builds. See RoadblockController-adjacent controllers for the
/// equivalent "core_config-backed settings" pattern used elsewhere.
/// </summary>
public class AppUpdateSettingsService
{
    public const string AndroidLatestVersionKey = "app_update.android.latest_version";
    public const string AndroidMinVersionKey    = "app_update.android.min_version";
    public const string AndroidStoreUrlKey      = "app_update.android.store_url";
    public const string AndroidMessageKey       = "app_update.android.message";
    public const string IosLatestVersionKey     = "app_update.ios.latest_version";
    public const string IosMinVersionKey        = "app_update.ios.min_version";
    public const string IosStoreUrlKey          = "app_update.ios.store_url";
    public const string IosMessageKey           = "app_update.ios.message";

    private static readonly string[] AllKeys =
    {
        AndroidLatestVersionKey, AndroidMinVersionKey, AndroidStoreUrlKey, AndroidMessageKey,
        IosLatestVersionKey, IosMinVersionKey, IosStoreUrlKey, IosMessageKey,
    };

    private readonly DOSDbContext _db;

    public AppUpdateSettingsService(DOSDbContext db)
    {
        _db = db;
    }

    public class PlatformSettings
    {
        public string LatestVersion { get; set; } = "";
        public string MinVersion { get; set; } = "";
        public string StoreUrl { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public class AppUpdateSettings
    {
        public PlatformSettings Android { get; set; } = new();
        public PlatformSettings Ios { get; set; } = new();
    }

    public async Task<AppUpdateSettings> GetSettingsAsync()
    {
        var rows = await _db.CoreConfigs
            .AsNoTracking()
            .Where(c => AllKeys.Contains(c.Code))
            .ToDictionaryAsync(c => c.Code, c => c.Value ?? "");

        string Get(string key) => rows.GetValueOrDefault(key, "");

        return new AppUpdateSettings
        {
            Android = new PlatformSettings
            {
                LatestVersion = Get(AndroidLatestVersionKey),
                MinVersion    = Get(AndroidMinVersionKey),
                StoreUrl      = Get(AndroidStoreUrlKey),
                Message       = Get(AndroidMessageKey),
            },
            Ios = new PlatformSettings
            {
                LatestVersion = Get(IosLatestVersionKey),
                MinVersion    = Get(IosMinVersionKey),
                StoreUrl      = Get(IosStoreUrlKey),
                Message       = Get(IosMessageKey),
            },
        };
    }

    public async Task SetAsync(PlatformSettings android, PlatformSettings ios)
    {
        // Sequential, not Task.WhenAll — all upserts share one DbContext,
        // which isn't safe for concurrent operations (same rule as
        // PaymentSettingsService.SetMethodsEnabledAsync).
        await UpsertAsync(AndroidLatestVersionKey, android.LatestVersion);
        await UpsertAsync(AndroidMinVersionKey, android.MinVersion);
        await UpsertAsync(AndroidStoreUrlKey, android.StoreUrl);
        await UpsertAsync(AndroidMessageKey, android.Message);
        await UpsertAsync(IosLatestVersionKey, ios.LatestVersion);
        await UpsertAsync(IosMinVersionKey, ios.MinVersion);
        await UpsertAsync(IosStoreUrlKey, ios.StoreUrl);
        await UpsertAsync(IosMessageKey, ios.Message);
    }

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
