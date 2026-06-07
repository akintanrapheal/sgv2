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

    /// <summary>
    /// Creates and submits a Sales Invoice with update_stock=1 — the same way ERPNext POS works.
    /// This records the sale AND deducts stock in one document, keeping website and POS in sync.
    /// Returns the invoice name (SINV-XXXX).
    /// </summary>
    Task<string> CreateSalesInvoiceAsync(ERPNextSalesInvoiceRequest request);
}

public class ERPNextSalesInvoiceRequest
{
    /// <summary>ERPNext Customer name — use "Walk-In Customer" for website orders.</summary>
    public string Customer { get; set; } = "Walk-In Customer";

    /// <summary>Website order number stored in ERPNext PO No field for easy cross-reference.</summary>
    public string PoNo { get; set; } = string.Empty;

    /// <summary>Human-readable remarks: customer name, email, fulfillment type.</summary>
    public string Remarks { get; set; } = string.Empty;

    public List<ERPNextInvoiceItem> Items { get; set; } = new();
}

public class ERPNextInvoiceItem
{
    public string ItemCode { get; set; } = string.Empty;
    public string Warehouse { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public decimal Rate { get; set; }
}
