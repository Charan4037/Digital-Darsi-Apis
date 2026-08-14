using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Customer;

// A persisted record of a push notification, powering the in-app
// notifications inbox. Written alongside (not instead of) the actual FCM
// send in NotificationService, so the in-app history exists independent of
// whether the push itself was ever delivered (no device token, app
// uninstalled, etc.) — the same way "My Orders" doesn't depend on whether
// an order-confirmation email actually reached the inbox.
//
// Named "NotificationRecord", not "Notification" — NotificationService.cs
// already has `using FirebaseAdmin.Messaging;`, whose own `Notification`
// type (the FCM payload) is used throughout that file; reusing the same
// name here would make every `new Notification { Title = ... }` call
// ambiguous.
//
// CustomerId is nullable: NULL means a broadcast (e.g. AdminNotificationController
// .Broadcast, a topic push) meant for every logged-in customer, not one
// specific account — see ShopNotificationController, which reads
// `customer_id = @me OR customer_id IS NULL`.
//
// Table is "notification_records", not "notifications" — Bagisto's own
// admin panel already owns a `notifications` table (id/type/read/order_id)
// for its native PHP admin order-notification feature, unrelated to and
// incompatible with this schema. Same collision class as `roles` vs
// `admin_roles` (see Models/Rbac/Role.cs) — CREATE TABLE IF NOT EXISTS
// would silently no-op against it and every query below would fail at
// runtime with "Unknown column" errors.
[Table("notification_records")]
public class NotificationRecord
{
    [Key, Column("id")]
    public long Id { get; set; }

    [Column("customer_id")]
    public int? CustomerId { get; set; }

    [Column("title")]
    public string Title { get; set; } = "";

    [Column("body")]
    public string Body { get; set; } = "";

    // Mirrors the `type` key already sent in FCM data payloads (e.g.
    // "order.placed") — lets the client decide how to react to a tap
    // without parsing DataJson for every list row.
    [Column("type")]
    public string? Type { get; set; }

    // Raw FCM `data` dictionary, JSON-encoded — e.g. {"orderId":"123",...}.
    [Column("data_json")]
    public string? DataJson { get; set; }

    // Same image shown in the push itself (FCM Notification.ImageUrl) —
    // admin broadcasts/per-customer sends can attach one; automatic
    // transactional sends (order-placed) currently don't.
    [Column("image_url")]
    public string? ImageUrl { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    // Read state is only meaningful for personal rows (CustomerId set) —
    // a broadcast row (CustomerId NULL) is shared across every customer's
    // inbox query, so a single is_read flag on it can't represent "read by
    // this customer" without incorrectly marking it read/unread for
    // everyone else too. See ShopNotificationController's unread-count and
    // mark-read endpoints, which filter to CustomerId == the caller and
    // leave broadcast rows out of the read/unread accounting entirely.
    [Column("is_read")]
    public bool IsRead { get; set; }

    [Column("read_at")]
    public DateTime? ReadAt { get; set; }

    public Customer? Customer { get; set; }
}
