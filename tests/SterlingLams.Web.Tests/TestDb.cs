using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Tests;

/// <summary>
/// A throwaway ApplicationDbContext backed by a private SQLite in-memory database (relational,
/// supports the real transactions the services use). The connection is held open so the schema
/// survives for the test's lifetime. Provides small seeding helpers.
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _conn;
    public ApplicationDbContext Db { get; }

    public TestDb()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_conn)
            .Options;
        Db = new ApplicationDbContext(options);
        Db.Database.EnsureCreated();
    }

    public ApplicationUser SeedUser()
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = $"u{Guid.NewGuid():N}@test.local",
            Email = "buyer@test.local",
            FirstName = "Test",
            LastName = "Buyer"
        };
        Db.Users.Add(user);
        Db.SaveChanges();
        return user;
    }

    public Store SeedStore(string name, string state, string city)
    {
        var store = new Store { Name = name, Slug = name.ToLowerInvariant().Replace(' ', '-'), State = state, City = city, Address = name, IsActive = true };
        Db.Stores.Add(store);
        Db.SaveChanges();
        return store;
    }

    /// <summary>Seeds the three real branches and returns (Abuja, Allen/Ikeja, Ikota/Ajah).</summary>
    public (Store abuja, Store allen, Store ikota) SeedBranches()
    {
        var abuja = SeedStore("Abuja", "Abuja", "Gwarimpa");
        var allen = SeedStore("Allen", "Lagos", "Ikeja");
        var ikota = SeedStore("Ikota", "Lagos", "Ajah");
        return (abuja, allen, ikota);
    }

    public Product SeedProduct(decimal price = 1000m, string? externalCode = null)
    {
        var p = new Product
        {
            Name = "Test Product " + Guid.NewGuid().ToString("N")[..6],
            Slug = "p-" + Guid.NewGuid().ToString("N")[..8],
            Price = price,
            ExternalCode = externalCode ?? "",
            ProductType = "simple",
            IsActive = true,
            CategoryId = EnsureCategory()
        };
        Db.Products.Add(p);
        Db.SaveChanges();
        return p;
    }

    private int? _categoryId;
    private int EnsureCategory()
    {
        if (_categoryId.HasValue) return _categoryId.Value;
        var c = new Category { Name = "Test", Slug = "test", IsActive = true };
        Db.Categories.Add(c);
        Db.SaveChanges();
        _categoryId = c.Id;
        return c.Id;
    }

    public void SetStock(int productId, int storeId, int onHand, int reserved = 0)
    {
        var inv = Db.StoreInventories.FirstOrDefault(si => si.ProductId == productId && si.StoreId == storeId);
        if (inv == null)
        {
            inv = new StoreInventory { ProductId = productId, StoreId = storeId };
            Db.StoreInventories.Add(inv);
        }
        inv.QuantityOnHand = onHand;
        inv.QuantityReserved = reserved;
        Db.SaveChanges();
    }

    public StoreInventory Inv(int productId, int storeId) =>
        Db.StoreInventories.AsNoTracking().First(si => si.ProductId == productId && si.StoreId == storeId);

    /// <summary>Builds and saves an unpaid online delivery order for the given product quantities.</summary>
    public Order NewDeliveryOrder(ApplicationUser user, string state, string city, params (Product product, int qty)[] lines)
    {
        var order = new Order
        {
            OrderNumber = "T-" + Guid.NewGuid().ToString("N")[..10],
            UserId = user.Id,
            Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.Delivery,
            Status = OrderStatus.Confirmed,
            Currency = "NGN",
            Subtotal = lines.Sum(l => l.product.Price * l.qty),
            Total = lines.Sum(l => l.product.Price * l.qty),
            DeliveryAddress = new Address
            {
                UserId = user.Id, FullName = user.FullName, Phone = "08000000000",
                Line1 = "1 Test St", City = city, State = state, Country = "Nigeria"
            },
            Items = lines.Select(l => new OrderItem
            {
                ProductId = l.product.Id, ProductName = l.product.Name, Quantity = l.qty, UnitPrice = l.product.Price
            }).ToList()
        };
        Db.Orders.Add(order);
        Db.SaveChanges();
        return order;
    }

    public void Dispose()
    {
        Db.Dispose();
        _conn.Dispose();
    }
}
