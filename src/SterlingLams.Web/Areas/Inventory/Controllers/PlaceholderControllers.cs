using Microsoft.AspNetCore.Mvc;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

// These tabs are part of the Inventory System but are being built in the next slice.
// They render a placeholder inside the inventory layout so the system shape is visible.

public class StocktakeController : InventoryAreaController
{
    public IActionResult Index() { ViewData["Title"] = "Stock-take"; return View("Soon"); }
}
