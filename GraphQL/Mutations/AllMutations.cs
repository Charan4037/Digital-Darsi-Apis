using HotChocolate;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.GraphQL.Types;
using DOSApi.GraphQL.Queries;
using DOSApi.Models.Catalog;
using DOSApi.Models.Customer;
using DOSApi.Services;

namespace DOSApi.GraphQL.Mutations;

// ─── Input Types ────────────────────────────────────────────────────────

public record CustomerLoginInput(string Email, string Password);
public record CustomerRegisterInput(string FirstName, string LastName, string Email, string Password, string ConfirmPassword);
public record CreateCartInput(string? Dummy = null);
public record AddToCartInput(int? CartId, int ProductId, int Quantity);
public record ReadCartInput(string? Dummy = null);
public record UpdateCartItemInput(int CartItemId, int Quantity);
public record RemoveCartItemInput(int CartItemId);
public record ApplyCouponInput(string CouponCode);
public record RemoveCouponInput(string? Dummy = null);
public record MergeCartInput(int CartId);
public record CheckoutAddressInput(string FirstName, string LastName, string Address1, string City, string State, string Country, string Postcode, string Phone, string? Email, bool? UseForShipping, bool? DefaultAddress);
public record ShippingMethodInput(string Shipping_method);
public record PaymentMethodInput(string Payment_method);
// Razorpay returns paymentId + signature client-side after a successful
// charge. We accept both camelCase and snake_case spellings because the
// Flutter client sends both for compatibility.
public record PlaceOrderInput(
    string? RazorpayPaymentId = null,
    string? Razorpay_payment_id = null,
    string? RazorpayOrderId = null,
    string? Razorpay_order_id = null,
    string? RazorpaySignature = null,
    string? Razorpay_signature = null);
public record AddUpdateAddressInput(int? AddressId, string FirstName, string LastName, string Address1, string City, string State, string Country, string Postcode, string Phone, string? Email, bool? UseForShipping, bool? DefaultAddress);
public record DeleteAddressInput(string Id);
public record ProfileUpdateInput(string? FirstName, string? LastName, string? Phone, string? Gender, string? DateOfBirth, bool? SubscribedToNewsLetter, string? Email, string? CurrentPassword, string? NewPassword, string? ConfirmPassword);
public record ProfileDeleteInput(string Password);
public record CreateWishlistInput(int ProductId);
public record DeleteWishlistInput(string Id);
public record MoveWishlistToCartInput(int WishlistItemId, int? Quantity);
public record CreateCompareInput(int ProductId);
public record CreateReviewInput(int ProductId, string Title, string Comment, int Rating, string Name);
public record ReorderInput(int OrderId);
public record ContactUsInput(string Email, string Subject, string Message, string? Name);
public record RefreshTokenInput(string RefreshToken);
public record LogoutInput(string? RefreshToken);
public record FirebaseLoginInput(string IdToken, string? FirstName, string? LastName);

// ─── Auth Mutations ─────────────────────────────────────────────────────

[ExtendObjectType("Mutation")]
public class AuthMutations
{
    public async Task<CustomerLoginResult> CustomerLogin(
        [Service] AuthService auth, CustomerLoginInput input)
    {
        var result = await auth.LoginAsync(input.Email, input.Password);
        if (!result.Success || result.Customer == null || result.Tokens == null)
            return new CustomerLoginResult { Success = false, Message = result.Message };

        var t = result.Tokens;
        return new CustomerLoginResult
        {
            Id = result.Customer.Id,
            // Legacy aliases — same value as AccessToken.
            ApiToken = t.AccessToken,
            Token = t.AccessToken,
            AccessToken = t.AccessToken,
            AccessTokenExpiresAt = t.AccessTokenExpiresAt,
            RefreshToken = t.RefreshToken,
            RefreshTokenExpiresAt = t.RefreshTokenExpiresAt,
            Message = result.Message,
            Success = true
        };
    }

