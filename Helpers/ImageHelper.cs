namespace DOSApi.Helpers;

/// <summary>
/// Generates DOS-style image objects with small/medium/large/original URLs.
/// </summary>
public static class ImageHelper
{
    /// <summary>
    /// Digital Darsi seeded data stores full upstream URLs (e.g.
    /// https://foodstore.digitaldarsi.in/images/thumbs/xxx.webp). When the
    /// stored path is already absolute, return it as-is so the Flutter app
    /// loads it directly instead of getting a broken
    /// {baseUrl}/cache/small/https://... path.
    /// </summary>
    private static bool IsAbsoluteUrl(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static object? CategoryLogo(string? logoPath, string baseUrl)
    {
        if (string.IsNullOrEmpty(logoPath)) return null;
        if (IsAbsoluteUrl(logoPath))
        {
            return new
            {
                small_image_url = logoPath,
                medium_image_url = logoPath,
                large_image_url = logoPath,
                original_image_url = logoPath,
            };
        }
        return new
        {
            small_image_url = $"{baseUrl}/cache/small/{logoPath}",
            medium_image_url = $"{baseUrl}/cache/medium/{logoPath}",
            large_image_url = $"{baseUrl}/cache/large/{logoPath}",
            original_image_url = $"{baseUrl}/cache/original/{logoPath}"
        };
    }

    public static object? CategoryBanner(string? bannerPath, string baseUrl)
    {
        if (string.IsNullOrEmpty(bannerPath)) return null;
        if (IsAbsoluteUrl(bannerPath))
        {
            return new
            {
                small_image_url = bannerPath,
                medium_image_url = bannerPath,
                large_image_url = bannerPath,
                original_image_url = bannerPath,
            };
        }
        return new
        {
            small_image_url = $"{baseUrl}/cache/small/{bannerPath}",
            medium_image_url = $"{baseUrl}/cache/medium/{bannerPath}",
            large_image_url = $"{baseUrl}/cache/large/{bannerPath}",
            original_image_url = $"{baseUrl}/cache/original/{bannerPath}"
        };
    }

    public static object ProductImage(string? imagePath, string baseUrl, int productId)
    {
        if (string.IsNullOrEmpty(imagePath))
        {
            return new
            {
                small_image_url = $"{baseUrl}/vendor/webkul/ui/assets/images/product/small-product-placeholder.webp",
                medium_image_url = $"{baseUrl}/vendor/webkul/ui/assets/images/product/medium-product-placeholder.webp",
                large_image_url = $"{baseUrl}/vendor/webkul/ui/assets/images/product/large-product-placeholder.webp",
                original_image_url = $"{baseUrl}/vendor/webkul/ui/assets/images/product/large-product-placeholder.webp"
            };
        }

        if (IsAbsoluteUrl(imagePath))
        {
            return new
            {
                small_image_url = imagePath,
                medium_image_url = imagePath,
                large_image_url = imagePath,
                original_image_url = imagePath,
            };
        }

        return new
        {
            small_image_url = $"{baseUrl}/cache/small/{imagePath}",
            medium_image_url = $"{baseUrl}/cache/medium/{imagePath}",
            large_image_url = $"{baseUrl}/cache/large/{imagePath}",
            original_image_url = $"{baseUrl}/cache/original/{imagePath}"
        };
    }
}
