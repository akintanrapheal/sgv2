using System.Text.Json;

namespace SterlingLams.Web.Services;

public enum DeliveryZone { Lagos, Abuja, Oyo, National }

/// <summary>Fulfilment region an order is routed to. North → the Abuja branch; South → the southern
/// branches (Allen, Ikota, Ibadan). The set of states counted as "North" is editable in
/// Admin → Delivery Zones (setting <c>fulfilment.north_states</c>); everything else is South.</summary>
public enum StoreRegion { North, South }

public class DeliveryOption
{
    public string Type { get; set; } = "Standard";   // "Express" | "Standard"
    public string Label { get; set; } = string.Empty;
    public decimal Fee { get; set; }
    public string Timeframe { get; set; } = string.Empty;
    public string FormattedFee => Fee == 0 ? "Free" : $"₦{Fee:N0}";
}

/// <summary>
/// One distance band within a state (e.g. "Island (VI / Ikoyi)"). Holds its own Standard + Express
/// fees/timeframes and the list of areas it covers. Editable in Admin → Delivery Zones and stored as
/// the <c>shipping.delivery_zones</c> JSON setting.
/// </summary>
public class DeliveryZoneDef
{
    public string State { get; set; } = "Lagos";      // "Lagos" | "Abuja"
    public string Name { get; set; } = "";
    public decimal StandardFee { get; set; }
    public decimal ExpressFee { get; set; }
    public string StandardDays { get; set; } = "2 - 4 working days";
    public string ExpressDays { get; set; } = "24 - 48 hours";
    public List<string> Areas { get; set; } = new();
}

public class DeliveryZoneService
{
    private readonly ISettingsService _settings;
    public DeliveryZoneService(ISettingsService settings) => _settings = settings;

