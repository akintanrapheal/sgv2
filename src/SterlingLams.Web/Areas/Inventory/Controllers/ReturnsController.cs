using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

/// <summary>
/// Step 2 of the refund workflow: once Finance approves a restock-requested refund, its returned units
/// land here for Inventory to decide per item — put back on the shelf (Restock) or write off as damaged
/// (recorded as shrinkage). No money is involved; the payout already happened at Finance approval.
/// </summary>
public class ReturnsController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly IRefundApprovalService _refunds;

    public ReturnsController(ApplicationDbContext db, IRefundApprovalService refunds)
    {
        _db = db;
        _refunds = refunds;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Returns to restock";
        var rows = await _db.Refunds
            .Where(r => r.Status == RefundStatus.Approved && r.RestockRequested
                     && r.Items.Any(i => i.RestockDecision == RestockDecision.Pending))
            .OrderBy(r => r.DecisionAt)
            .Select(r => new PendingReturnVm
            {
                Id = r.Id,
                Number = r.RefundNumber,
                Order = r.OriginalOrder.OrderNumber,
                Channel = r.OriginalOrder.Channel.ToString(),
                ApprovedAt = r.DecisionAt,
                StoreName = _db.Stores.Where(s => s.Id == r.RestockStoreId).Select(s => s.Name).FirstOrDefault() ?? "—",
                Reason = string.IsNullOrWhiteSpace(r.Reason) ? "—" : r.Reason!,
                Items = r.Items.Where(i => i.RestockDecision == RestockDecision.Pending)
                    .Select(i => new PendingReturnItem { ItemId = i.Id, Name = i.ProductName, Variant = i.VariantName, Qty = i.Quantity })
                    .ToList()
            })
            .ToListAsync();
        return View(rows);
    }

    // decisions[<itemId>] = "restock" | "writeoff" (from the per-item radios).
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(int id, [FromForm] Dictionary<int, string> decisions)
    {
        var map = new Dictionary<int, RestockDecision>();
        foreach (var (itemId, val) in decisions ?? new())
            map[itemId] = val == "restock" ? RestockDecision.Restocked
                        : val == "writeoff" ? RestockDecision.WrittenOff
                        : RestockDecision.Pending;

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var res = await _refunds.ResolveStockAsync(id, map, userId);
        if (res.Success)
            await LogAsync("RefundRestock", "Refund", id.ToString(), res.Message);
        TempData[res.Success ? "Success" : "Error"] = res.Message;
        return RedirectToAction(nameof(Index));
    }
}

public class PendingReturnVm
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public string Order { get; set; } = "";
    public string Channel { get; set; } = "";
    public DateTime? ApprovedAt { get; set; }
    public string StoreName { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<PendingReturnItem> Items { get; set; } = new();
}

public class PendingReturnItem
{
    public int ItemId { get; set; }
    public string Name { get; set; } = "";
    public string? Variant { get; set; }
    public int Qty { get; set; }
}
