using System.Collections.Concurrent;
using BagistoApi.Data;
using BagistoApi.Models.Catalog;
using Microsoft.EntityFrameworkCore;

namespace BagistoApi.Services;

/// <summary>
/// Migrates all product images and category logos/banners from their
/// original external URLs to Firebase Storage.
///
/// Strategy:
///   1. Load every record whose path is an external http/https URL.
///   2. Download each image fresh from the source (parallel, up to 8 concurrent).
///   3. Upload to Firebase Storage.
///   4. After all uploads complete, save new URLs to the DB sequentially
///      (EF Core DbContext is not thread-safe — we must not SaveChanges
///       from multiple threads simultaneously).
///
/// The method is idempotent: any path that already points at Firebase
/// Storage is counted as "skipped" and left untouched.
/// </summary>
public static class ImageMigrationRunner
{
    public record MigrationReport(
        string Bucket,
        ProductReport ProductImages,
        CategoryReport CategoryImages,
        BannerReport BannerImages);

    public record ProductReport(
        int Total, int Migrated, int Skipped, int Failed,
        IReadOnlyList<string> FailedUrls);

    public record CategoryReport(
        int Total, int Migrated, int Skipped, int Failed,
        IReadOnlyList<string> FailedUrls);

    public record BannerReport(
        int Total, int Migrated, int Skipped, int Failed,
        IReadOnlyList<string> FailedUrls);

