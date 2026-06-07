using System.Net.Http.Json;
using System.Text.Json;
using SterlingLams.Web.Services.ERPNext.ERPNextModels;

namespace SterlingLams.Web.Services.ERPNext;

public class ERPNextSettings
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public string DefaultCustomer { get; set; } = "Walk-In Customer";
    public int InventoryCacheTtlSeconds { get; set; } = 60;
}

public class ERPNextService : IERPNextService
{
    private readonly HttpClient _http;
    private readonly ERPNextSettings _settings;
    private readonly ILogger<ERPNextService> _logger;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public ERPNextService(HttpClient http, ERPNextSettings settings, ILogger<ERPNextService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    // ─── Items ───────────────────────────────────────────────────────────────

    public async Task<List<ERPNextItem>> GetItemsAsync(int offset = 0, int limit = 100)
    {
        var fields = """["name","item_name","standard_rate","item_group","description","disabled"]""";
        var filters = """[["disabled","=",0]]""";
        var url = $"/api/resource/Item?fields={Uri.EscapeDataString(fields)}" +
                  $"&filters={Uri.EscapeDataString(filters)}" +
                  $"&limit_page_length={limit}&limit_start={offset}&order_by=creation+desc";

        var response = await _http.GetFromJsonAsync<ERPNextListResponse<ERPNextItem>>(url, _json)
            ?? new ERPNextListResponse<ERPNextItem>();
        return response.Data;
    }

    public async Task<ERPNextItem?> GetItemByCodeAsync(string itemCode)
    {
        var url = $"/api/resource/Item/{Uri.EscapeDataString(itemCode)}";
        try
        {
            var response = await _http.GetFromJsonAsync<ERPNextSingleResponse<ERPNextItem>>(url, _json);
            return response?.Data;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // ─── Inventory ───────────────────────────────────────────────────────────

    public async Task<Dictionary<string, Dictionary<string, int>>> GetInventoryByWarehouseAsync(string[] itemCodes)
    {
        var result = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        if (itemCodes.Length == 0) return result;

        var quotedCodes = string.Join(",", itemCodes.Select(c => $"\"{c}\""));
        var filters = $"""[["item_code","in",[{quotedCodes}]]]""";
        var fields = """["item_code","warehouse","actual_qty","reserved_qty"]""";
        var url = $"/api/resource/Bin?fields={Uri.EscapeDataString(fields)}" +
                  $"&filters={Uri.EscapeDataString(filters)}&limit_page_length=500";

        var response = await _http.GetFromJsonAsync<ERPNextListResponse<ERPNextBin>>(url, _json)
            ?? new ERPNextListResponse<ERPNextBin>();

        foreach (var bin in response.Data)
        {
            if (!result.ContainsKey(bin.ItemCode))
                result[bin.ItemCode] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            result[bin.ItemCode][bin.Warehouse] = bin.AvailableQty;
        }

        return result;
    }

    // ─── Sales Invoice (matches ERPNext POS flow) ────────────────────────────

    public async Task<string> CreateSalesInvoiceAsync(ERPNextSalesInvoiceRequest request)
    {
        // Step 1: create as draft
        var body = new
        {
            customer      = request.Customer,
            update_stock  = 1,          // deducts stock on submit — same as POS
            po_no         = request.PoNo,
            remarks       = request.Remarks,
            items         = request.Items.Select(i => new
            {
                item_code = i.ItemCode,
                warehouse = i.Warehouse,
                qty       = i.Qty,
                rate      = i.Rate,
            }).ToArray()
        };

        var createResp = await _http.PostAsJsonAsync("/api/resource/Sales Invoice", body, _json);
        if (!createResp.IsSuccessStatusCode)
        {
            var err = await createResp.Content.ReadAsStringAsync();
            throw new ERPNextException($"Failed to create Sales Invoice: {createResp.StatusCode} — {err}");
        }

        var created = await createResp.Content.ReadFromJsonAsync<ERPNextSingleResponse<ERPNextNamedDocument>>(_json)
            ?? throw new InvalidOperationException("Empty ERPNext response when creating Sales Invoice.");
        var invoiceName = created.Data.Name;

        // Step 2: submit (sets docstatus = 1, triggers stock deduction)
        var submitUrl = $"/api/resource/Sales Invoice/{Uri.EscapeDataString(invoiceName)}";
        var submitResp = await _http.PutAsJsonAsync(submitUrl, new { docstatus = 1 }, _json);
        if (!submitResp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Sales Invoice {Invoice} created but submit failed: {Status}",
                invoiceName, submitResp.StatusCode);
        }

        return invoiceName;
    }

    // ─── Item writes (disable / create) ──────────────────────────────────────

    public async Task<bool> SetItemDisabledAsync(string itemCode, bool disabled)
    {
        var url = $"/api/resource/Item/{Uri.EscapeDataString(itemCode)}";
        var resp = await _http.PutAsJsonAsync(url, new { disabled = disabled ? 1 : 0 }, _json);
        if (resp.IsSuccessStatusCode) return true;

        var err = await resp.Content.ReadAsStringAsync();
        _logger.LogWarning("Failed to set disabled={Disabled} on item {Item}: {Status} — {Err}",
            disabled, itemCode, resp.StatusCode, Truncate(err));
        return false;
    }

    public async Task<(bool Created, string? Error)> CreateItemAsync(ERPNextNewItemRequest request)
    {
        var body = new
        {
            item_code      = request.ItemCode,
            item_name      = string.IsNullOrWhiteSpace(request.ItemName) ? request.ItemCode : request.ItemName,
            item_group     = request.ItemGroup,
            stock_uom      = request.StockUom,
            is_stock_item  = 1,
            is_sales_item  = 1,
            standard_rate  = request.StandardRate,
            description    = request.Description ?? string.Empty,
            disabled       = 0
        };

        var resp = await _http.PostAsJsonAsync("/api/resource/Item", body, _json);
        if (resp.IsSuccessStatusCode) return (true, null);

        var err = await resp.Content.ReadAsStringAsync();

        // ERPNext returns 409 Conflict (DuplicateEntryError) when the item already exists.
        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict ||
            err.Contains("DuplicateEntry", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null); // already present — not an error
        }

        return (false, $"{(int)resp.StatusCode} {resp.StatusCode}: {Truncate(err)}");
    }

    private static string Truncate(string s, int max = 400) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max] + "…");
}

public class ERPNextException : Exception
{
    public ERPNextException(string message) : base(message) { }
    public ERPNextException(string message, Exception inner) : base(message, inner) { }
}
