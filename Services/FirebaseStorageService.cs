using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using System.Text.RegularExpressions;

namespace BagistoApi.Services;

/// <summary>
/// Downloads images from external source URLs and uploads them freshly to
/// Firebase Storage. Used by the image migration endpoint to move all
/// product and category images off the original scrape-source domains and
/// into Firebase Storage so the app never depends on those servers.
/// </summary>
public sealed class FirebaseStorageService : IDisposable
{
    private readonly StorageClient _storage;
    private readonly HttpClient _http;
    private readonly string _bucket;
    private bool _disposed;

    public string Bucket => _bucket;

    public FirebaseStorageService(IConfiguration config, IHttpClientFactory httpFactory)
    {
        var saPath     = config["Firebase:ServiceAccountPath"] ?? "";
        var projectId  = config["Firebase:ProjectId"] ?? "";
        _bucket        = config["Firebase:StorageBucket"]
                         ?? $"{projectId}.appspot.com";

        if (string.IsNullOrWhiteSpace(_bucket) || _bucket == ".appspot.com")
            throw new InvalidOperationException(
                "Firebase:StorageBucket (or Firebase:ProjectId) must be configured.");

        GoogleCredential cred;
        if (!string.IsNullOrWhiteSpace(saPath) && File.Exists(saPath))
            cred = GoogleCredential.FromFile(saPath);
        else
        {
            var envPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS") ?? "";
            if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
                cred = GoogleCredential.FromFile(envPath);
            else
                throw new InvalidOperationException(
                    "Firebase service account credentials not found. "
                    + "Set Firebase:ServiceAccountPath in config or "
                    + "GOOGLE_APPLICATION_CREDENTIALS env var.");
        }

        cred = cred.CreateScoped("https://www.googleapis.com/auth/devstorage.full_control");
        _storage = StorageClient.Create(cred);
        _http    = httpFactory.CreateClient("ImageDownloader");
    }

    // ─── URL classification ───────────────────────────────────────────────

    /// <summary>Returns true when the URL already points to Firebase Storage
    /// — i.e. the image has already been migrated and must not be
    /// re-uploaded.</summary>
    public static bool IsFirebaseUrl(string? url) =>
        !string.IsNullOrEmpty(url) &&
        (url.Contains("firebasestorage.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
         url.Contains("storage.googleapis.com",        StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns true when the URL is an external http/https link that
    /// has NOT yet been migrated to Firebase Storage.</summary>
    public static bool NeedsMigration(string? url) =>
        !string.IsNullOrEmpty(url) &&
        (url.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
        !IsFirebaseUrl(url);

    // ─── Upload ───────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads <paramref name="sourceUrl"/> fresh from the network and
    /// uploads the bytes to Firebase Storage at <paramref name="storagePath"/>.
    /// Returns the public Firebase Storage download URL on success, or
    /// <c>null</c> when the download fails (404, timeout, network error).
    /// </summary>
    public async Task<(string? Url, string? Error)> UploadFromUrlAsync(
        string sourceUrl,
        string storagePath,
        CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(
                sourceUrl, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!resp.IsSuccessStatusCode)
                return (null, $"HTTP {(int)resp.StatusCode} downloading source image");

            var contentType = resp.Content.Headers.ContentType?.MediaType
                              ?? ContentTypeFromPath(storagePath);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            await _storage.UploadObjectAsync(
                _bucket, storagePath, contentType, stream,
                cancellationToken: ct);

            return (BuildFirebaseUrl(_bucket, storagePath), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (null, ex.Message); }
    }

    // ─── URL / path helpers ───────────────────────────────────────────────

    /// <summary>Builds the public Firebase Storage download URL for an
    /// already-uploaded object.</summary>
    public static string BuildFirebaseUrl(string bucket, string storagePath) =>
        $"https://firebasestorage.googleapis.com/v0/b/{bucket}/o/" +
        $"{Uri.EscapeDataString(storagePath)}?alt=media";

    private static readonly Regex _safenameRe =
        new(@"[^a-zA-Z0-9._-]", RegexOptions.Compiled);

    /// <summary>Extracts and sanitises the filename from a URL (last path
    /// segment). Falls back to "image.jpg" on malformed URLs.</summary>
    public static string FileNameFromUrl(string url)
    {
        try
        {
            var seg  = new Uri(url).AbsolutePath;
            var name = System.IO.Path.GetFileName(seg);
            name = _safenameRe.Replace(Uri.UnescapeDataString(name), "_");
            return string.IsNullOrWhiteSpace(name) ? "image.jpg" : name;
        }
        catch { return "image.jpg"; }
    }

    /// <summary>Returns the file extension (with dot) from a URL, falling
    /// back to ".jpg" when none is found.</summary>
    public static string ExtFromUrl(string url)
    {
        try
        {
            var ext = System.IO.Path.GetExtension(new Uri(url).AbsolutePath)
                          .ToLowerInvariant();
            return ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".svg"
                ? ext : ".jpg";
        }
        catch { return ".jpg"; }
    }

    private static string ContentTypeFromPath(string storagePath)
    {
        var ext = System.IO.Path.GetExtension(storagePath).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"            => "image/png",
            ".webp"           => "image/webp",
            ".gif"            => "image/gif",
            ".svg"            => "image/svg+xml",
            _                 => "image/jpeg",
        };
    }

    // ─── Dispose ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (!_disposed) { _storage.Dispose(); _disposed = true; }
    }
}