    public static async Task<MigrationReport> RunAsync(
        BagistoDbContext db,
        FirebaseStorageService storage,
        TextWriter? log = null,
        CancellationToken ct = default)
    {
        var sem = new SemaphoreSlim(8, 8);
        void Log(string msg) => log?.WriteLine($"[ImageMigration] {msg}");

        // ── 1. Product images ─────────────────────────────────────────────

        var allProductImages = await db.ProductImages
            .AsNoTracking()
            .Where(i => i.Path != null && i.Path.StartsWith("http"))
            .Select(i => new { i.Id, i.ProductId, i.Position, i.Path })
            .ToListAsync(ct);

        int piSkipped = 0;
        var piPending = new List<(int Id, int ProductId, int Position, string OrigUrl)>();
        foreach (var img in allProductImages)
        {
            if (FirebaseStorageService.IsFirebaseUrl(img.Path)) { piSkipped++; continue; }
            piPending.Add((img.Id, img.ProductId, img.Position, img.Path));
        }

        Log($"Product images: {piPending.Count} to upload, {piSkipped} already on Firebase.");

        // Parallel upload pass — results collected in a thread-safe bag.
        var piResults   = new ConcurrentBag<(int Id, string OrigUrl, string? NewUrl)>();
        string? piFirstError = null;
        var piUploadTasks = piPending.Select(async img =>
        {
            var ext  = FirebaseStorageService.ExtFromUrl(img.OrigUrl);
            var path = $"product-images/{img.ProductId}/{img.Position}{ext}";

            await sem.WaitAsync(ct);
            try
            {
                var (newUrl, err) = await storage.UploadFromUrlAsync(img.OrigUrl, path, ct);
                if (err != null) Interlocked.CompareExchange(ref piFirstError, err, null);
                piResults.Add((img.Id, img.OrigUrl, newUrl));
            }
            finally { sem.Release(); }
        });
        await Task.WhenAll(piUploadTasks);

        if (piFirstError != null)
            Log($"First upload error: {piFirstError}");

        // Sequential DB save pass.
        int piMigrated  = 0;
        int piFailed    = 0;
        var piFailedUrls = new List<string>();
        int piProgress  = 0;

        foreach (var (id, origUrl, newUrl) in piResults)
        {
            if (newUrl == null)
            {
                piFailed++;
                piFailedUrls.Add(origUrl);
                continue;
            }

            var entity = new ProductImage { Id = id };
            db.ProductImages.Attach(entity);
            entity.Path = newUrl;
            await db.SaveChangesAsync(ct);
            piMigrated++;

            piProgress++;
            if (piProgress % 100 == 0)
                Log($"  ... {piProgress}/{piPending.Count} product images saved.");
        }

        Log($"Product images done: {piMigrated} migrated, {piFailed} failed.");

        // ── 2. Category logos and banners ─────────────────────────────────

        var allCategories = await db.Categories
            .AsNoTracking()
            .Where(c => (c.LogoPath   != null && c.LogoPath.StartsWith("http")) ||
                        (c.BannerPath != null && c.BannerPath.StartsWith("http")))
            .Select(c => new { c.Id, c.LogoPath, c.BannerPath })
            .ToListAsync(ct);

        int catSkipped = 0;
        var catPending = new List<(int Id, string Url, string Slot)>();
        foreach (var cat in allCategories)
        {
            if (FirebaseStorageService.NeedsMigration(cat.LogoPath))
                catPending.Add((cat.Id, cat.LogoPath!, "logo"));
            else if (FirebaseStorageService.IsFirebaseUrl(cat.LogoPath))
                catSkipped++;

            if (FirebaseStorageService.NeedsMigration(cat.BannerPath))
                catPending.Add((cat.Id, cat.BannerPath!, "banner"));
            else if (FirebaseStorageService.IsFirebaseUrl(cat.BannerPath))
                catSkipped++;
        }

        Log($"Category images: {catPending.Count} to upload, {catSkipped} already on Firebase.");

        // Parallel upload pass.
        var catResults   = new ConcurrentBag<(int Id, string Slot, string OrigUrl, string? NewUrl)>();
        string? catFirstError = null;
        var catUploadTasks = catPending.Select(async job =>
        {
            var ext  = FirebaseStorageService.ExtFromUrl(job.Url);
            var path = $"category-images/{job.Id}/{job.Slot}{ext}";

            await sem.WaitAsync(ct);
            try
            {
                var (newUrl, err) = await storage.UploadFromUrlAsync(job.Url, path, ct);
                if (err != null) Interlocked.CompareExchange(ref catFirstError, err, null);
                catResults.Add((job.Id, job.Slot, job.Url, newUrl));
            }
            finally { sem.Release(); }
        });
        await Task.WhenAll(catUploadTasks);

        if (catFirstError != null)
            Log($"First category upload error: {catFirstError}");

        // Sequential DB save pass — group by category ID so a category whose
        // logo AND banner both need updating is handled in a single Attach+Save,
        // avoiding the "already tracked" EF Core conflict.
        int catMigrated   = 0;
        int catFailed     = 0;
        var catFailedUrls = new List<string>();

        foreach (var group in catResults.GroupBy(r => r.Id))
        {
            var entity = new Category { Id = group.Key };
            db.Categories.Attach(entity);

            foreach (var (_, slot, origUrl, newUrl) in group)
            {
                if (newUrl == null)
                {
                    catFailed++;
                    catFailedUrls.Add(origUrl);
                    continue;
                }
                if (slot == "logo")   entity.LogoPath   = newUrl;
                else                  entity.BannerPath = newUrl;
                catMigrated++;
            }

            await db.SaveChangesAsync(ct);
        }

        Log($"Category images done: {catMigrated} migrated, {catFailed} failed.");

        // ── 3. Promotional banners (dd_scraped_banners.image_url) ──────────

        var allBanners = await db.ScrapedBanners
            .AsNoTracking()
            .Where(b => b.ImageUrl != null && b.ImageUrl.StartsWith("http"))
            .Select(b => new { b.Id, b.ImageUrl })
            .ToListAsync(ct);

        int bannerSkipped = 0;
        var bannerPending = new List<(int Id, string Url)>();
        foreach (var b in allBanners)
        {
            if (FirebaseStorageService.IsFirebaseUrl(b.ImageUrl)) { bannerSkipped++; continue; }
            bannerPending.Add((b.Id, b.ImageUrl));
        }

        Log($"Banner images: {bannerPending.Count} to upload, {bannerSkipped} already on Firebase.");

        // Parallel upload pass.
        var bannerResults   = new ConcurrentBag<(int Id, string OrigUrl, string? NewUrl)>();
        string? bannerFirstError = null;
        var bannerUploadTasks = bannerPending.Select(async job =>
        {
            var ext  = FirebaseStorageService.ExtFromUrl(job.Url);
            var path = $"banner-images/{job.Id}/banner{ext}";

            await sem.WaitAsync(ct);
            try
            {
                var (newUrl, err) = await storage.UploadFromUrlAsync(job.Url, path, ct);
                if (err != null) Interlocked.CompareExchange(ref bannerFirstError, err, null);
                bannerResults.Add((job.Id, job.Url, newUrl));
            }
            finally { sem.Release(); }
        });
        await Task.WhenAll(bannerUploadTasks);

        if (bannerFirstError != null)
            Log($"First banner upload error: {bannerFirstError}");

        // Sequential DB save pass.
        int bannerMigrated   = 0;
        int bannerFailed     = 0;
        var bannerFailedUrls = new List<string>();

        foreach (var (id, origUrl, newUrl) in bannerResults)
        {
            if (newUrl == null)
            {
                bannerFailed++;
                bannerFailedUrls.Add(origUrl);
                continue;
            }

            var entity = new ScrapedBanner { Id = id };
            db.ScrapedBanners.Attach(entity);
            entity.ImageUrl = newUrl;
            await db.SaveChangesAsync(ct);
            bannerMigrated++;
        }

        Log($"Banner images done: {bannerMigrated} migrated, {bannerFailed} failed.");

        return new MigrationReport(
            Bucket: storage.Bucket,
            ProductImages: new ProductReport(
                Total:    piPending.Count,
                Migrated: piMigrated,
                Skipped:  piSkipped,
                Failed:   piFailed,
                FailedUrls: piFailedUrls),
            CategoryImages: new CategoryReport(
                Total:    catPending.Count,
                Migrated: catMigrated,
                Skipped:  catSkipped,
                Failed:   catFailed,
                FailedUrls: catFailedUrls),
            BannerImages: new BannerReport(
                Total:    bannerPending.Count,
                Migrated: bannerMigrated,
                Skipped:  bannerSkipped,
                Failed:   bannerFailed,
                FailedUrls: bannerFailedUrls));
    }
}
