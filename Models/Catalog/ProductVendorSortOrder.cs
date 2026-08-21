using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Catalog;

// Per-product display order *within* a single vendor's own block of
// products — distinct from Vendor.SortOrder, which ranks whole vendor
// blocks against each other. One row per product that an admin has given
// an explicit rank via AdminVendorsController.ReorderProducts; products
// with no row here keep the default newest-first order within their
// vendor's block (see CategoryController.QueryCategoryProductsAsync).
// Purely additive satellite table, same convention as Vendor.cs.
[Table("product_vendor_sort_orders")]
public class ProductVendorSortOrder
{
    [Key, Column("product_id")]
    public int ProductId { get; set; }

    [Column("vendor_id")]
    public int VendorId { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
