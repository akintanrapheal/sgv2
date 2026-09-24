using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin → Google Analytics: live GA4 reports (users, sessions, page views, bounce rate, average
/// session duration, a daily series and top pages) pulled through the GA Data API. Read-only; its own
/// grantable "GoogleAnalytics" section. Configuration (Measurement ID, Property ID, service-account key)
/// lives in Admin → Integrations. Degrades gracefully when GA isn't set up.
/// </summary>
public class GoogleAnalyticsController : AdminBaseController
{
    protected override string Section => "GoogleAnalytics";

    private readonly IGoogleAnalytics _ga;
    private readonly ISettingsService _settings;
    public GoogleAnalyticsController(IGoogleAnalytics ga, ISettingsService settings)
    {
        _ga = ga;
        _settings = settings;
    }

    public async Task<IActionResult> Index(int days = 28)
    {
        ViewData["Title"] = "Google Analytics";
        if (days is not (7 or 28 or 90)) days = 28;

        var vm = new GaPageVm
        {
            Days = days,
            MeasurementId = (await _settings.GetAsync("ga.measurement_id", "")).Trim(),
            Result = await _ga.GetAsync(days),
        };
        return View(vm);
    }

    public class GaPageVm
    {
        public int Days { get; set; }
        public string MeasurementId { get; set; } = "";
        public GaResult Result { get; set; } = new();
    }
}
