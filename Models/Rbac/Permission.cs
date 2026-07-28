using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Rbac;

// Master feature catalog — developer-owned (seeded/code-defined), not
// user-editable. Admins configure grants against these rows via roles; they
// don't add/remove rows themselves. See RbacSeeder for the full list.
[Table("admin_permissions")]
public class Permission
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("feature_key")]
    public string FeatureKey { get; set; } = "";

    [Column("label")]
    public string Label { get; set; } = "";

    [Column("section")]
    public string Section { get; set; } = "";

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }
}
