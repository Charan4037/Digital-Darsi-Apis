using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Models.Cart;
using BagistoApi.Models.Catalog;

namespace BagistoApi.Services;

public class CartService
{
    private readonly BagistoDbContext _db;
    private readonly ProductService _productService;
    private readonly AuthService _authService;
    private readonly string _baseUrl;
    private readonly byte[] _cartTokenKey;
    private readonly TimeSpan _cartTokenLifetime;

    public CartService(BagistoDbContext db, ProductService productService, AuthService authService, IConfiguration config)
    {
        _db = db;
        _productService = productService;
        _authService = authService;
        _baseUrl = config["App:BaseUrl"] ?? "http://192.168.0.116:8000";

        // Derive a dedicated HMAC key for cart tokens from the JWT signing
        // key so we don't need a second secret. Domain-separated via prefix
        // so a leaked cart token can't be replayed as anything else.
        var jwtKey = config["Jwt:Key"]
            ?? throw new InvalidOperationException("Jwt:Key must be configured.");
        _cartTokenKey = SHA256.HashData(Encoding.UTF8.GetBytes("cart-token-v1:" + jwtKey));

        var days = int.TryParse(config["Cart:TokenDays"], out var d) && d > 0 ? d : 30;
        _cartTokenLifetime = TimeSpan.FromDays(days);
    }

    public async Task<(Cart cart, string sessionToken, bool success, string message)> CreateCartAsync(int? customerId)
    {
        var cart = new Cart
        {
            IsGuest = customerId == null,
            CustomerId = customerId,
            ChannelId = 1,
            IsActive = true,
            ItemsCount = 0,
            ItemsQty = 0,
            SubTotal = 0,
            GrandTotal = 0,
            TaxTotal = 0,
            DiscountAmount = 0,
            BaseCurrencyCode = "INR",
            ChannelCurrencyCode = "INR",
            CartCurrencyCode = "INR",
            GlobalCurrencyCode = "INR",
            ExchangeRate = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Carts.Add(cart);
        await _db.SaveChangesAsync();

        var sessionToken = IssueGuestCartToken(cart.Id);
        return (cart, sessionToken, true, "Cart created successfully.");
    }

    public async Task<Cart?> GetCartAsync(int? customerId, string? sessionToken)
    {
        Cart? cart = null;

        if (customerId.HasValue)
        {
            cart = await _db.Carts
                .Include(c => c.Items).ThenInclude(i => i.Product).ThenInclude(p => p!.Images)
                .Include(c => c.Items).ThenInclude(i => i.Product).ThenInclude(p => p!.Flats)
                .Include(c => c.Items).ThenInclude(i => i.Product).ThenInclude(p => p!.Parent).ThenInclude(p => p!.Images)
                .Where(c => c.CustomerId == customerId && c.IsActive == true)
                .OrderByDescending(c => c.Id)
                .FirstOrDefaultAsync();
        }

        if (cart == null && !string.IsNullOrEmpty(sessionToken))
        {
            // Verify the token's HMAC and recover the cart id. We don't fall
            // back to "treat the token as a numeric id" — that allowed an
            // attacker to fetch any guest cart by guessing a small integer.
            var resolvedId = TryReadGuestCartToken(sessionToken);

            if (resolvedId.HasValue)
            {
                cart = await _db.Carts
                    .Include(c => c.Items).ThenInclude(i => i.Product).ThenInclude(p => p!.Images)
                    .Include(c => c.Items).ThenInclude(i => i.Product).ThenInclude(p => p!.Flats)
                    .Include(c => c.Items).ThenInclude(i => i.Product).ThenInclude(p => p!.Parent).ThenInclude(p => p!.Images)
                    .Where(c => c.Id == resolvedId.Value
                                && c.IsActive == true
                                && c.CustomerId == null)
                    .FirstOrDefaultAsync();
            }
        }

        return cart;
    }

    /// <summary>
    /// Issue a signed, opaque cart token of the form
    /// <c>base64url(cartId|expiryUnix).base64url(hmac)</c>. Stateless — the
    /// server doesn't persist anything beyond the cart row itself, and the
    /// HMAC stops a guest from forging a token for someone else's cart.
    /// </summary>
    public string IssueGuestCartToken(int cartId)
    {
        var expiry = DateTimeOffset.UtcNow.Add(_cartTokenLifetime).ToUnixTimeSeconds();
        var payload = $"{cartId}|{expiry}";
        var sig = ComputeCartTokenHmac(payload);
        return $"{ToBase64Url(Encoding.UTF8.GetBytes(payload))}.{ToBase64Url(sig)}";
    }

    private int? TryReadGuestCartToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1) return null;

