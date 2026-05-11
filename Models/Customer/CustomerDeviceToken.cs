using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BagistoApi.Models.Customer;

/// <summary>
/// FCM device-token registry. One row per (customer, fcm_token) — the same
/// customer may have multiple devices (phone + tablet), and a single device
/// rotates its FCM token when the app reinstalls or clears storage. The
/// notification fan-out reads this table to know who to push to.
/// </summary>
[Table("customer_device_tokens")]
public class CustomerDeviceToken
{
    [Key, Column("id")]
    public long Id { get; set; }

    [Column("customer_id")]
    public int CustomerId { get; set; }

    [Column("fcm_token")]
    public string FcmToken { get; set; } = "";

    /// <summary>"android", "ios", or "web".</summary>
    [Column("platform")]
    public string? Platform { get; set; }

    /// <summary>Stable per-install device id (Android: ANDROID_ID-derived; iOS: identifierForVendor).</summary>
    [Column("device_id")]
    public string? DeviceId { get; set; }

    /// <summary>app version name, e.g. "1.0.0".</summary>
    [Column("app_version")]
    public string? AppVersion { get; set; }

    /// <summary>app build number, e.g. "1" — separate from semver so we can spot per-build regressions.</summary>
    [Column("build_number")]
    public string? BuildNumber { get; set; }

    /// <summary>marketing model name, e.g. "Redmi Note 5 Pro" or "iPhone14,2".</summary>
    [Column("device_model")]
    public string? DeviceModel { get; set; }

    /// <summary>e.g. "Xiaomi", "samsung", "Apple".</summary>
    [Column("manufacturer")]
    public string? Manufacturer { get; set; }

    /// <summary>e.g. "Android 9" / "iOS 17.2".</summary>
    [Column("os_version")]
    public string? OsVersion { get; set; }

    /// <summary>IETF locale, e.g. "en_IN".</summary>
    [Column("locale")]
    public string? Locale { get; set; }

    /// <summary>IANA timezone, e.g. "Asia/Kolkata".</summary>
    [Column("timezone")]
    public string? Timezone { get; set; }

    [Column("last_seen_at")]
    public DateTime? LastSeenAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }

    public Customer? Customer { get; set; }
}