    public async Task<CustomerRegisterResult> Customer(
        [Service] AuthService auth, CustomerRegisterInput input)
    {
        var result = await auth.RegisterAsync(
            input.FirstName, input.LastName, input.Email, input.Password);

        if (!result.Success || result.Customer == null || result.Tokens == null)
            return new CustomerRegisterResult { Success = false, Message = result.Message };

        var c = result.Customer;
        var t = result.Tokens;
        return new CustomerRegisterResult
        {
            Id = c.Id,
            FirstName = c.FirstName,
            LastName = c.LastName,
            Email = c.Email,
            Status = c.Status == 1,
            ApiToken = t.AccessToken,
            Token = t.AccessToken,
            AccessToken = t.AccessToken,
            AccessTokenExpiresAt = t.AccessTokenExpiresAt,
            RefreshToken = t.RefreshToken,
            RefreshTokenExpiresAt = t.RefreshTokenExpiresAt,
            Name = $"{c.FirstName} {c.LastName}",
            Success = true,
            Message = result.Message
        };
    }

    public async Task<CustomerLoginResult> CustomerFirebaseLogin(
        [Service] AuthService auth, FirebaseLoginInput input)
    {
        var result = await auth.LoginWithFirebaseAsync(input.IdToken, input.FirstName, input.LastName);
        if (!result.Success || result.Customer == null || result.Tokens == null)
            return new CustomerLoginResult { Success = false, Message = result.Message };

        var t = result.Tokens;
        return new CustomerLoginResult
        {
            Id = result.Customer.Id,
            ApiToken = t.AccessToken,
            Token = t.AccessToken,
            AccessToken = t.AccessToken,
            AccessTokenExpiresAt = t.AccessTokenExpiresAt,
            RefreshToken = t.RefreshToken,
            RefreshTokenExpiresAt = t.RefreshTokenExpiresAt,
            Message = result.Message,
            Success = true,
        };
    }

    public async Task<RefreshTokenResult> RefreshToken(
        [Service] AuthService auth, RefreshTokenInput input)
    {
        var result = await auth.RefreshAsync(input.RefreshToken);
        if (!result.Success || result.Customer == null || result.Tokens == null)
            return new RefreshTokenResult { Success = false, Message = result.Message };

        var t = result.Tokens;
        return new RefreshTokenResult
        {
            Id = result.Customer.Id,
            AccessToken = t.AccessToken,
            AccessTokenExpiresAt = t.AccessTokenExpiresAt,
            RefreshToken = t.RefreshToken,
            RefreshTokenExpiresAt = t.RefreshTokenExpiresAt,
            Success = true,
            Message = result.Message
        };
    }

    public async Task<SimpleResult> ForgotPassword([Service] AuthService auth, string email)
    {
        var (message, success) = await auth.ForgotPasswordAsync(email);
        return new SimpleResult { Success = success, Message = message };
    }

    public async Task<SimpleResult> Logout([Service] AuthService auth, LogoutInput? input)
    {
        if (!string.IsNullOrWhiteSpace(input?.RefreshToken))
            await auth.RevokeRefreshTokenAsync(input.RefreshToken);

        return new SimpleResult { Success = true, Message = "Logged out successfully." };
    }
}

// ─── Cart Mutations ─────────────────────────────────────────────────────

[ExtendObjectType("Mutation")]
public class CartMutations
{
    public async Task<CartTokenResult> CartToken(
        [Service] CartService cartSvc, [Service] AuthService auth, CreateCartInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        var (cart, session, success, message) = await cartSvc.CreateCartAsync(cid);
        return new CartTokenResult
        {
            Id = cart.Id, CartToken = session, CustomerId = cid,
            Success = success, Message = message, SessionToken = session, IsGuest = cid == null
        };
    }

    public async Task<CartResult> AddProductInCart(
        [Service] CartService cartSvc, [Service] AuthService auth,
        [Service] IHttpContextAccessor http, AddToCartInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        var session = http.HttpContext?.Request.Headers["X-Session-Token"].FirstOrDefault();
        var cart = await cartSvc.GetCartAsync(cid, session);

        if (cart == null)
        {
            var (newCart, newSession, _, _) = await cartSvc.CreateCartAsync(cid);
            cart = newCart;
        }

        var (result, success, message) = await cartSvc.AddToCartAsync(cart, input.ProductId, input.Quantity);
        return MapCartResult(result, success, message, cartSvc);
    }

