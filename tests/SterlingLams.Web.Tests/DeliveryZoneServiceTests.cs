using System.Threading.Tasks;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

public class DeliveryZoneServiceTests
{
    // Settings stub: every lookup returns the caller's default, so GetZonesAsync falls back to the built-in
    // DefaultZones and the nationwide fee is the ₦7,500 default — i.e. a clean, out-of-the-box config.
    private sealed class DefaultsSettings : ISettingsService
    {
        public Task<string> GetAsync(string key, string defaultValue = "") => Task.FromResult(defaultValue);
        public Task<bool> GetBoolAsync(string key, bool defaultValue = false) => Task.FromResult(defaultValue);
        public Task<decimal> GetDecimalAsync(string key, decimal defaultValue = 0) => Task.FromResult(defaultValue);
        public Task<int> GetIntAsync(string key, int defaultValue = 0) => Task.FromResult(defaultValue);
        public Task SaveManyAsync(System.Collections.Generic.Dictionary<string, string> values) => Task.CompletedTask;
        public Task<System.Collections.Generic.List<SiteSetting>> GetGroupAsync(string group) => Task.FromResult(new System.Collections.Generic.List<SiteSetting>());
        public Task<System.Collections.Generic.List<SiteSetting>> GetAllAsync() => Task.FromResult(new System.Collections.Generic.List<SiteSetting>());
        public void ClearCache() { }
    }

    // While the Ibadan storefront is closed, an Oyo/Ibadan order is a plain interstate order: the
    // nationwide Standard rate (₦7,500) and NO local Express or Same-Day option — exactly like any other
    // state. (Lagos/Abuja keep their local zones + Express.)
    [Theory]
    [InlineData("Oyo", "Bodija")]     // a listed Ibadan-metro area must NOT unlock local Oyo rates
    [InlineData("Oyo", null)]
    [InlineData("Ibadan", null)]
    public async Task Oyo_is_charged_the_nationwide_interstate_rate_with_no_express(string state, string? area)
    {
        var svc = new DeliveryZoneService(new DefaultsSettings());

        var options = await svc.GetOptionsAsync(state, area);
        Assert.Single(options);                                  // Standard only
        Assert.Equal("Standard", options[0].Type);
        Assert.Equal(7500m, options[0].Fee);                     // nationwide fee, not a local metro rate
        Assert.DoesNotContain(options, o => o.Type == "Express");

        Assert.Equal(7500m, await svc.CalculateFeeAsync(state, area, "Standard"));
        Assert.Equal(0m, await svc.SameDayFeeAsync(state, area)); // no local same-day for Oyo
    }

    [Fact]
    public async Task Lagos_keeps_its_local_zones_and_express()
    {
        var svc = new DeliveryZoneService(new DefaultsSettings());
        var options = await svc.GetOptionsAsync("Lagos", "Ikota");   // Lekki/Ajah axis
        Assert.Contains(options, o => o.Type == "Express");
        Assert.Equal(2500m, await svc.CalculateFeeAsync("Lagos", "Ikota", "Standard"));
    }

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
