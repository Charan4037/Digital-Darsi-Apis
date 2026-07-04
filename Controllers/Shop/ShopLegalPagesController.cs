using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;

namespace DOSApi.Controllers.Shop;

/// <summary>
/// Serves the URL + title for the app's Terms and Privacy pages so ops can
/// redirect either one from the <c>core_config</c> table without shipping a
/// new app build. Hardcoded defaults are returned when the config rows
/// aren't present, so the endpoint is safe to ship before any DB changes.
/// </summary>
[ApiController]
[Route("api/shop/legal-pages")]
[Tags("LegalPages")]
[AllowAnonymous]
public class ShopLegalPagesController : ControllerBase
{
    private readonly DOSDbContext _db;

    public ShopLegalPagesController(DOSDbContext db)
    {
        _db = db;
    }

    /// core_config keys that override the defaults. Rows can be inserted or
    /// updated via existing admin tooling; missing rows fall through to the
    /// hardcoded values below.
    private const string TermsUrlKey = "app.legal.terms.url";
    private const string TermsTitleKey = "app.legal.terms.title";
    private const string PrivacyUrlKey = "app.legal.privacy.url";
    private const string PrivacyTitleKey = "app.legal.privacy.title";

    private const string DefaultTermsUrl =
        "https://store.digitaldarsi.in/conditions-of-use";
    private const string DefaultTermsTitle = "Terms & Conditions";
    private const string DefaultPrivacyUrl =
        "https://store.digitaldarsi.in/privacy-notice";
    private const string DefaultPrivacyTitle = "Privacy Policy";

    /// <summary>Return the Terms + Privacy URLs/titles for the mobile app.</summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var keys = new[]
        {
            TermsUrlKey, TermsTitleKey, PrivacyUrlKey, PrivacyTitleKey,
        };
        var rows = await _db.CoreConfigs
            .Where(c => keys.Contains(c.Code))
            .ToListAsync();

        string Resolve(string key, string fallback)
        {
            var v = rows.FirstOrDefault(r => r.Code == key)?.Value;
            return string.IsNullOrWhiteSpace(v) ? fallback : v!;
        }

        return Ok(new
        {
            data = new
            {
                terms = new
                {
                    url = Resolve(TermsUrlKey, DefaultTermsUrl),
                    title = Resolve(TermsTitleKey, DefaultTermsTitle),
                },
                privacy = new
                {
                    url = Resolve(PrivacyUrlKey, DefaultPrivacyUrl),
                    title = Resolve(PrivacyTitleKey, DefaultPrivacyTitle),
                },
            }
        });
    }
}
