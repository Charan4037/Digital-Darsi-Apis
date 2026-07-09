using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Customer;

[Table("customer_refresh_tokens")]
public class CustomerRefreshToken
{
    [Key, Column("id")]
    public long Id { get; set; }

    [Column("customer_id")]
    public int CustomerId { get; set; }

    // SHA-256 hash of the opaque refresh-token string. We never store the
    // plaintext token — if the table is ever exfiltrated, the hashes can't be
    // replayed against the API.
    [Column("token_hash")]
    public string TokenHash { get; set; } = "";

    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    // When a refresh token is rotated, this points at the hash of the token
    // that replaced it. Lets us invalidate the whole rotation chain if we
    // ever detect a stolen-token replay.
    [Column("replaced_by_hash")]
    public string? ReplacedByHash { get; set; }

    [Column("created_ip")]
    public string? CreatedIp { get; set; }

    [Column("user_agent")]
    public string? UserAgent { get; set; }

    public Customer? Customer { get; set; }

    public bool IsActive => RevokedAt == null && DateTime.UtcNow < ExpiresAt;
}