    // ── Zone detection (state level, still used for fulfilment/branch ranking) ──
    public static DeliveryZone GetZone(string state)
    {
        if (string.IsNullOrWhiteSpace(state)) return DeliveryZone.National;
        var s = state.Trim();

        if (s.Equals("Lagos", StringComparison.OrdinalIgnoreCase) ||
            LagosLGAs.Any(lga => lga.Equals(s, StringComparison.OrdinalIgnoreCase)))
            return DeliveryZone.Lagos;

        if (s.Equals("FCT", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Abuja", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Federal Capital", StringComparison.OrdinalIgnoreCase))
            return DeliveryZone.Abuja;

        if (s.Equals("Oyo", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Ibadan", StringComparison.OrdinalIgnoreCase))
            return DeliveryZone.Oyo;

        return DeliveryZone.National;
    }

    private static string StateKey(string state) => GetZone(state) switch
    {
        DeliveryZone.Lagos => "Lagos",
        DeliveryZone.Abuja => "Abuja",
        DeliveryZone.Oyo => "Oyo",
        _ => "National"
    };

    // ── Fulfilment region (North → Abuja branch, South → Allen/Ikota/Ibadan) ────
    // Built-in default: the far North (North-West + North-East) plus FCT/Abuja route to the Abuja store.
    // Everything else (incl. the North-Central middle belt and the whole South) is South. The admin can
    // move states between the two groups in Admin → Delivery Zones (setting fulfilment.north_states).
    public static readonly string[] DefaultNorthStates =
    {
        "FCT (Abuja)",
        // North-West
        "Kaduna", "Kano", "Katsina", "Kebbi", "Sokoto", "Jigawa", "Zamfara",
        // North-East
        "Adamawa", "Bauchi", "Borno", "Gombe", "Taraba", "Yobe",
    };

    /// <summary>Parses the stored fulfilment.north_states JSON (an array of state names). A blank or
    /// unparseable value falls back to <see cref="DefaultNorthStates"/>; an explicit empty array is
    /// honoured (every state then routes South).</summary>
    public static List<string> ParseNorthStates(string? json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed != null)
                    return parsed.Select(s => (s ?? "").Trim()).Where(s => s.Length > 0).ToList();
            }
            catch { /* fall back to the default below */ }
        }
        return DefaultNorthStates.ToList();
    }

    /// <summary>The admin-editable set of states that route to the North (Abuja) branch.</summary>
    public async Task<List<string>> GetNorthStatesAsync()
        => ParseNorthStates(await _settings.GetAsync("fulfilment.north_states", ""));

    /// <summary>Which fulfilment region a state belongs to, given the North state set. Tolerates the
    /// FCT/Abuja name variants ("FCT", "Abuja", "FCT (Abuja)", "Federal Capital Territory").</summary>
    public static StoreRegion GetRegion(string? state, IEnumerable<string> northStates)
    {
        var s = (state ?? "").Trim();
        if (s.Length == 0) return StoreRegion.South; // unknown → the larger southern group
        foreach (var n in northStates)
            if (StateMatches(s, n)) return StoreRegion.North;
        return StoreRegion.South;
    }

    private static bool IsFct(string s) =>
        s.Contains("Abuja", StringComparison.OrdinalIgnoreCase)
        || s.Equals("FCT", StringComparison.OrdinalIgnoreCase)
        || s.Contains("FCT ", StringComparison.OrdinalIgnoreCase)
        || s.Contains("Federal Capital", StringComparison.OrdinalIgnoreCase);

    private static bool StateMatches(string a, string b)
    {
        a = a.Trim(); b = b.Trim();
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        return IsFct(a) && IsFct(b); // "FCT (Abuja)" ≡ "FCT" ≡ "Abuja"
    }

    // ── Zone config (admin-editable JSON, with a sensible built-in default) ────
    public async Task<List<DeliveryZoneDef>> GetZonesAsync()
    {
        var json = await _settings.GetAsync("shipping.delivery_zones", "");
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<DeliveryZoneDef>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed is { Count: > 0 }) return parsed;
            }
            catch { /* fall back to the default below */ }
        }
        return DefaultZones();
    }

    /// <summary>Finds the zone covering <paramref name="area"/> within the state; else the state's
    /// first (default) zone. Returns null for states with no zones (National).</summary>
    private static DeliveryZoneDef? ResolveZone(List<DeliveryZoneDef> zones, string state, string? area)
    {
        var key = StateKey(state);
        if (key == "National") return null;
        var stateZones = zones.Where(z => string.Equals(z.State, key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (stateZones.Count == 0) return null;

        var a = (area ?? "").Trim();
        if (a.Length > 0)
        {
            var match = stateZones.FirstOrDefault(z => z.Areas.Any(x =>
                x.Trim().Equals(a, StringComparison.OrdinalIgnoreCase)
                || x.Trim().Contains(a, StringComparison.OrdinalIgnoreCase)
                || a.Contains(x.Trim(), StringComparison.OrdinalIgnoreCase)));
            if (match != null) return match;
        }
        return stateZones[0]; // area not listed → the state's first zone is the default fee
    }

    // ── Options for a state + (optional) area ─────────────────────────────────
    public async Task<List<DeliveryOption>> GetOptionsAsync(string state, string? area = null)
    {
        var zone = ResolveZone(await GetZonesAsync(), state, area);
        if (zone != null)
        {
            return new List<DeliveryOption>
            {
                new() { Type = "Express",  Label = "Express Delivery",  Fee = zone.ExpressFee,  Timeframe = zone.ExpressDays  },
                new() { Type = "Standard", Label = "Standard Delivery", Fee = zone.StandardFee, Timeframe = zone.StandardDays },
            };
        }

        var natFee  = await _settings.GetDecimalAsync("shipping.national_standard_fee", 7500);
        var natDays = await _settings.GetAsync("shipping.national_standard_days", "2 - 5 working days");
        return new List<DeliveryOption>
        {
            new() { Type = "Standard", Label = "Standard Delivery", Fee = natFee, Timeframe = natDays },
        };
    }

    // ── Calculate fee from state + area + type (server-side, at order placement) ─
    public async Task<decimal> CalculateFeeAsync(string state, string? area, string deliveryType)
    {
        var options = await GetOptionsAsync(state, area);
        var match = options.FirstOrDefault(o => o.Type.Equals(deliveryType, StringComparison.OrdinalIgnoreCase))
                 ?? options.First();
        return match.Fee;
    }

    // ── Rank branches by proximity to a customer (for online fulfilment) ──────
    public static List<Models.Domain.Store> RankStoresByProximity(
        IEnumerable<Models.Domain.Store> stores, string? customerState, string? customerCity)
        => RankStoresByProximity(stores, customerState, customerCity, DefaultNorthStates);

    /// <summary>Ranks branches for fulfilling a customer's order: same fulfilment region first (so a
    /// northern order prefers Abuja and a southern order prefers Allen/Ikota/Ibadan — the other region
    /// is only a last-resort tail), then same fine zone (Lagos/Oyo/Abuja), then a city match, then Id.
    /// Stock isn't considered here; the caller picks the branch that actually has the items.</summary>
    public static List<Models.Domain.Store> RankStoresByProximity(
        IEnumerable<Models.Domain.Store> stores, string? customerState, string? customerCity,
        IEnumerable<string> northStates)
    {
        var north = northStates as ICollection<string> ?? northStates.ToList();
        var customerRegion = GetRegion(customerState, north);
        var customerZone = GetZone(customerState ?? "");
        var city = (customerCity ?? "").Trim();

        bool CityMatches(Models.Domain.Store s)
        {
            if (city.Length == 0 || string.IsNullOrWhiteSpace(s.City)) return false;
            var sc = s.City.Trim();
            return sc.Contains(city, StringComparison.OrdinalIgnoreCase)
                || city.Contains(sc, StringComparison.OrdinalIgnoreCase);
        }

        return stores
            .OrderBy(s => GetRegion(s.State, north) == customerRegion ? 0 : 1) // region group first
            .ThenBy(s => GetZone(s.State) == customerZone ? 0 : 1)
            .ThenBy(s => CityMatches(s) ? 0 : 1)
            .ThenBy(s => s.Id)
            .ToList();
    }

    // ── Default zones (starter grouping + prices; admin can fully edit) ────────
    // Farther bands cost more: Island / Outer-Mainland / Far ≈ ₦3,500 std, ₦5,500 express.
    public static List<DeliveryZoneDef> DefaultZones() => new()
    {
        new() { State = "Lagos", Name = "Lekki / Ajah axis", StandardFee = 2500, ExpressFee = 3500,
            Areas = new() { "Ajah", "Sangotedo", "Lekki", "Chevron", "Agungi", "Osapa", "Ikota", "Badore", "Awoyaya", "Ibeju-Lekki", "Jakande", "Ogombo", "Abraham Adesanya", "Lakowe" } },
        new() { State = "Lagos", Name = "Island (VI / Ikoyi)", StandardFee = 3500, ExpressFee = 5500,
            Areas = new() { "Victoria Island", "Ikoyi", "Lekki Phase 1", "Oniru", "Lagos Island", "Marina", "Obalende", "Eti-Osa" } },
        new() { State = "Lagos", Name = "Central Mainland", StandardFee = 3000, ExpressFee = 4500,
            Areas = new() { "Ikeja", "Yaba", "Surulere", "Gbagada", "Maryland", "Ketu", "Ojota", "Anthony Village", "Mushin", "Oshodi", "Isolo", "Ilupeju", "Ogudu", "Ogba", "Magodo", "Ojodu Berger", "Agidingbi", "Shomolu", "Bariga", "Palmgroove", "Alapere" } },
        new() { State = "Lagos", Name = "Outer Mainland", StandardFee = 3500, ExpressFee = 5500,
            Areas = new() { "Ikorodu", "Alimosho", "Iyana Ipaja", "Ipaja", "Ayobo", "Ikotun", "Egbe", "Idimu", "Igando", "Festac", "Amuwo-Odofin", "Mile 2", "Satellite Town", "Ojo", "Abule Egba", "Meiran", "Akute", "Agbado", "Ejigbo", "Okota", "Ago Palace", "Akowonjo", "Dopemu", "Ijegun", "Agege" } },
        new() { State = "Lagos", Name = "Far outskirts", StandardFee = 4000, ExpressFee = 6000,
            Areas = new() { "Epe", "Badagry", "Eredo", "Shapati", "Ibeju" } },

        new() { State = "Abuja", Name = "Gwarinpa / Life Camp axis", StandardFee = 2500, ExpressFee = 3500,
            Areas = new() { "Gwarinpa", "Life Camp", "Kado", "Katampe", "Jabi", "Utako", "Dawaki", "Kubwa Express" } },
        new() { State = "Abuja", Name = "City centre", StandardFee = 3000, ExpressFee = 4500,
            Areas = new() { "Central Business District", "CBD", "Wuse", "Wuse 2", "Maitama", "Asokoro", "Garki", "Garki 2", "Central Area", "Wuye", "Guzape" } },
        new() { State = "Abuja", Name = "Outer / satellite", StandardFee = 3500, ExpressFee = 5500,
            Areas = new() { "Lugbe", "Kubwa", "Nyanya", "Karu", "Mararaba", "Gwagwalada", "Kuje", "Bwari", "Dei-Dei", "Zuba", "Airport Road", "Lokogoma", "Apo", "Gudu", "Durumi", "Idu", "Karmo", "Jahi" } },

        new() { State = "Oyo", Name = "Ibadan metro", StandardFee = 2500, ExpressFee = 4000,
            Areas = new() { "Bodija", "Dugbe", "Mokola", "Ring Road", "Challenge", "Iwo Road", "Agodi", "Jericho", "Jericho GRA", "University of Ibadan", "UI", "Samonda", "Sango", "Bashorun", "Akobo", "Ojoo", "Apata", "Eleyele", "Oluyole", "Molete", "Gate", "Orita Challenge", "Monatan", "Ojurin", "Agbowo", "Poly Ibadan", "Yemetu", "Beere", "Oke-Ado", "Felele", "New Garage", "Idi-Ape", "Basorun", "Adamasingba" } },
        new() { State = "Oyo", Name = "Greater Oyo", StandardFee = 4000, ExpressFee = 6000,
            Areas = new() { "Oyo Town", "Ogbomoso", "Iseyin", "Saki", "Eruwa", "Igboora", "Lalupon", "Moniya", "Akinyele", "Egbeda", "Lanlate" } },
    };

    /// <summary>Areas for a state (Lagos/Abuja), flattened from its zones — for the checkout dropdown.</summary>
    public async Task<List<string>> AreasForStateAsync(string state)
    {
        var key = StateKey(state);
        return (await GetZonesAsync())
            .Where(z => string.Equals(z.State, key, StringComparison.OrdinalIgnoreCase))
            .SelectMany(z => z.Areas)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();
    }

    // ── All Nigerian states (for the dropdown) ────────────────────────────────
    public static readonly string[] NigerianStates =
    {
        "Lagos", "FCT (Abuja)",
        "Abia", "Adamawa", "Akwa Ibom", "Anambra", "Bauchi", "Bayelsa",
        "Benue", "Borno", "Cross River", "Delta", "Ebonyi", "Edo",
        "Ekiti", "Enugu", "Gombe", "Imo", "Jigawa", "Kaduna", "Kano",
        "Katsina", "Kebbi", "Kogi", "Kwara", "Nasarawa", "Niger",
        "Ogun", "Ondo", "Osun", "Oyo", "Plateau", "Rivers",
        "Sokoto", "Taraba", "Yobe", "Zamfara",
    };

    // ── All Lagos LGAs + popular areas (city autocomplete fallback) ───────────
    public static readonly string[] LagosLGAs =
    {
        "Agege", "Ajeromi-Ifelodun", "Alimosho", "Amuwo-Odofin", "Apapa",
        "Badagry", "Epe", "Eti-Osa", "Ibeju-Lekki", "Ifako-Ijaiye",
        "Ikeja", "Ikorodu", "Kosofe", "Lagos Island", "Lagos Mainland",
        "Mushin", "Ojo", "Oshodi-Isolo", "Shomolu", "Surulere",
        "Victoria Island", "Lekki", "Ajah", "Sangotedo", "Chevron",
        "Yaba", "Maryland", "Gbagada", "Magodo",
        "Ojodu Berger", "Ogba", "Agidingbi", "Oshodi", "Isolo",
        "Festac", "Mile 2", "Satellite Town", "Iganmu", "Orile",
        "Ogudu", "Alapere", "Ketu", "Mile 12", "Ojota",
        "Anthony Village", "Palmgroove", "Pedro", "Bariga", "Ilaje",
        "Badore", "Agungi", "Osapa", "Jakande", "Oke-Ira",
        "Awoyaya", "Shapati", "Ibeju", "Eredo", "Igando",
        "Ikotun", "Egbe", "Idimu", "Ijegun", "Egan",
        "Ejigbo", "Okota", "Ago Palace", "Akowonjo", "Dopemu",
        "Iyana Ipaja", "Pleasure", "Abule Egba", "Ipaja", "Ayobo",
        "Meiran", "Akute", "Ojokoro", "Agbado", "Ifako",
    };
}
