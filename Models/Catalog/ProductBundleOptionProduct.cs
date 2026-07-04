using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Catalog;

[Table("product_bundle_option_products")]
public class ProductBundleOptionProduct
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("product_bundle_option_id")] public int ProductBundleOptionId { get; set; }
    [Column("product_id")] public int ProductId { get; set; }
    [Column("qty")] public int Qty { get; set; }
    [Column("is_user_defined")] public bool IsUserDefined { get; set; }
    [Column("sort_order")] public int SortOrder { get; set; }
    [Column("is_default")] public bool IsDefault { get; set; }

    public Product? Product { get; set; }
}