    public async Task<CartResult> ReadCart(
        [Service] CartService cartSvc, [Service] AuthService auth,
        [Service] IHttpContextAccessor http, ReadCartInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        var session = http.HttpContext?.Request.Headers["X-Session-Token"].FirstOrDefault();
        var cart = await cartSvc.GetCartAsync(cid, session);
        if (cart == null) return new CartResult { Message = "Cart not found." };
        return MapCartResult(cart, true, "Cart loaded.", cartSvc);
    }

    public async Task<CartResult> UpdateCartItem(
        [Service] CartService cartSvc, [Service] AuthService auth,
        [Service] IHttpContextAccessor http, UpdateCartItemInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        var session = http.HttpContext?.Request.Headers["X-Session-Token"].FirstOrDefault();
        var cart = await cartSvc.GetCartAsync(cid, session);
        if (cart == null) return new CartResult { Message = "Cart not found." };

        var (result, success, message) = await cartSvc.UpdateCartItemAsync(cart, input.CartItemId, input.Quantity);
        return MapCartResult(result, success, message, cartSvc);
    }

    public async Task<CartResult> RemoveCartItem(
        [Service] CartService cartSvc, [Service] AuthService auth,
        [Service] IHttpContextAccessor http, RemoveCartItemInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        var session = http.HttpContext?.Request.Headers["X-Session-Token"].FirstOrDefault();
        var cart = await cartSvc.GetCartAsync(cid, session);
        if (cart == null) return new CartResult { Message = "Cart not found." };

        var (result, success, message) = await cartSvc.RemoveCartItemAsync(cart, input.CartItemId);
        return MapCartResult(result, success, message, cartSvc);
    }

    public async Task<CartResult> ApplyCoupon(
        [Service] CartService cartSvc, [Service] AuthService auth,
        [Service] IHttpContextAccessor http, ApplyCouponInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        var session = http.HttpContext?.Request.Headers["X-Session-Token"].FirstOrDefault();
        var cart = await cartSvc.GetCartAsync(cid, session);
        if (cart == null) return new CartResult { Message = "Cart not found." };

        var (result, success, message) = await cartSvc.ApplyCouponAsync(cart, input.CouponCode);
        return MapCartResult(result, success, message, cartSvc);
    }

    public async Task<CartResult> RemoveCoupon(
        [Service] CartService cartSvc, [Service] AuthService auth,
        [Service] IHttpContextAccessor http, RemoveCouponInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        var session = http.HttpContext?.Request.Headers["X-Session-Token"].FirstOrDefault();
        var cart = await cartSvc.GetCartAsync(cid, session);
        if (cart == null) return new CartResult { Message = "Cart not found." };

        var (result, success, message) = await cartSvc.RemoveCouponAsync(cart);
        return MapCartResult(result, success, message, cartSvc);
    }

    public async Task<CartResult> MergeCart(
        [Service] CartService cartSvc, [Service] AuthService auth, MergeCartInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new CartResult { Message = "Not authenticated." };

        var (result, success, message, _) = await cartSvc.MergeCartAsync(input.CartId, cid.Value);
        return MapCartResult(result, success, message, cartSvc);
    }

    private static CartResult MapCartResult(Models.Cart.Cart? cart, bool success, string message, CartService cartSvc)
    {
        if (cart == null) return new CartResult { Success = false, Message = message };

        return new CartResult
        {
            Id = cart.Id, CartToken = cart.Id.ToString(),
            Subtotal = cart.SubTotal, ItemsCount = cart.ItemsCount,
            TaxAmount = cart.TaxTotal, ShippingAmount = 0,
            GrandTotal = cart.GrandTotal, DiscountAmount = cart.DiscountAmount,
            CouponCode = cart.CouponCode, ItemsQty = (int)(cart.ItemsQty ?? 0),
            IsGuest = cart.IsGuest ?? true, Success = success, Message = message,
            Items = new Connection<CartItemResult>
            {
                Edges = cart.Items.Select((i, idx) => new Edge<CartItemResult>
                {
                    Node = new CartItemResult
                    {
                        Id = i.Id, CartId = i.CartId, ProductId = i.ProductId,
                        Name = i.Name, Price = i.Price, Sku = i.Sku,
                        Quantity = i.Quantity, Type = i.Type,
                        BaseImage = cartSvc.GetCartItemBaseImage(i),
                        ProductUrlKey = cartSvc.GetCartItemProductUrlKey(i),
                        CanChangeQty = true
                    },
                    Cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(idx.ToString()))
                }).ToList(),
                TotalCount = cart.Items.Count,
                PageInfo = new PageInfo()
            }
        };
    }
}

