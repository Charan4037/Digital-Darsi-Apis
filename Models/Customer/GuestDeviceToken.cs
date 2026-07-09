using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Customer;

/// <summary>
/// FCM device token for a guest (unauthenticated) session. Keyed by the
/// X-Session-Token header value that the Flutter app sends on every request
/// during a guest checkout flow. Rows are short-lived — they expire after
/// 7 days and are cleaned up on lookup, so the table stays small.
/// </summary>
[Table("guest_device_tokens")]
public class GuestDeviceToken
{
    [Key, Column("id")]
    public long Id { get; set; }

    /// <summary>
    /// The cart / session token sent in X-Session-Token. Uniquely identifies
    /// the guest device for the duration of one checkout session.
    /// </summary>
    [Column("session_token")]
    public string SessionToken { get; set; } = "";

    [Column("fcm_token")]
    public string FcmToken { get; set; } = "";

    [Column("platform")]
    public string? Platform { get; set; }

    [Column("device_id")]
    public string? DeviceId { get; set; }

    [Column("app_version")]
    public string? AppVersion { get; set; }

    [Column("device_model")]
    public string? DeviceModel { get; set; }

    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
