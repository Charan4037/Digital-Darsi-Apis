using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Catalog;

// Real marketplace seller identity. Backfilled from the free-text
// `vendor_en`/`vendor_te` name in each product's `additional` JSON (see
// ProductService.ExtractVendorName) — there is no FK from `products` to
// this table; matching happens by name. Purely additive satellite table.
[Table("vendors")]
public class Vendor
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("active")]
    public bool Active { get; set; } = true;

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