// ─── Checkout Mutations ─────────────────────────────────────────────────

[ExtendObjectType("Mutation")]
public class CheckoutMutations
{
    public async Task<CheckoutAddressResponse> CheckoutAddress(
        [Service] CheckoutService svc, [Service] AuthService auth,
        [Service] CartService cartSvc, [Service] IHttpContextAccessor http,
        CheckoutAddressInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        var session = http.HttpContext?.Request.Headers["X-Session-Token"].FirstOrDefault();
        var cart = await cartSvc.GetCartAsync(cid, session);
        if (cart == null) return new CheckoutAddressResponse { Success = false, Message = "Cart not found." };

        var (success, message, addrId) = await svc.SaveCheckoutAddressAsync(
            cart.Id, input.FirstName, input.LastName, input.Address1, input.City,
            input.State, input.Country, input.Postcode, input.Phone, input.Email,
            input.UseForShipping ?? true, input.DefaultAddress ?? false, cid);

        var cartTokenForResponse = cart.CustomerId == null
            ? cartSvc.IssueGuestCartToken(cart.Id)
            : cart.Id.ToString();
        return new CheckoutAddressResponse { Success = success, Message = message, Id = addrId, CartToken = cartTokenForResponse };
    }

    public async Task<SimpleIdResult> CheckoutShippingMethod(
        [Service] CheckoutService svc, [Service] AuthService auth,
        [Service] CartService cartSvc, [Service] IHttpContextAccessor http,
        ShippingMethodInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        var session = http.HttpContext?.Request.Headers["X-Session-Token"].FirstOrDefault();
        var cart = await cartSvc.GetCartAsync(cid, session);
        if (cart == null) return new SimpleIdResult { Success = false, Message = "Cart not found." };

        var (success, message) = await svc.SaveShippingMethodAsync(cart.Id, input.Shipping_method);
        return new SimpleIdResult { Success = success, Id = cart.Id, Message = message };
    }

    public async Task<CheckoutPaymentResponse> CheckoutPaymentMethod(
        [Service] CheckoutService svc, [Service] AuthService auth,
        [Service] CartService cartSvc, [Service] IHttpContextAccessor http,
        PaymentMethodInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        var session = http.HttpContext?.Request.Headers["X-Session-Token"].FirstOrDefault();
        var cart = await cartSvc.GetCartAsync(cid, session);
        if (cart == null) return new CheckoutPaymentResponse { Success = false, Message = "Cart not found." };

        var (success, message, gatewayUrl, paymentData) = await svc.SavePaymentMethodAsync(cart.Id, input.Payment_method);
        return new CheckoutPaymentResponse { Success = success, Message = message, PaymentGatewayUrl = gatewayUrl, PaymentData = paymentData };
    }

    public async Task<CheckoutOrderResponse> CheckoutOrder(
        [Service] CheckoutService svc, [Service] AuthService auth,
        [Service] CartService cartSvc, [Service] IHttpContextAccessor http,
        PlaceOrderInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        var session = http.HttpContext?.Request.Headers["X-Session-Token"].FirstOrDefault();
        var cart = await cartSvc.GetCartAsync(cid, session);
        if (cart == null) return new CheckoutOrderResponse { Success = false, Message = "Cart not found." };

        var razorpayPaymentId = input.RazorpayPaymentId ?? input.Razorpay_payment_id;
        var razorpayOrderId = input.RazorpayOrderId ?? input.Razorpay_order_id;
        var razorpaySignature = input.RazorpaySignature ?? input.Razorpay_signature;

        var guestSession = cid == null ? session : null;
        var (success, message, orderId, incrementId) = await svc.PlaceOrderAsync(
            cart.Id, cid, razorpayPaymentId, razorpayOrderId, razorpaySignature, guestSession);
        return new CheckoutOrderResponse
        {
            Id = orderId, OrderId = orderId, OrderIncrementId = incrementId,
            Success = success, Message = message
        };
    }
}

