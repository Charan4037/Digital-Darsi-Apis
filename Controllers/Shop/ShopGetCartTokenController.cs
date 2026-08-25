using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Services;
using DOSApi.Helpers;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/get-cart-tokens")]
[Tags("GetCartToken")]
[AllowAnonymous]
public class ShopGetCartTokenController : ControllerBase
{
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly ExtraChargeService _extraChargeService;
    private readonly PreorderService _preorderService;
    private readonly DOSDbContext _db;
    private readonly string _baseUrl;

    public ShopGetCartTokenController(CartService cartService, AuthService authService, ExtraChargeService extraChargeService, PreorderService preorderService, DOSDbContext db, IConfiguration config)
    {
        _cartService = cartService;
        _authService = authService;
        _extraChargeService = extraChargeService;
        _preorderService = preorderService;
        _db = db;
        _baseUrl = (config["App:BaseUrl"] ?? "http://192.168.0.116:8000").TrimEnd('/');
    }

    /// <summary>Get list of carts</summary>
    [HttpGet]
    public async Task<IActionResult> GetCarts(
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();

        var query = _db.Carts
            .AsSplitQuery()
            .Include(c => c.Items).ThenInclude(i => i.Product).ThenInclude(p => p!.Images)
            .Include(c => c.Items).ThenInclude(i => i.Product).ThenInclude(p => p!.Flats)
            .Include(c => c.Items).ThenInclude(i => i.Product).ThenInclude(p => p!.Categories)
            .Include(c => c.Items).ThenInclude(i => i.Product).ThenInclude(p => p!.Parent).ThenInclude(p => p!.Categories)
            .Include(c => c.Addresses)
            .Include(c => c.Payment)
            .Where(c => c.IsActive == true);

        if (customerId.HasValue)
            query = query.Where(c => c.CustomerId == customerId);

        var carts = await query
            .OrderByDescending(c => c.Id)
            .ToListAsync();

        var mapped = new List<object>();
        foreach (var c in carts)
        {
            var extraCharges = await _extraChargeService.ComputeAsync(c.Items);
            var pincode = c.Addresses.FirstOrDefault(a => a.AddressType == "cart_shipping")?.Postcode
                ?? c.Addresses.FirstOrDefault(a => a.AddressType == "cart_billing")?.Postcode;
            var preorder = await _preorderService.ResolveForCartAsync(c, pincode);
            mapped.Add(CartResourceHelper.ToCartResource(c, _baseUrl, extraCharges, preorder));
        }

        return Ok(mapped);
    }

    /// <summary>Get specific cart by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetCartById(int id,
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);

        if (cart == null || cart.Id != id)
            return NotFound(new { message = "Cart not found." });

        var extraCharges = await _extraChargeService.ComputeAsync(cart.Items);
        var pincode = cart.Addresses.FirstOrDefault(a => a.AddressType == "cart_shipping")?.Postcode
            ?? cart.Addresses.FirstOrDefault(a => a.AddressType == "cart_billing")?.Postcode;
        var preorder = await _preorderService.ResolveForCartAsync(cart, pincode);
        return Ok(CartResourceHelper.ToCartResource(cart, _baseUrl, extraCharges, preorder));
    }
}
