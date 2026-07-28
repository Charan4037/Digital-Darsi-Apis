using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DOSApi.Models.Rbac;

namespace DOSApi.Models.Customer;

// Marks a `customers` row as a staff/admin account. Purely additive satellite
// table — does not touch the shared `customers` table, which is also
// read/written by the Bagisto PHP/Laravel side.
[Table("customer_admins")]
public class CustomerAdmin
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("customer_id")]
    public int CustomerId { get; set; }

    // Nullable because pre-existing rows predate RBAC — RbacSeeder backfills
    // them all to Super Admin on first boot after this column is added.
    [Column("role_id")]
    public int? RoleId { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    public Customer? Customer { get; set; }
    public Role? Role { get; set; }
}