// ─── Account Mutations ──────────────────────────────────────────────────

[ExtendObjectType("Mutation")]
public class AccountMutations
{
    public async Task<AddUpdateAddressResponse> AddUpdateCustomerAddress(
        [Service] AccountService svc, [Service] AuthService auth, AddUpdateAddressInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new AddUpdateAddressResponse { Message = "Not authenticated." };

        var addr = await svc.AddOrUpdateAddressAsync(cid.Value, input.AddressId,
            input.FirstName, input.LastName, input.Address1, input.City,
            input.State, input.Country, input.Postcode, input.Phone,
            input.Email, input.UseForShipping ?? false, input.DefaultAddress ?? false);

        return new AddUpdateAddressResponse
        {
            Id = addr.Id, AddressId = addr.Id, FirstName = addr.FirstName, LastName = addr.LastName,
            Email = addr.Email, Phone = addr.Phone, Address1 = addr.AddressLine,
            Country = addr.Country, State = addr.State, City = addr.City, Postcode = addr.Postcode,
            UseForShipping = addr.UseForShipping, DefaultAddress = addr.DefaultAddress
        };
    }

    public async Task<SimpleResult> DeleteCustomerAddress(
        [Service] AccountService svc, [Service] AuthService auth, DeleteAddressInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new SimpleResult { Success = false, Message = "Not authenticated." };

        var id = ParseIriId(input.Id);
        var (success, message) = await svc.DeleteAddressAsync(cid.Value, id);
        return new SimpleResult { Success = success, Message = message };
    }

    public async Task<ProfileUpdateResponse> CustomerProfileUpdate(
        [Service] AccountService svc, [Service] AuthService auth, ProfileUpdateInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new ProfileUpdateResponse { Id = 0 };

        // Handle password change
        if (!string.IsNullOrEmpty(input.NewPassword) && !string.IsNullOrEmpty(input.CurrentPassword))
        {
            await svc.ChangePasswordAsync(cid.Value, input.CurrentPassword, input.NewPassword);
        }

        // Handle email change
        if (!string.IsNullOrEmpty(input.Email) && !string.IsNullOrEmpty(input.CurrentPassword))
        {
            await svc.ChangeEmailAsync(cid.Value, input.Email, input.CurrentPassword);
        }

        // Handle profile update
        await svc.UpdateProfileAsync(cid.Value, input.FirstName, input.LastName,
            input.Phone, input.Gender, input.DateOfBirth, input.SubscribedToNewsLetter);

        return new ProfileUpdateResponse { Id = cid.Value };
    }

    public async Task<SimpleResult> CustomerProfileDelete(
        [Service] AccountService svc, [Service] AuthService auth, ProfileDeleteInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new SimpleResult { Success = false, Message = "Not authenticated." };

        var (success, message) = await svc.DeleteAccountAsync(cid.Value, input.Password);
        return new SimpleResult { Success = success, Message = message };
    }

