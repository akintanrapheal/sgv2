using System.Net.Http.Json;
using System.Text.Json;
using SterlingLams.Web.Services.Odoo.OdooModels;

namespace SterlingLams.Web.Services.Odoo;

public class OdooSettings
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public Dictionary<string, int> Stores { get; set; } = new();
    public int InventoryCacheTtlSeconds { get; set; } = 60;
}

public class OdooService : IOdooService
{
    private readonly HttpClient _http;
    private readonly OdooSettings _settings;
    private readonly ILogger<OdooService> _logger;

    private int? _uid;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public OdooService(HttpClient http, OdooSettings settings, ILogger<OdooService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    // ─── Authentication ──────────────────────────────────────────────────────

    private async Task<int> GetUidAsync()
    {
        if (_uid.HasValue) return _uid.Value;

        await _authLock.WaitAsync();
        try
        {
            if (_uid.HasValue) return _uid.Value;

            var request = new OdooRpcRequest
            {
                Params = new OdooRpcParams
                {
                    Service = "common",
                    Method = "authenticate",
                    Args = new object[] { _settings.Database, _settings.Username, _settings.ApiKey, new { } }
                }
            };

            var response = await PostAsync<int>("jsonrpc", request);
            _uid = response;
            _logger.LogInformation("Authenticated with Odoo. UID: {Uid}", _uid);
            return _uid.Value;
        }
        finally
        {
            _authLock.Release();
        }
    }

    // ─── Core RPC helper ─────────────────────────────────────────────────────

    private async Task<T> ExecuteKwAsync<T>(string model, string method, object[] args, object? kwargs = null)
    {
        var uid = await GetUidAsync();

        var request = new OdooRpcRequest
        {
            Params = new OdooRpcParams
            {
                Service = "object",
                Method = "execute_kw",
                Args = new object[]
                {
                    _settings.Database,
                    uid,
                    _settings.ApiKey,
                    model,
                    method,
                    args,
                    kwargs ?? new { }
                }
            }
        };

        return await PostAsync<T>("jsonrpc", request);
    }

    private async Task<T> PostAsync<T>(string endpoint, OdooRpcRequest request)
    {
        var response = await _http.PostAsJsonAsync(endpoint, request, _json);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var rpc = JsonSerializer.Deserialize<OdooRpcResponse<T>>(content, _json)
            ?? throw new InvalidOperationException("Empty Odoo response");

        if (!rpc.IsSuccess)
            throw new OdooException(rpc.Error?.Message ?? "Odoo RPC error", rpc.Error?.Data?.Debug);

        return rpc.Result!;
    }

    // ─── Products ────────────────────────────────────────────────────────────

    public async Task<List<OdooProduct>> GetProductsAsync(int offset = 0, int limit = 100)
    {
        var fields = new[] { "id", "name", "list_price", "default_code", "barcode", "categ_id", "description_sale", "active", "image_1920" };

        return await ExecuteKwAsync<List<OdooProduct>>(
            "product.template",
            "search_read",
            new object[] { new object[] { new object[] { "active", "=", true } } },
            new { fields, offset, limit, order = "id desc" }
        );
    }

    public async Task<OdooProduct?> GetProductByIdAsync(int odooProductId)
    {
        var fields = new[] { "id", "name", "list_price", "default_code", "barcode", "categ_id", "description_sale", "active", "image_1920" };

        var results = await ExecuteKwAsync<List<OdooProduct>>(
            "product.template",
            "search_read",
            new object[] { new object[] { new object[] { "id", "=", odooProductId } } },
            new { fields, limit = 1 }
        );

        return results.FirstOrDefault();
    }

    // ─── Inventory ───────────────────────────────────────────────────────────

    public async Task<List<OdooStockQuant>> GetStockQuantsAsync(int[] odooProductIds, int[] warehouseLocationIds)
    {
        var domain = new object[]
        {
            new object[] { "product_id", "in", odooProductIds },
            new object[] { "location_id", "in", warehouseLocationIds },
            new object[] { "location_id.usage", "=", "internal" }
        };

        var fields = new[] { "id", "product_id", "location_id", "quantity", "reserved_quantity" };

        return await ExecuteKwAsync<List<OdooStockQuant>>(
            "stock.quant",
            "search_read",
            new object[] { domain },
            new { fields }
        );
    }

    /// <summary>
    /// Returns a map: productOdooId → (warehouseId → quantity)
    /// </summary>
    public async Task<Dictionary<int, Dictionary<int, int>>> GetInventoryByStoreAsync(int[] odooProductIds)
    {
        var warehouseLocationIds = _settings.Stores.Values.ToArray();
        var quants = await GetStockQuantsAsync(odooProductIds, warehouseLocationIds);

        var result = new Dictionary<int, Dictionary<int, int>>();

        foreach (var quant in quants)
        {
            var productId = quant.ProductOdooId;
            var locationId = quant.LocationOdooId;
            var available = (int)Math.Max(0, quant.Quantity - quant.ReservedQuantity);

            if (!result.ContainsKey(productId))
                result[productId] = new Dictionary<int, int>();

            result[productId][locationId] = available;
        }

        return result;
    }

    // ─── Sale Orders ─────────────────────────────────────────────────────────

    public async Task<int> CreateSaleOrderAsync(CreateSaleOrderRequest request)
    {
        var orderVals = new Dictionary<string, object>
        {
            ["partner_id"] = request.OdooPartnerId,
            ["warehouse_id"] = request.OdooWarehouseId,
            ["note"] = request.Note ?? string.Empty,
            ["order_line"] = request.Lines.Select(l => new object[]
            {
                0, 0, new Dictionary<string, object>
                {
                    ["product_id"] = l.OdooProductId,
                    ["product_uom_qty"] = l.Quantity,
                    ["price_unit"] = l.PriceUnit
                }
            }).ToArray()
        };

        return await ExecuteKwAsync<int>(
            "sale.order",
            "create",
            new object[] { orderVals }
        );
    }

    public async Task<bool> ConfirmSaleOrderAsync(int odooOrderId)
    {
        var result = await ExecuteKwAsync<bool>(
            "sale.order",
            "action_confirm",
            new object[] { new[] { odooOrderId } }
        );
        return result;
    }
}

public class OdooException : Exception
{
    public string? Debug { get; }
    public OdooException(string message, string? debug = null) : base(message) => Debug = debug;
}
