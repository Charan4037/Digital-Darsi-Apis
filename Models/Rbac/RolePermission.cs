using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Rbac;

// The actual configurable matrix: one row per (role, permission) grant.
// A missing row for a given role/permission pair means implicit deny —
// new permissions can be added later without needing to backfill every role.
[Table("admin_role_permissions")]
public class RolePermission
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("role_id")]
    public int RoleId { get; set; }

    [Column("permission_id")]
    public int PermissionId { get; set; }

    [Column("can_read")]
    public bool CanRead { get; set; }

    [Column("can_write")]
    public bool CanWrite { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Role? Role { get; set; }
    public Permission? Permission { get; set; }
}