    public async Task<WishlistMutationResult> CreateWishlist(
        [Service] DOSDbContext db, [Service] AuthService auth, CreateWishlistInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new WishlistMutationResult();

        var existing = await db.Wishlists.FirstOrDefaultAsync(w => w.CustomerId == cid && w.ProductId == input.ProductId);
        if (existing != null)
            return new WishlistMutationResult { Id = $"/api/shop/wishlists/{existing.Id}", _Id = existing.Id };

        var item = new Wishlist
        {
            CustomerId = cid.Value, ProductId = input.ProductId, ChannelId = 1,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.Wishlists.Add(item);
        await db.SaveChangesAsync();

        return new WishlistMutationResult
        {
            Id = $"/api/shop/wishlists/{item.Id}", _Id = item.Id,
            CreatedAt = item.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss")
        };
    }

    public async Task<WishlistMutationResult> DeleteWishlist(
        [Service] DOSDbContext db, [Service] AuthService auth, DeleteWishlistInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new WishlistMutationResult();

        var id = ParseIriId(input.Id);
        var item = await db.Wishlists.FirstOrDefaultAsync(w => w.Id == id && w.CustomerId == cid);
        if (item != null)
        {
            db.Wishlists.Remove(item);
            await db.SaveChangesAsync();
        }
        return new WishlistMutationResult { Id = input.Id, _Id = id };
    }

    public async Task<SimpleMessageResult> WishlistToCart(
        [Service] DOSDbContext db, [Service] CartService cartSvc, [Service] AuthService auth,
        MoveWishlistToCartInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new SimpleMessageResult { Message = "Not authenticated." };

        var wish = await db.Wishlists.FirstOrDefaultAsync(w => w.Id == input.WishlistItemId && w.CustomerId == cid);
        if (wish == null) return new SimpleMessageResult { Message = "Wishlist item not found." };

        var cart = await cartSvc.GetCartAsync(cid, null);
        if (cart == null)
        {
            var (c, _, _, _) = await cartSvc.CreateCartAsync(cid);
            cart = c;
        }

        await cartSvc.AddToCartAsync(cart, wish.ProductId, input.Quantity ?? 1);
        db.Wishlists.Remove(wish);
        await db.SaveChangesAsync();

        return new SimpleMessageResult { Message = "Item moved to cart successfully." };
    }

    public async Task<CompareMutationResult> CreateCompareItem(
        [Service] DOSDbContext db, [Service] AuthService auth, CreateCompareInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new CompareMutationResult();

        var item = new CompareItem
        {
            CustomerId = cid.Value, ProductId = input.ProductId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.CompareItems.Add(item);
        await db.SaveChangesAsync();

        return new CompareMutationResult { Id = $"/api/shop/compare-items/{item.Id}", _Id = item.Id };
    }

    public async Task<CompareMutationResult> DeleteCompareItem(
        [Service] DOSDbContext db, [Service] AuthService auth, string id)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new CompareMutationResult();

        var numId = ParseIriId(id);
        var item = await db.CompareItems.FirstOrDefaultAsync(ci => ci.Id == numId && ci.CustomerId == cid);
        if (item != null)
        {
            db.CompareItems.Remove(item);
            await db.SaveChangesAsync();
        }
        return new CompareMutationResult { Id = id };
    }

    public async Task<SimpleMessageResult> DeleteAllCompareItems(
        [Service] DOSDbContext db, [Service] AuthService auth)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new SimpleMessageResult { Message = "Not authenticated." };

        await db.CompareItems.Where(ci => ci.CustomerId == cid).ExecuteDeleteAsync();
        return new SimpleMessageResult { Message = "All compare items removed." };
    }

    public async Task<ProductReviewMutationResult> CreateProductReview(
        [Service] DOSDbContext db, [Service] AuthService auth, CreateReviewInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        var review = new ProductReview
        {
            ProductId = input.ProductId, Title = input.Title, Comment = input.Comment,
            Rating = input.Rating, Name = input.Name, Status = "pending",
            CustomerId = cid, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.ProductReviews.Add(review);
        await db.SaveChangesAsync();

        return new ProductReviewMutationResult
        {
            Id = review.Id, _Id = review.Id, Name = review.Name, Title = review.Title,
            Rating = review.Rating, Comment = review.Comment, Status = review.Status,
            CreatedAt = review.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            UpdatedAt = review.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss")
        };
    }

    public async Task<ReorderResult> ReorderOrder(
        [Service] AccountService svc, [Service] AuthService auth, ReorderInput input)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new ReorderResult { Success = false, Message = "Not authenticated." };

        var (success, message, orderId, count) = await svc.ReorderAsync(cid.Value, input.OrderId);
        return new ReorderResult { Success = success, Message = message, OrderId = orderId, ItemsAddedCount = count };
    }

    public SimpleResult ContactUs(ContactUsInput input)
    {
        return new SimpleResult { Success = true, Message = "Your message has been sent successfully." };
    }

    private static int ParseIriId(string id)
    {
        if (id.Contains('/')) { var parts = id.Split('/'); int.TryParse(parts.Last(), out var n); return n; }
        int.TryParse(id, out var num); return num;
    }
}

