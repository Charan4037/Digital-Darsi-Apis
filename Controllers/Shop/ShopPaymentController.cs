using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Services;

namespace DOSApi.Controllers.Shop;

/// <summary>
/// Backend half of the Razorpay contract the Flutter app already calls (see
/// api_service.dart's getPaymentGatewayConfig/createRazorpayOrder/
/// verifyRazorpayPayment). Anonymous like the GraphQL checkout mutations —
/// guest checkout never carries a JWT (guests are identified purely by the
/// signed cart session token), so gating this behind [Authorize] would
/// silently break Razorpay checkout for every guest while leaving logged-in
/// customers unaffected. The actual security boundary is CheckoutService.
/// PlaceOrderAsync, which re-verifies independently of what happened on this
/// controller — /verify here is a fast-fail UX convenience, not the real gate.
/// </summary>
[ApiController]
[Tags("Payment")]
[AllowAnonymous]
public class ShopPaymentController : ControllerBase
{
    private readonly CheckoutService _checkoutService;
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly PaymentSettingsService _paymentSettings;
    private readonly RazorpayService _razorpay;
    private readonly ILogger<ShopPaymentController> _log;

    public ShopPaymentController(
        CheckoutService checkoutService, CartService cartService, AuthService authService,
        PaymentSettingsService paymentSettings, RazorpayService razorpay, ILogger<ShopPaymentController> log)
    {
        _checkoutService = checkoutService;
        _cartService = cartService;
        _authService = authService;
        _paymentSettings = paymentSettings;
        _razorpay = razorpay;
        _log = log;
    }

    public record CreateOrderRequest(decimal? Amount, string? Currency);
    public record VerifyRequest(string? RazorpayOrderId, string? Razorpay_order_id, string? RazorpayPaymentId, string? Razorpay_payment_id, string? RazorpaySignature, string? Razorpay_signature);

    /// <summary>GET /api/shop/payment-gateway-config — public config the
    /// client needs to decide whether "Pay Online" is even offered, and the
    /// Razorpay key id to open the payment sheet with. Never the secret.</summary>
    [HttpGet("api/shop/payment-gateway-config")]
    [AllowAnonymous]
    public async Task<IActionResult> GetGatewayConfig()
    {
        var settings = await _paymentSettings.GetSettingsAsync();
        return Ok(new
        {
            razorpay = new
            {
                enabled = settings.OnlineUsable,
                keyId = settings.OnlineUsable ? settings.RazorpayKeyId : "",
                currency = "INR",
            }
        });
    }

    /// <summary>POST /api/shop/payment/razorpay/create-order — creates a
    /// real Razorpay order for the cart's own server-recomputed total (the
    /// client's `amount` is accepted for logging only, never trusted).</summary>
    [HttpPost("api/shop/payment/razorpay/create-order")]
    public async Task<IActionResult> CreateOrder(
        [FromBody] CreateOrderRequest req,
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId() ?? 0;
        var cart = await _cartService.GetCartAsync(customerId > 0 ? customerId : null, cartToken);
        if (cart == null) return NotFound(new { success = false, message = "Cart not found." });

        var (ok, message, result) = await _checkoutService.CreateRazorpayOrderAsync(cart.Id);
        if (!ok || result == null) return BadRequest(new { success = false, message });

        return Ok(new
        {
            orderId = result.OrderId,
            amount = result.AmountPaise,
            currency = result.Currency,
            keyId = result.KeyId,
        });
    }

    /// <summary>POST /api/shop/payment/razorpay/verify — fast-fail UX check
    /// only. The order is not actually placed until CheckoutService.
    /// PlaceOrderAsync independently re-verifies the same signature.</summary>
    [HttpPost("api/shop/payment/razorpay/verify")]
    public async Task<IActionResult> Verify([FromBody] VerifyRequest req)
    {
        var orderId = req.RazorpayOrderId ?? req.Razorpay_order_id;
        var paymentId = req.RazorpayPaymentId ?? req.Razorpay_payment_id;
        var signature = req.RazorpaySignature ?? req.Razorpay_signature;

        if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(paymentId) || string.IsNullOrWhiteSpace(signature))
            return Ok(new { verified = false });

        var verified = await _checkoutService.VerifyRazorpayPaymentAsync(orderId, paymentId, signature);
        return Ok(new { verified });
    }

    /// <summary>POST /api/shop/payment/razorpay/webhook — server-to-server
    /// safety net. Authenticated purely via the X-Razorpay-Signature header
    /// over the raw body (no user session involved), so this route stays
    /// anonymous but is not reachable without a valid webhook secret.</summary>
    [HttpPost("api/shop/payment/razorpay/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> Webhook()
    {
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync();
        Request.Body.Position = 0;

        var settings = await _paymentSettings.GetSettingsAsync();
        if (string.IsNullOrWhiteSpace(settings.RazorpayWebhookSecret))
            return Ok(); // Webhook not configured — nothing to do, but don't error Razorpay's retries.

        var signatureHeader = Request.Headers["X-Razorpay-Signature"].FirstOrDefault();
        if (!_razorpay.VerifyWebhookSignature(rawBody, signatureHeader, settings.RazorpayWebhookSecret))
        {
            _log.LogWarning("[Webhook] Razorpay signature verification failed.");
            return Unauthorized();
        }

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            var eventName = root.TryGetProperty("event", out var evtEl) ? evtEl.GetString() : null;
            if (eventName != "payment.captured") return Ok();

            var paymentEntity = root.GetProperty("payload").GetProperty("payment").GetProperty("entity");
            var razorpayOrderId = paymentEntity.GetProperty("order_id").GetString();
            var razorpayPaymentId = paymentEntity.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(razorpayOrderId) || string.IsNullOrEmpty(razorpayPaymentId))
                return Ok();

            var (success, message, orderId, _) = await _checkoutService.HandleWebhookPaymentCapturedAsync(razorpayOrderId, razorpayPaymentId);
            _log.LogInformation("[Webhook] payment.captured for {RazorpayOrderId}: success={Success} orderId={OrderId} message={Message}",
                razorpayOrderId, success, orderId, message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Webhook] Failed to process Razorpay webhook payload.");
        }

        return Ok();
    }
}
