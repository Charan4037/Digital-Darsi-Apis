namespace DOSApi.Services;

/// Per-request locale derived from the client's Accept-Language header.
/// The client sends a simple code ("en" or "te"); anything else falls back
/// to the App:Locale default from config.
public class LocaleContext
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "te"
    };

    public string Locale { get; }

    public LocaleContext(IHttpContextAccessor accessor, IConfiguration config)
    {
        var fallback = config["App:Locale"] ?? "en";

        var header = accessor.HttpContext?.Request.Headers["Accept-Language"].ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            Locale = fallback;
            return;
        }

        // Take the first language tag, strip any q-value, strip region ("en-US" -> "en").
        var primary = header
            .Split(',')[0]
            .Split(';')[0]
            .Trim()
            .Split('-')[0]
            .ToLowerInvariant();

        Locale = Supported.Contains(primary) ? primary : fallback;
    }
}
