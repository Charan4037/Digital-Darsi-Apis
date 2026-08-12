using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Customer;

/// <summary>
/// FCM device-token registry for staff (Admin / Super Admin) devices —
/// mirrors <see cref="CustomerDeviceToken"/> but keyed to a
/// <see cref="CustomerAdmin"/> instead of a plain shopper. The admin-side
/// notification fan-out (NotificationService.SendToAdminsAsync) reads this
/// table to know which devices to push order-received alerts to.
/// </summary>
[Table("admin_device_tokens")]
public class AdminDeviceToken
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

    [Column("device_id")]
    public string? DeviceId { get; set; }

    [Column("app_version")]
    public string? AppVersion { get; set; }

    [Column("build_number")]
    public string? BuildNumber { get; set; }

    [Column("device_model")]
    public string? DeviceModel { get; set; }

    [Column("manufacturer")]
    public string? Manufacturer { get; set; }

    [Column("os_version")]
    public string? OsVersion { get; set; }

    [Column("locale")]
    public string? Locale { get; set; }

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
