using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Services;

namespace DOSApi.Controllers.Shop;

/// <summary>
/// Returns the app update policy (per platform) that the app reads on every
/// launch to decide whether to force-block or nudge the user to update.
/// Config is stored in `core_config` — see AppUpdateSettingsService — and
/// edited from the admin panel via AdminAppUpdateController. A blank field
/// means "not configured", which the app must treat as "no check possible,
/// don't block" rather than an error.
/// </summary>
[ApiController]
[Route("api/shop/app-update")]
[Tags("AppUpdate")]
[AllowAnonymous]
public class ShopAppUpdateController : ControllerBase
{
    private readonly AppUpdateSettingsService _appUpdate;

    public ShopAppUpdateController(AppUpdateSettingsService appUpdate)
    {
        _appUpdate = appUpdate;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var settings = await _appUpdate.GetSettingsAsync();
        return Ok(new
        {
            data = new
            {
                android = ToDto(settings.Android),
                ios = ToDto(settings.Ios),
            }
        });
    }

    private static object ToDto(AppUpdateSettingsService.PlatformSettings s) => new
    {
        latestVersion = s.LatestVersion,
        minVersion = s.MinVersion,
        storeUrl = s.StoreUrl,
        message = s.Message,
    };
}
