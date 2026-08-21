using System.Text.Json;
using DOSApi.Models.Cart;
using DOSApi.Services;

namespace DOSApi.Helpers;

/// <summary>
/// Transforms Cart/CartItem models into the exact DOS PHP CartResource JSON format.
/// All keys are snake_case (handled by the global JSON serializer).
/// </summary>
public static class CartResourceHelper
{
    /// <summary>
    /// Maps a Cart entity to the DOS CartResource shape.
    /// </summary>
    /// <param name="extraCharges">Admin-defined charges (Handling, Processing
    /// Fee, etc.) resolved against this cart's subtotal — see
    /// ExtraChargeService.ComputeAsync. Folded into the exposed grand_total;
    /// the stored cart.GrandTotal column stays merchandise-only (unaffected),
    /// same as shipping.</param>
    public static object ToCartResource(Cart cart, string baseUrl, (List<ExtraChargeLine> lines, decimal total)? extraCharges = null)
    {
        var subTotal = cart.SubTotal ?? 0m;
        var taxTotal = cart.TaxTotal ?? 0m;
        var discountAmount = cart.DiscountAmount ?? 0m;
        var extraChargeLines = extraCharges?.lines ?? new List<ExtraChargeLine>();
        var extraChargesTotal = extraCharges?.total ?? 0m;
        var grandTotal = (cart.GrandTotal ?? 0m) + extraChargesTotal;
        var shippingAmount = 0m;
        var shippingAmountInclTax = 0m;
        var subTotalInclTax = subTotal + taxTotal;

        // Derive billing/shipping addresses from the Addresses collection
        object? billingAddress = null;
        object? shippingAddress = null;
        if (cart.Addresses != null)
        {
            var billing = cart.Addresses.FirstOrDefault(a => a.AddressType == "cart_billing");
            if (billing != null)
                billingAddress = MapAddress(billing);

            var shipping = cart.Addresses.FirstOrDefault(a => a.AddressType == "cart_shipping");
            if (shipping != null)
                shippingAddress = MapAddress(shipping);
        }

        // Payment method from CartPayment navigation
        string? paymentMethod = cart.Payment?.Method;
        string? paymentMethodTitle = cart.Payment?.MethodTitle;

        // have_stockable_items: true when any item type is NOT "virtual" or "downloadable"
        var haveStockableItems = cart.Items.Any(i =>
            i.Type != "virtual" && i.Type != "downloadable");

        return new
        {
            id = cart.Id,
            is_guest = cart.IsGuest ?? true,
            customer_id = cart.CustomerId,
            items_count = cart.ItemsCount ?? 0,
            items_qty = cart.ItemsQty ?? 0m,
            applied_taxes = new { },
            tax_total = taxTotal,
            formatted_tax_total = FormatPrice(taxTotal),
            sub_total_incl_tax = subTotalInclTax,
            sub_total = subTotal,
            formatted_sub_total_incl_tax = FormatPrice(subTotalInclTax),
            formatted_sub_total = FormatPrice(subTotal),
            coupon_code = cart.CouponCode,
            discount_amount = discountAmount,
            formatted_discount_amount = FormatPrice(discountAmount),
            shipping_method = cart.ShippingMethod,
            shipping_amount = shippingAmount,
            formatted_shipping_amount = FormatPrice(shippingAmount),
            shipping_amount_incl_tax = shippingAmountInclTax,
            formatted_shipping_amount_incl_tax = FormatPrice(shippingAmountInclTax),
            extra_charges = extraChargeLines.Select(c => new
            {
                name = c.Name,
                charge_type = c.ChargeType,
                rate = c.Rate,
                amount = c.Amount,
                formatted_amount = FormatPrice(c.Amount)
            }).ToList(),
            extra_charges_total = extraChargesTotal,
            formatted_extra_charges_total = FormatPrice(extraChargesTotal),
            grand_total = grandTotal,
            formatted_grand_total = FormatPrice(grandTotal),
            items = cart.Items.Select(i => ToCartItemResource(i, baseUrl)).ToList(),
            billing_address = billingAddress,
            shipping_address = shippingAddress,
            have_stockable_items = haveStockableItems,
            payment_method = paymentMethod,
            payment_method_title = paymentMethodTitle
        };
    }

    /// <summary>
    /// Maps a CartItem entity to the DOS cart-item shape.
    /// </summary>
    public static object ToCartItemResource(CartItem item, string baseUrl)
    {
        var price = item.Price;
        var total = item.Total;
        var taxAmount = item.TaxAmount ?? 0m;
        var priceInclTax = price + (item.Quantity > 0 ? taxAmount / item.Quantity : 0m);
        var totalInclTax = total + taxAmount;
        var discountAmount = item.DiscountAmount;

        // Resolve product image path — fall back to parent product images for
        // variant (simple) products that carry no images of their own.
        string? imagePath =
            item.Product?.Images?.OrderBy(i => i.Position).FirstOrDefault()?.Path
            ?? item.Product?.Parent?.Images?.OrderBy(i => i.Position).FirstOrDefault()?.Path;

        // Resolve product url key
        string? urlKey = item.Product?.Flats?.FirstOrDefault()?.UrlKey;

