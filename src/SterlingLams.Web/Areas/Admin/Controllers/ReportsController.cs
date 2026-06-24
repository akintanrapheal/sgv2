using Microsoft.AspNetCore.Mvc;

namespace SterlingLams.Web.Areas.Admin.Controllers;

// Reports/analytics now live in ONE place — the Inventory System's report suite (the comprehensive
// set: sales, by item/category/customer/staff, payments, discounts, stock value, movements,
// shrinkage, dead stock, reorder…). This controller used to duplicate a thin subset (Sales /
// Best-sellers / Stock); it now just forwards there so the Admin sidebar's "Reports" entry lands in
// the unified analytics home. Old deep links (Sales/Products/Stock) are redirected too.
public class ReportsController : AdminBaseController
{
    protected override string Section => "Reports";

    private IActionResult Home() => RedirectToAction("Summary", "Reports", new { area = "Inventory" });

    public IActionResult Index() => Home();
    public IActionResult Sales() => Home();
    public IActionResult Products() => Home();
    public IActionResult Stock() => Home();
}
