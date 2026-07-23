using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    public Customer? Customer { get; set; }
}
