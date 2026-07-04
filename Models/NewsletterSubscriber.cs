using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models;

[Table("subscribers_list")]
public class NewsletterSubscriber
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("email")] public string Email { get; set; } = "";
    [Column("is_subscribed")] public bool IsSubscribed { get; set; }
    [Column("customer_id")] public int? CustomerId { get; set; }
    [Column("channel_id")] public int ChannelId { get; set; }
    [Column("token")] public string? Token { get; set; }
    [Column("created_at")] public DateTime? CreatedAt { get; set; }
    [Column("updated_at")] public DateTime? UpdatedAt { get; set; }
}
