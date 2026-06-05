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

    // ─── Sales Orders ────────────────────────────────────────────────────────

    public async Task<string> CreateSalesOrderAsync(ERPNextCreateSalesOrderRequest request)
    {
        var response = await _http.PostAsJsonAsync("/api/resource/Sales Order", request, _json);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ERPNextSingleResponse<ERPNextNamedDocument>>(_json)
            ?? throw new InvalidOperationException("Empty ERPNext response when creating Sales Order.");
        return result.Data.Name;
    }

    public async Task<bool> SubmitSalesOrderAsync(string salesOrderName)
    {
        var url = $"/api/resource/Sales Order/{Uri.EscapeDataString(salesOrderName)}";
        var body = new { docstatus = 1 };
        var response = await _http.PutAsJsonAsync(url, body, _json);
        return response.IsSuccessStatusCode;
    }

    // ─── Stock Entries ───────────────────────────────────────────────────────

    public async Task<string> CreateMaterialIssueAsync(List<ERPNextMaterialIssueItem> items, string? reference = null)
    {
        var body = new
        {
            stock_entry_type = "Material Issue",
            company = "Sterlin Glams",
            docstatus = 1,
            remarks = reference ?? "Web order",
            items = items.Select(i => new
            {
                item_code = i.ItemCode,
                s_warehouse = i.SourceWarehouse,
                qty = i.Qty,
                basic_rate = i.BasicRate
            }).ToArray()
        };

        var response = await _http.PostAsJsonAsync("/api/resource/Stock Entry", body);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ERPNextSingleResponse<ERPNextNamedDocument>>(_json)
            ?? throw new InvalidOperationException("Empty response from ERPNext when creating Stock Entry.");
        return result.Data.Name;
    }
}

public class ERPNextException : Exception
{
    public ERPNextException(string message) : base(message) { }
    public ERPNextException(string message, Exception inner) : base(message, inner) { }
}
