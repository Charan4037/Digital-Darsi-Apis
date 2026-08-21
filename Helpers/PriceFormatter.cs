namespace DOSApi.Helpers;

/// <summary>
/// Formats a rupee amount for display, keeping only as many decimal places
/// as the amount actually needs: "₹90" for a whole rupee, "₹90.5" for one
/// real paisa digit, "₹90.52" for two — never a padded "₹90.50" or a
/// rounded-away "₹91". Every controller that builds a FormattedPrice string
/// should go through this instead of an ad-hoc "{price:N2}"/"{price:0.00}"
/// format string, which either loses a fractional price entirely or always
/// pads to 2 decimals. Mirrors formatInr on the Flutter side.
/// </summary>
public static class PriceFormatter
{
    public static string Format(decimal price) => $"₹{FormatNumber(price)}";

    /// <summary>Same rule as <see cref="Format(decimal)"/> but with a caller-supplied
    /// prefix (e.g. "Rs. " for the plain-text invoice/notification copy that
    /// predates the ₹ glyph being used everywhere else).</summary>
    public static string Format(decimal price, string prefix) => $"{prefix}{FormatNumber(price)}";

    private static string FormatNumber(decimal price)
    {
        var rounded = Math.Round(price, 2, MidpointRounding.AwayFromZero);
        if (rounded == Math.Truncate(rounded))
            return rounded.ToString("0");

        var text = rounded.ToString("0.00");
        if (text.EndsWith('0'))
            text = text[..^1];
        return text;
    }
}
