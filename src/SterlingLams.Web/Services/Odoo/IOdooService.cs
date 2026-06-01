using SterlingLams.Web.Services.Odoo.OdooModels;

namespace SterlingLams.Web.Services.Odoo;

public interface IOdooService
{
    Task<List<OdooProduct>> GetProductsAsync(int offset = 0, int limit = 100);
    Task<OdooProduct?> GetProductByIdAsync(int odooProductId);
    Task<List<OdooStockQuant>> GetStockQuantsAsync(int[] odooProductIds, int[] warehouseLocationIds);
    Task<Dictionary<int, Dictionary<int, int>>> GetInventoryByStoreAsync(int[] odooProductIds);
    Task<int> CreateSaleOrderAsync(CreateSaleOrderRequest request);
    Task<bool> ConfirmSaleOrderAsync(int odooOrderId);
}

public class CreateSaleOrderRequest
{
    public int OdooPartnerId { get; set; }
    public int OdooWarehouseId { get; set; }
    public List<SaleOrderLine> Lines { get; set; } = new();
    public string? Note { get; set; }
}

public class SaleOrderLine
{
    public int OdooProductId { get; set; }
    public int Quantity { get; set; }
    public decimal PriceUnit { get; set; }
}
