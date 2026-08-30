using Microsoft.AspNetCore.Mvc;
using DOSApi.Data;
using DOSApi.Services;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin control over the app update policy — per platform (Android/iOS)
/// latest/minimum versions, store URL and update message. See
/// AppUpdateSettingsService for the underlying core_config storage and how
/// force vs. skip is derived from the two version thresholds.
/// Routes: /api/v1/admin/settings/app-update
/// </summary>
[Route("api/v1/admin/settings/app-update")]
[Tags("Admin – App Update")]
public class AdminAppUpdateController : AdminBaseController
{
    private const string Resource = "app_update";
    private readonly AppUpdateSettingsService _appUpdate;

    public AdminAppUpdateController(DOSDbContext db, IConfiguration config, AppUpdateSettingsService appUpdate) : base(db, config)
    {
        _appUpdate = appUpdate;
    }

    public record PlatformUpdateRequest(string LatestVersion, string MinVersion, string StoreUrl, string? Message);
    public record UpdateAppUpdateRequest(PlatformUpdateRequest Android, PlatformUpdateRequest Ios);

    /// <summary>Current app update policy for both platforms.</summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        if (!await HasPermissionAsync(Resource)) return AdminUnauthorized();

        var settings = await _appUpdate.GetSettingsAsync();
        return Ok(new
        {
            success = true,
            data = new
            {
                android = ToDto(settings.Android),
                ios = ToDto(settings.Ios),
            }
        });
    }

    /// <summary>
    /// Update the app update policy for both platforms in one call.
    /// `minVersion` is the hard floor — installed versions below it are
    /// force-blocked. `latestVersion` at or above `minVersion` gets a
    /// dismissible nudge instead. Setting `minVersion` equal to
    /// `latestVersion` forces every outdated user immediately.
    /// </summary>
    [HttpPatch]
    public async Task<IActionResult> Update([FromBody] UpdateAppUpdateRequest req)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        if (req.Android == null || req.Ios == null)
            return BadRequest(new { success = false, message = "android and ios settings are both required." });

        var androidError = ValidatePlatform(req.Android, "android");
        if (androidError != null) return BadRequest(new { success = false, message = androidError });
        var iosError = ValidatePlatform(req.Ios, "ios");
        if (iosError != null) return BadRequest(new { success = false, message = iosError });

        await _appUpdate.SetAsync(
            new AppUpdateSettingsService.PlatformSettings
            {
                LatestVersion = req.Android.LatestVersion.Trim(),
                MinVersion = req.Android.MinVersion.Trim(),
                StoreUrl = req.Android.StoreUrl.Trim(),
                Message = req.Android.Message?.Trim() ?? "",
            },
            new AppUpdateSettingsService.PlatformSettings
            {
                LatestVersion = req.Ios.LatestVersion.Trim(),
                MinVersion = req.Ios.MinVersion.Trim(),
                StoreUrl = req.Ios.StoreUrl.Trim(),
                Message = req.Ios.Message?.Trim() ?? "",
            });

        return Ok(new { success = true, message = "App update settings saved." });
    }

    private static string? ValidatePlatform(PlatformUpdateRequest req, string label)
    {
        if (string.IsNullOrWhiteSpace(req.LatestVersion))
            return $"{label}.latestVersion is required.";
        if (string.IsNullOrWhiteSpace(req.MinVersion))
            return $"{label}.minVersion is required.";
        if (string.IsNullOrWhiteSpace(req.StoreUrl))
            return $"{label}.storeUrl is required.";
        return null;
    }

    private static object ToDto(AppUpdateSettingsService.PlatformSettings s) => new
    {
        latestVersion = s.LatestVersion,
        minVersion = s.MinVersion,
        storeUrl = s.StoreUrl,
        message = s.Message,
    };
}
