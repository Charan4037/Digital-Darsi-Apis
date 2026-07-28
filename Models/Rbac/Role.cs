using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Rbac;

// A staff role (Super Admin / Admin / Data Evaluator / any custom role an
// admin creates later). Access is entirely driven by the RolePermission
// grants attached to it — see AdminBaseController.HasPermissionAsync.
// Named admin_roles, not roles — Bagisto's own admin panel already owns a
// `roles` table (id/name/description/permission_type/permissions-JSON) for
// its native PHP admin auth, unrelated to and incompatible with this schema.
[Table("admin_roles")]
public class Role
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("slug")]
    public string Slug { get; set; } = "";

    [Column("description")]
    public string? Description { get; set; }

    // The Super Admin role: cannot be deleted or renamed, and its grants are
    // re-asserted to full access on every boot so nobody can lock everyone
    // out by misconfiguring it.
    [Column("is_system")]
    public bool IsSystem { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}
