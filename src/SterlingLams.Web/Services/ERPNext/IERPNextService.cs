using SterlingLams.Web.Services.ERPNext.ERPNextModels;

namespace SterlingLams.Web.Services.ERPNext;

public interface IERPNextService
{
    /// <summary>Fetches a paginated list of active Items from ERPNext.</summary>
    Task<List<ERPNextItem>> GetItemsAsync(int offset = 0, int limit = 100);

    /// <summary>Fetches a single Item by its item code.</summary>
    Task<ERPNextItem?> GetItemByCodeAsync(string itemCode);

    /// <summary>
    /// Returns a map: itemCode → (warehouse → availableQty)
    /// by querying the Bin doctype in ERPNext.
    /// </summary>
    Task<Dictionary<string, Dictionary<string, int>>> GetInventoryByWarehouseAsync(string[] itemCodes);

    /// <summary>Creates a Sales Order in ERPNext and returns its name (SO-XXXX).</summary>
    Task<string> CreateSalesOrderAsync(ERPNextCreateSalesOrderRequest request);

    /// <summary>Submits (confirms) a Sales Order by setting docstatus to 1.</summary>
    Task<bool> SubmitSalesOrderAsync(string salesOrderName);

    /// <summary>
    /// Creates and immediately submits a Stock Entry (Material Issue) to deduct stock
    /// from the specified warehouse. Returns the Stock Entry name (e.g. STE-00001).
    /// </summary>
    Task<string> CreateMaterialIssueAsync(List<ERPNextMaterialIssueItem> items, string? reference = null);
}

public class ERPNextCreateSalesOrderRequest
{
    public string Customer { get; set; } = string.Empty;
    public string DeliveryDate { get; set; } = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd");
    public List<ERPNextSalesOrderItem> Items { get; set; } = new();
}

public class ERPNextSalesOrderItem
{
    public string ItemCode { get; set; } = string.Empty;
    public string Warehouse { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public decimal Rate { get; set; }
}

public class ERPNextMaterialIssueItem
{
    public string ItemCode { get; set; } = string.Empty;
    public string SourceWarehouse { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public decimal BasicRate { get; set; }
}
