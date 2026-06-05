using System;
using System.Collections.Generic;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.ViewModels
{
    // ─── Dashboard ────────────────────────────────────────────────────────
    public class DashboardViewModel
    {
        public decimal RevenueToday { get; set; }
        public decimal RevenueThisMonth { get; set; }
        public int OrdersToday { get; set; }
        public int OrdersPending { get; set; }
        public int TotalProducts { get; set; }
        public int LowStockAlerts { get; set; }
        public List<RecentOrderRow> RecentOrders { get; set; } = new();
        public List<LowStockRow> LowStockItems { get; set; } = new();
    }

    public class RecentOrderRow
    {
        public string OrderNumber { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public decimal Total { get; set; }
        public string Status { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    public class LowStockRow
    {
        public string ProductName { get; set; } = "";
        public string StoreName { get; set; } = "";
        public int Quantity { get; set; }
    }

    // ─── Orders ───────────────────────────────────────────────────────────
    public class AdminOrderListViewModel
    {
        public List<AdminOrderRow> Orders { get; set; } = new();
        public string StatusFilter { get; set; } = "";
        public string SearchQuery { get; set; } = "";
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; } = 1;
    }

    public class AdminOrderRow
    {
        public int Id { get; set; }
        public string OrderNumber { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string CustomerEmail { get; set; } = "";
        public decimal Total { get; set; }
        public string Status { get; set; } = "";
        public bool IsPaid { get; set; }
        public string FulfillmentType { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    public class AdminOrderDetailViewModel
    {
        public Order Order { get; set; } = null!;
        public string CustomerName { get; set; } = "";
        public string CustomerEmail { get; set; } = "";
        public List<string> AvailableStatuses { get; set; } = new()
        {
            "Pending", "Confirmed", "Processing", "ReadyForPickup", "Shipped", "Delivered", "Cancelled"
        };
    }

    // ─── Products ─────────────────────────────────────────────────────────
    public class AdminProductListViewModel
    {
        public List<Product> Products { get; set; } = new();
        public string SearchQuery { get; set; } = "";
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; } = 1;
    }

    public class AdminProductEditViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Slug { get; set; } = "";
        public string Description { get; set; } = "";
        public decimal Price { get; set; }
        public string? Material { get; set; }
        public string? Carat { get; set; }
        public string? GemstoneType { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsFeatured { get; set; }
        public string ErpNextItemCode { get; set; } = string.Empty;
        public int? CategoryId { get; set; }
        public List<Category> Categories { get; set; } = new();
    }

    // ─── Inventory ────────────────────────────────────────────────────────
    public class AdminInventoryViewModel
    {
        public List<InventoryStoreSection> Stores { get; set; } = new();
        public DateTime? LastSyncedAt { get; set; }
    }

    public class InventoryStoreSection
    {
        public Store Store { get; set; } = null!;
        public List<InventoryProductRow> Products { get; set; } = new();
    }

    public class InventoryProductRow
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = "";
        public string Sku { get; set; } = "";
        public int QuantityOnHand { get; set; }
        public bool IsLowStock => QuantityOnHand < 3;
    }

    // ─── Stores ───────────────────────────────────────────────────────────
    public class AdminStoreEditViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Address { get; set; } = "";
        public string City { get; set; } = "";
        public string State { get; set; } = "";
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? OpeningHours { get; set; }
        public string ErpNextWarehouse { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
    }
}