// ─── Mutation Result Types ──────────────────────────────────────────────

public class CustomerLoginResult
{
    public int Id { get; set; }
    // ApiToken/Token are kept for backwards compatibility with older
    // clients — they hold the same value as AccessToken.
    public string? ApiToken { get; set; }
    public string? Token { get; set; }
    public string? AccessToken { get; set; }
    public DateTime? AccessTokenExpiresAt { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiresAt { get; set; }
    public string TokenType { get; set; } = "Bearer";
    public string Message { get; set; } = "";
    public bool Success { get; set; }
}

public class CustomerRegisterResult
{
    public int Id { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public bool Status { get; set; }
    public string? ApiToken { get; set; }
    public string? Token { get; set; }
    public string? AccessToken { get; set; }
    public DateTime? AccessTokenExpiresAt { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiresAt { get; set; }
    public string TokenType { get; set; } = "Bearer";
    public string? Name { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

public class RefreshTokenResult
{
    public int Id { get; set; }
    public string? AccessToken { get; set; }
    public DateTime? AccessTokenExpiresAt { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiresAt { get; set; }
    public string TokenType { get; set; } = "Bearer";
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

public class SimpleResult { public bool Success { get; set; } public string Message { get; set; } = ""; }
public class SimpleIdResult : SimpleResult { public int? Id { get; set; } }
public class SimpleMessageResult { public string Message { get; set; } = ""; }

public class CartTokenResult
{
    public int Id { get; set; }
    public string? CartToken { get; set; }
    public int? CustomerId { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? SessionToken { get; set; }
    public bool IsGuest { get; set; }
}

public class CartResult
{
    public int Id { get; set; }
    public string? CartToken { get; set; }
    public decimal? Subtotal { get; set; }
    public int? ItemsCount { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? ShippingAmount { get; set; }
    public decimal? GrandTotal { get; set; }
    public decimal? DiscountAmount { get; set; }
    public string? CouponCode { get; set; }
    public int ItemsQty { get; set; }
    public bool IsGuest { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? SessionToken { get; set; }
    public Connection<CartItemResult>? Items { get; set; }
}

public class CartItemResult
{
    public int Id { get; set; }
    public int CartId { get; set; }
    public int ProductId { get; set; }
    public string? Name { get; set; }
    public decimal Price { get; set; }
    public string? BaseImage { get; set; }
    public string? Sku { get; set; }
    public int Quantity { get; set; }
    public string? Type { get; set; }
    public string? ProductUrlKey { get; set; }
    public bool CanChangeQty { get; set; } = true;
}

public class CheckoutAddressResponse : SimpleResult
{
    public int? Id { get; set; }
    public string? CartToken { get; set; }
}

public class CheckoutPaymentResponse : SimpleResult
{
    public string? PaymentGatewayUrl { get; set; }
    public string? PaymentData { get; set; }
}

public class CheckoutOrderResponse : SimpleResult
{
    public int? Id { get; set; }
    public int? OrderId { get; set; }
    public string? OrderIncrementId { get; set; }
}

public class AddUpdateAddressResponse
{
    public int Id { get; set; }
    public int AddressId { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? Country { get; set; }
    public string? State { get; set; }
    public string? City { get; set; }
    public string? Postcode { get; set; }
    public bool UseForShipping { get; set; }
    public bool DefaultAddress { get; set; }
    public string? Message { get; set; }
}

public class ProfileUpdateResponse { public int Id { get; set; } }

public class WishlistMutationResult
{
    public string Id { get; set; } = "";
    [GraphQLName("_id")] public int _Id { get; set; }
    public string? CreatedAt { get; set; }
}

public class CompareMutationResult
{
    public string Id { get; set; } = "";
    [GraphQLName("_id")] public int _Id { get; set; }
}

public class ProductReviewMutationResult
{
    public int Id { get; set; }
    [GraphQLName("_id")] public int _Id { get; set; }
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public int Rating { get; set; }
    public string? Comment { get; set; }
    public string? Status { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
}

public class ReorderResult : SimpleResult
{
    public int? OrderId { get; set; }
    public int ItemsAddedCount { get; set; }
}