        // Resolve min/max qty from this item's own product flat (variant-specific)
        var itemFlat = item.Product?.Flats?.FirstOrDefault(f => f.ProductId == item.ProductId);
        var minQty = itemFlat?.MinQty ?? 1;
        var maxQty = itemFlat?.MaxQty;

        // DOS stores configurable selections in the `additional` JSON column
        // (parent product_id, selected_configurable_option, and an `attributes`
        // map keyed by attribute_id). Surface both the parsed blob AND a flat
        // `attributes` array on the response so clients can render the chosen
        // SKU/variant without having to fetch the product detail again.
        var (additional, attributes) = ParseAdditional(item.Additional);

        return new
        {
            id = item.Id,
            sku = item.Sku,
            product_id = item.ProductId,
            parent_id = item.ParentId,
            quantity = item.Quantity,
            type = item.Type,
            name = item.Name,
            price = price,
            base_price = item.BasePrice,
            formatted_price = FormatPrice(price),
            price_incl_tax = priceInclTax,
            formatted_price_incl_tax = FormatPrice(priceInclTax),
            total = total,
            formatted_total = FormatPrice(total),
            total_incl_tax = totalInclTax,
            formatted_total_incl_tax = FormatPrice(totalInclTax),
            discount_amount = discountAmount,
            formatted_discount_amount = FormatPrice(discountAmount),
            base_image = ImageHelper.ProductImage(imagePath, baseUrl, item.ProductId),
            product_url_key = urlKey,
            additional = additional,
            attributes = attributes,
            options = Array.Empty<object>(),
            min_qty = minQty,
            max_qty = maxQty
        };
    }

    /// <summary>
    /// Parses the `cart_items.additional` JSON blob into:
    ///   - the original object (returned as-is so clients can read everything)
    ///   - a flat `attributes` array of {attribute_id, attribute_code,
    ///     attribute_name, option_id, option_label} matching DOS's
    ///     attribute resource shape.
    /// DOS stores the selected variant attributes under
    /// `additional.attributes` keyed by attribute id; we flatten that to a
    /// list because keyed maps are awkward to iterate on the client.
    /// </summary>
    private static (object? additional, List<object> attributes) ParseAdditional(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, new List<object>());
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            root = doc.RootElement.Clone();
        }
        catch
        {
            // Defensive: an invalid blob shouldn't break the entire cart fetch
            // — return the raw string so debugging is still possible.
            return (raw, new List<object>());
        }

        var attributes = new List<object>();
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("attributes", out var attrEl))
        {
            // Two shapes seen in the wild:
            //   { "attributes": { "<id>": { attribute_id, option_label, ... } } }
            //   { "attributes": [ { attribute_id, option_label, ... }, ... ] }
            if (attrEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in attrEl.EnumerateObject())
                {
                    var item = MapAttributeEntry(kv.Value);
                    if (item != null) attributes.Add(item);
                }
            }
            else if (attrEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in attrEl.EnumerateArray())
                {
                    var item = MapAttributeEntry(el);
                    if (item != null) attributes.Add(item);
                }
            }
        }

        // Convert root JsonElement into a plain CLR object tree so the global
        // serializer treats it like any other anonymous payload.
        return (JsonElementToObject(root), attributes);
    }

    private static object? MapAttributeEntry(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        string? Read(string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
                ? v.ToString()
                : null;

        var attributeId = Read("attribute_id");
        var attributeCode = Read("attribute_code") ?? Read("code");
        var attributeName = Read("attribute_name") ?? Read("name") ?? Read("label");
        var optionId = Read("option_id");
        var optionLabel = Read("option_label") ?? Read("value") ?? Read("display_value");

        if (attributeName == null && optionLabel == null) return null;

        return new
        {
            attribute_id = attributeId,
            attribute_code = attributeCode,
            attribute_name = attributeName,
            option_id = optionId,
            option_label = optionLabel
        };
    }

    private static object? JsonElementToObject(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>();
                foreach (var kv in el.EnumerateObject())
                {
                    dict[kv.Name] = JsonElementToObject(kv.Value);
                }
                return dict;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in el.EnumerateArray())
                {
                    list.Add(JsonElementToObject(item));
                }
                return list;
            case JsonValueKind.String:
                return el.GetString();
            case JsonValueKind.Number:
                return el.TryGetInt64(out var l) ? l : el.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                return null;
        }
    }

    /// <summary>
    /// Formats a decimal value for display — see PriceFormatter.Format for
    /// the exact rule (no padded/rounded-away decimals).
    /// </summary>
    public static string FormatPrice(decimal value)
    {
        return PriceFormatter.Format(value);
    }

    private static object MapAddress(DOSApi.Models.Customer.Address addr)
    {
        return new
        {
            id = addr.Id,
            address_type = addr.AddressType,
            first_name = addr.FirstName,
            last_name = addr.LastName,
            email = addr.Email,
            company_name = addr.CompanyName,
            address = new[] { addr.AddressLine },
            city = addr.City,
            state = addr.State,
            postcode = addr.Postcode,
            country = addr.Country,
            phone = addr.Phone
        };
    }
}
