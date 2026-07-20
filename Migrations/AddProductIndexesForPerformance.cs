using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DOSApi.Migrations
{
    /// <inheritdoc />
    public partial class AddProductIndexesForPerformance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ─── ProductFlats Indexes (Search & Filter) ───────────────────
            migrationBuilder.CreateIndex(
                name: "IX_product_flats_name",
                table: "product_flats",
                column: "name",
                filter: "name IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_product_flats_price",
                table: "product_flats",
                column: "price");

            migrationBuilder.CreateIndex(
                name: "IX_product_flats_url_key",
                table: "product_flats",
                column: "url_key",
                filter: "url_key IS NOT NULL");

            // Composite index for locale-based queries
            migrationBuilder.CreateIndex(
                name: "IX_product_flats_locale_product_id",
                table: "product_flats",
                columns: new[] { "locale", "product_id" });

            // Composite index for common filter combinations
            migrationBuilder.CreateIndex(
                name: "IX_product_flats_locale_name_price",
                table: "product_flats",
                columns: new[] { "locale", "name", "price" });

            // ─── Products Indexes (Filtering & Sorting) ────────────────────
            migrationBuilder.CreateIndex(
                name: "IX_products_parent_id",
                table: "products",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_products_parent_id_created_at",
                table: "products",
                columns: new[] { "parent_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_products_created_at",
                table: "products",
                column: "created_at");

            // ─── Category Relationship Indexes ────────────────────────────
            migrationBuilder.CreateIndex(
                name: "IX_product_category_product_id",
                table: "product_category",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_category_category_id",
                table: "product_category",
                column: "category_id");

            // ─── Inventory Indexes ───────────────────────────────────────
            migrationBuilder.CreateIndex(
                name: "IX_product_inventory_product_id",
                table: "product_inventory",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_inventory_qty",
                table: "product_inventory",
                column: "qty");

            // ─── Attribute Value Indexes ────────────────────────────────
            migrationBuilder.CreateIndex(
                name: "IX_product_attribute_values_attribute_id",
                table: "product_attribute_values",
                column: "attribute_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_attribute_values_product_id",
                table: "product_attribute_values",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_attribute_values_locale",
                table: "product_attribute_values",
                column: "locale");

            // Composite index for attribute queries
            migrationBuilder.CreateIndex(
                name: "IX_product_attribute_values_product_attribute_locale",
                table: "product_attribute_values",
                columns: new[] { "product_id", "attribute_id", "locale" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_product_flats_name", table: "product_flats");
            migrationBuilder.DropIndex(name: "IX_product_flats_price", table: "product_flats");
            migrationBuilder.DropIndex(name: "IX_product_flats_url_key", table: "product_flats");
            migrationBuilder.DropIndex(name: "IX_product_flats_locale_product_id", table: "product_flats");
            migrationBuilder.DropIndex(name: "IX_product_flats_locale_name_price", table: "product_flats");

            migrationBuilder.DropIndex(name: "IX_products_parent_id", table: "products");
            migrationBuilder.DropIndex(name: "IX_products_parent_id_created_at", table: "products");
            migrationBuilder.DropIndex(name: "IX_products_created_at", table: "products");

            migrationBuilder.DropIndex(name: "IX_product_category_product_id", table: "product_category");
            migrationBuilder.DropIndex(name: "IX_product_category_category_id", table: "product_category");

            migrationBuilder.DropIndex(name: "IX_product_inventory_product_id", table: "product_inventory");
            migrationBuilder.DropIndex(name: "IX_product_inventory_qty", table: "product_inventory");

            migrationBuilder.DropIndex(name: "IX_product_attribute_values_attribute_id", table: "product_attribute_values");
            migrationBuilder.DropIndex(name: "IX_product_attribute_values_product_id", table: "product_attribute_values");
            migrationBuilder.DropIndex(name: "IX_product_attribute_values_locale", table: "product_attribute_values");
            migrationBuilder.DropIndex(name: "IX_product_attribute_values_product_attribute_locale", table: "product_attribute_values");
        }
    }
}