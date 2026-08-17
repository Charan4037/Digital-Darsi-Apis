using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DOSApi.Services;

/// <summary>
/// Thin client for Razorpay's Orders API plus the signature checks that
/// actually gate order confirmation — creating a server-side order (so the
/// amount can't be tampered with client-side) and verifying both the
/// checkout-success signature and the webhook signature. Credentials come
/// from PaymentSettingsService (admin-configured), never from appsettings.
/// </summary>
public class RazorpayService
{
    public const string HttpClientName = "Razorpay";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RazorpayService> _log;

    public RazorpayService(IHttpClientFactory httpClientFactory, ILogger<RazorpayService> log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    public class RazorpayOrderResult
    {
        public required string OrderId { get; set; }
        public required long AmountPaise { get; set; }
        public required string Currency { get; set; }
    }

    /// <summary>Creates a real order on Razorpay's side so the payment sheet
    /// (and later, signature verification) is tied to a specific, server-
    /// pinned amount rather than whatever the client claims it charged.</summary>
    public async Task<RazorpayOrderResult> CreateOrderAsync(string keyId, string keySecret, decimal amountRupees, string currency, string receipt)
    {
        var amountPaise = (long)Math.Round(amountRupees * 100m, MidpointRounding.AwayFromZero);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.DefaultRequestHeaders.Authorization = BasicAuthHeader(keyId, keySecret);

        var body = JsonSerializer.Serialize(new { amount = amountPaise, currency, receipt });
        using var response = await client.PostAsync("v1/orders", new StringContent(body, Encoding.UTF8, "application/json"));
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _log.LogError("[Razorpay] create-order failed: {Status} {Body}", response.StatusCode, responseBody);
            throw new InvalidOperationException("Could not create Razorpay order. Please try again.");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var orderId = doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Razorpay did not return an order id.");
        var returnedAmount = doc.RootElement.TryGetProperty("amount", out var amtEl) ? amtEl.GetInt64() : amountPaise;
        var returnedCurrency = doc.RootElement.TryGetProperty("currency", out var curEl) ? curEl.GetString() ?? currency : currency;

        return new RazorpayOrderResult { OrderId = orderId, AmountPaise = returnedAmount, Currency = returnedCurrency };
    }

    /// <summary>Recomputes the checkout-success signature Razorpay's SDK
    /// hands the client (HMAC-SHA256 of "{orderId}|{paymentId}" using the key
    /// secret) and compares in constant time. This — not the client's own
    /// "verify" call — is the real gate on whether an order gets created.</summary>
    public bool VerifyPaymentSignature(string razorpayOrderId, string razorpayPaymentId, string signature, string keySecret)
    {
        var expected = HmacHex($"{razorpayOrderId}|{razorpayPaymentId}", keySecret);
        return FixedTimeEquals(expected, signature);
    }

    /// <summary>Verifies the `X-Razorpay-Signature` header on an incoming
    /// webhook call against the raw request body, using the separate webhook
    /// secret configured in Razorpay's dashboard (not the API key secret).</summary>
    public bool VerifyWebhookSignature(string rawBody, string? signatureHeader, string webhookSecret)
    {
        if (string.IsNullOrEmpty(signatureHeader)) return false;
        var expected = HmacHex(rawBody, webhookSecret);
        return FixedTimeEquals(expected, signatureHeader);
    }

    private static string HmacHex(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(actual);
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static AuthenticationHeaderValue BasicAuthHeader(string keyId, string keySecret)
    {
        var raw = Encoding.UTF8.GetBytes($"{keyId}:{keySecret}");
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
    }
}
