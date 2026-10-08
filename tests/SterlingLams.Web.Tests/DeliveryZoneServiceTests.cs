using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

public class DeliveryZoneServiceTests
{
    private static List<Store> Branches() => new()
    {
        new Store { Id = 1, Name = "Abuja", State = "Abuja", City = "Gwarimpa", IsActive = true },
        new Store { Id = 2, Name = "Allen", State = "Lagos",  City = "Ikeja",    IsActive = true },
        new Store { Id = 3, Name = "Ikota", State = "Lagos",  City = "Ajah",     IsActive = true },
    };

    [Fact]
    public void Lagos_Ikeja_customer_ranks_Allen_first()
    {
        var ranked = DeliveryZoneService.RankStoresByProximity(Branches(), "Lagos", "Ikeja");
        Assert.Equal("Allen", ranked[0].Name);     // same zone + city match
        Assert.Equal("Abuja", ranked[^1].Name);    // other zone last
    }

    [Fact]
    public void Lagos_Ajah_customer_ranks_Ikota_first()
    {
        var ranked = DeliveryZoneService.RankStoresByProximity(Branches(), "Lagos", "Ajah");
        Assert.Equal("Ikota", ranked[0].Name);
    }

    [Fact]
    public void Abuja_customer_ranks_Abuja_first()
    {
        var ranked = DeliveryZoneService.RankStoresByProximity(Branches(), "FCT (Abuja)", "Gwarimpa");
        Assert.Equal("Abuja", ranked[0].Name);
    }

    // Island areas (VI / Ikoyi / Lekki Phase 1 / Lagos Island) sit in a different sub-zone than Ikota's
    // Ajah, but they're on the same island axis — so they must still prefer Ikota over mainland Allen
    // (whose lower store id would otherwise win the tiebreaker). Owner directive 2026-10-08.
    [Theory]
    [InlineData("Victoria Island")]
    [InlineData("Ikoyi")]
    [InlineData("Lekki Phase 1")]
    [InlineData("Lagos Island")]
    public void Lagos_island_customer_ranks_Ikota_first(string area)
    {
        var ranked = DeliveryZoneService.RankStoresByProximity(Branches(), "Lagos", area);
        Assert.Equal("Ikota", ranked[0].Name);
        Assert.Equal("Abuja", ranked[^1].Name);   // other region still last
    }
}