        byte[] payloadBytes;
        byte[] sigBytes;
        try
        {
            payloadBytes = FromBase64Url(token[..dot]);
            sigBytes = FromBase64Url(token[(dot + 1)..]);
        }
        catch (FormatException) { return null; }

        var expectedSig = ComputeCartTokenHmac(Encoding.UTF8.GetString(payloadBytes));
        if (!CryptographicOperations.FixedTimeEquals(sigBytes, expectedSig))
            return null;

        var parts = Encoding.UTF8.GetString(payloadBytes).Split('|');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var cartId) || cartId <= 0) return null;
        if (!long.TryParse(parts[1], out var expiry)) return null;
        if (DateTimeOffset.FromUnixTimeSeconds(expiry) <= DateTimeOffset.UtcNow) return null;

        return cartId;
    }

    private byte[] ComputeCartTokenHmac(string payload)
    {
        using var h = new HMACSHA256(_cartTokenKey);
        return h.ComputeHash(Encoding.UTF8.GetBytes(payload));
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] FromBase64Url(string s)
    {
        var p = s.Replace('-', '+').Replace('_', '/');
        switch (p.Length % 4) { case 2: p += "=="; break; case 3: p += "="; break; }
        return Convert.FromBase64String(p);
    }

    public async Task<(Cart? cart, bool success, string message)> AddToCartAsync(Cart cart, int productId, int quantity)
    {
        await _productService.EnsureAttrIdsAsync();

        var product = await _db.Products
            .Include(p => p.Flats)
            .Include(p => p.Images)
            .Include(p => p.Inventories)
            .Include(p => p.AttributeValues)
            .Include(p => p.Parent).ThenInclude(p => p!.Images)
            .FirstOrDefaultAsync(p => p.Id == productId);

        if (product == null)
            return (cart, false, "Product not found.");

        // Resolve core fields: prefer product_flat row, fall back to product_attribute_values.
        // Upstream Bagisto data may not have flat rows populated for every product.
        var flat = _productService.GetFlat(product);

        var sku = !string.IsNullOrWhiteSpace(flat?.Sku) ? flat!.Sku : product.Sku;
        var name = !string.IsNullOrWhiteSpace(flat?.Name)
            ? flat!.Name!
            : (_productService.GetProductName(product) ?? product.Sku);
        var type = flat?.Type ?? product.Type ?? "simple";
        var weight = flat?.Weight ?? 0m;
        var price = flat != null
            ? _productService.GetEffectivePrice(flat)
            : _productService.GetEffectivePrice(product);

        if (price <= 0)
            return (cart, false, "Product price not available.");

        // Check existing item
        var existingItem = cart.Items.FirstOrDefault(i => i.ProductId == productId);
        if (existingItem != null)
        {
            existingItem.Quantity += quantity;
            existingItem.Total = existingItem.Price * existingItem.Quantity;
            existingItem.BaseTotal = existingItem.BasePrice * existingItem.Quantity;
            existingItem.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            // Fall back to parent product images for variants that have none.
            var imgPath =
                product.Images.OrderBy(i => i.Position).FirstOrDefault()?.Path
                ?? product.Parent?.Images.OrderBy(i => i.Position).FirstOrDefault()?.Path;
            var item = new CartItem
            {
                CartId = cart.Id,
                ProductId = productId,
                Sku = sku,
                Name = name,
                Type = type,
                Quantity = quantity,
                Price = price,
                BasePrice = price,
                Total = price * quantity,
                BaseTotal = price * quantity,
                Weight = weight,
                TotalWeight = weight * quantity,
                BaseTotalWeight = weight * quantity,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Additional = imgPath != null ? System.Text.Json.JsonSerializer.Serialize(new { product_image = imgPath }) : null
            };
            cart.Items.Add(item);
        }

        RecalculateTotals(cart);
        cart.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return (cart, true, "Product added to cart successfully.");
    }

    public async Task<(Cart? cart, bool success, string message)> UpdateCartItemAsync(Cart cart, int cartItemId, int quantity)
    {
        var item = cart.Items.FirstOrDefault(i => i.Id == cartItemId);
        if (item == null)
            return (cart, false, "Cart item not found.");

        if (quantity <= 0)
            return await RemoveCartItemAsync(cart, cartItemId);

        item.Quantity = quantity;
        item.Total = item.Price * quantity;
        item.BaseTotal = item.BasePrice * quantity;
        item.TotalWeight = item.Weight * quantity;
        item.UpdatedAt = DateTime.UtcNow;

        RecalculateTotals(cart);
        cart.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return (cart, true, "Cart updated successfully.");
    }

    public async Task<(Cart? cart, bool success, string message)> RemoveCartItemAsync(Cart cart, int cartItemId)
    {
        var item = cart.Items.FirstOrDefault(i => i.Id == cartItemId);
        if (item == null)
            return (cart, false, "Cart item not found.");

        _db.CartItems.Remove(item);
        cart.Items.Remove(item);

        RecalculateTotals(cart);
        cart.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return (cart, true, "Item removed from cart.");
    }

    public async Task<(Cart? cart, bool success, string message)> ApplyCouponAsync(Cart cart, string couponCode)
    {
        var coupon = await _db.CartRuleCoupons
            .Include(c => c.CartRule)
            .FirstOrDefaultAsync(c => c.Code == couponCode && (c.ExpiredAt == null || c.ExpiredAt > DateTime.UtcNow));

        if (coupon?.CartRule == null || !coupon.CartRule.Status)
            return (cart, false, "Invalid coupon code.");

        if (coupon.UsageLimit > 0 && coupon.TimesUsed >= coupon.UsageLimit)
            return (cart, false, "Coupon usage limit exceeded.");

        var rule = coupon.CartRule;
        decimal discount = 0;

        switch (rule.ActionType)
        {
            case "cart_fixed":
                discount = rule.DiscountAmount;
                break;
            case "cart_percent":
            case "percent_of_product_price_discount":
                discount = (cart.SubTotal ?? 0) * rule.DiscountAmount / 100;
                break;
            default:
                discount = rule.DiscountAmount;
                break;
        }

        cart.CouponCode = couponCode;
        cart.DiscountAmount = discount;
        cart.BaseDiscountAmount = discount;
        cart.GrandTotal = (cart.SubTotal ?? 0) + (cart.TaxTotal ?? 0) - discount;
        cart.BaseGrandTotal = cart.GrandTotal;
        cart.AppliedCartRuleIds = rule.Id.ToString();
        cart.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return (cart, true, "Coupon applied successfully.");
    }

    public async Task<(Cart? cart, bool success, string message)> RemoveCouponAsync(Cart cart)
    {
        cart.CouponCode = null;
        cart.DiscountAmount = 0;
        cart.BaseDiscountAmount = 0;
        cart.AppliedCartRuleIds = null;
        RecalculateTotals(cart);
        cart.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return (cart, true, "Coupon removed successfully.");
    }

    public async Task<(Cart? cart, bool success, string message, string? sessionToken)> MergeCartAsync(int guestCartId, int customerId)
    {
        var guestCart = await _db.Carts.Include(c => c.Items).FirstOrDefaultAsync(c => c.Id == guestCartId && c.IsActive == true);
        if (guestCart == null)
            return (null, false, "Guest cart not found.", null);

        var customerCart = await _db.Carts.Include(c => c.Items)
            .Where(c => c.CustomerId == customerId && c.IsActive == true)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync();

        if (customerCart == null)
        {
            guestCart.CustomerId = customerId;
            guestCart.IsGuest = false;
            guestCart.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return (guestCart, true, "Cart merged successfully.", null);
        }

        foreach (var guestItem in guestCart.Items)
        {
            var existing = customerCart.Items.FirstOrDefault(i => i.ProductId == guestItem.ProductId);
            if (existing != null)
            {
                existing.Quantity += guestItem.Quantity;
                existing.Total = existing.Price * existing.Quantity;
                existing.BaseTotal = existing.BasePrice * existing.Quantity;
            }
            else
            {
                guestItem.CartId = customerCart.Id;
                customerCart.Items.Add(guestItem);
            }
        }

        guestCart.IsActive = false;
        RecalculateTotals(customerCart);
        customerCart.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return (customerCart, true, "Cart merged successfully.", null);
    }

    public string? GetCartItemBaseImage(CartItem item)
    {
        var img = item.Product?.Images.OrderBy(i => i.Position).FirstOrDefault();
        if (img != null) return $"{_baseUrl}/storage/{img.Path}";
        return null;
    }

    public string? GetCartItemProductUrlKey(CartItem item)
    {
        return item.Product?.Flats.FirstOrDefault()?.UrlKey;
    }

    private void RecalculateTotals(Cart cart)
    {
        cart.ItemsCount = cart.Items.Count;
        cart.ItemsQty = cart.Items.Sum(i => i.Quantity);
        cart.SubTotal = cart.Items.Sum(i => i.Total);
        cart.BaseSubTotal = cart.SubTotal;
        cart.TaxTotal = cart.Items.Sum(i => i.TaxAmount ?? 0);
        cart.BaseTaxTotal = cart.TaxTotal;
        cart.GrandTotal = cart.SubTotal + cart.TaxTotal - (cart.DiscountAmount ?? 0);
        cart.BaseGrandTotal = cart.GrandTotal;
    }
}
