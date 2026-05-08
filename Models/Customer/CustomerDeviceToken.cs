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

    [Column("app_version")]
    public string? AppVersion { get; set; }

    [Column("last_seen_at")]
    public DateTime? LastSeenAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }

    public Customer? Customer { get; set; }
}
