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

    [Column("name_te")]
    public string? NameTe { get; set; }

    [Column("phone")]
    public string? Phone { get; set; }

    [Column("address")]
    public string? Address { get; set; }

    [Column("active")]
    public bool Active { get; set; } = true;

    // Home-page priority: vendors with a lower (non-zero) SortOrder have
    // their products surfaced before everyone else's on category/home
    // listings (see CategoryController.QueryCategoryProductsAsync).
    // 0 = no explicit priority.
    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
