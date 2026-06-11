using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

/// <summary>
/// Base for the dedicated Inventory System (its own /Inventory area + layout).
/// Restricted to the Inventory team and full Administrators. This is a self-contained
/// workspace (stock, transfers, till, stock-take) separate from the website admin.
/// </summary>
[Area("Inventory")]
[Authorize(Roles = "Admin,Inventory")]
public abstract class InventoryAreaController : Controller
{
}
