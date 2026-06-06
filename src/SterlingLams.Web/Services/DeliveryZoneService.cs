namespace SterlingLams.Web.Services;

public enum DeliveryZone { Lagos, Abuja, National }

public class DeliveryOption
{
    public string Type { get; set; } = "Standard";   // "Express" | "Standard"
    public string Label { get; set; } = string.Empty;
    public decimal Fee { get; set; }
    public string Timeframe { get; set; } = string.Empty;
    public string FormattedFee => Fee == 0 ? "Free" : $"₦{Fee:N0}";
}

public class DeliveryZoneService
{
    private readonly ISettingsService _settings;
    public DeliveryZoneService(ISettingsService settings) => _settings = settings;

    // ── Zone detection ────────────────────────────────────────────────────────
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

        return DeliveryZone.National;
    }

    // ── Options for a given state ─────────────────────────────────────────────
    public async Task<List<DeliveryOption>> GetOptionsAsync(string state)
    {
        var zone = GetZone(state);

        if (zone == DeliveryZone.Lagos || zone == DeliveryZone.Abuja)
        {
            var expressFee  = await _settings.GetDecimalAsync("shipping.lagos_abuja_express_fee",  4000);
            var expressDays = await _settings.GetAsync("shipping.lagos_abuja_express_days",        "24 - 48 hours");
            var stdFee      = await _settings.GetDecimalAsync("shipping.lagos_abuja_standard_fee", 2000);
            var stdDays     = await _settings.GetAsync("shipping.lagos_abuja_standard_days",       "2 - 4 working days");

            return new List<DeliveryOption>
            {
                new() { Type = "Express",  Label = "Express Delivery",  Fee = expressFee, Timeframe = expressDays },
                new() { Type = "Standard", Label = "Standard Delivery", Fee = stdFee,     Timeframe = stdDays     },
            };
        }

        var natFee  = await _settings.GetDecimalAsync("shipping.national_standard_fee",  7500);
        var natDays = await _settings.GetAsync("shipping.national_standard_days",        "2 - 5 working days");

        return new List<DeliveryOption>
        {
            new() { Type = "Standard", Label = "Standard Delivery", Fee = natFee, Timeframe = natDays },
        };
    }

    // ── Calculate fee from state + type (server-side, used at order placement) ─
    public async Task<decimal> CalculateFeeAsync(string state, string deliveryType)
    {
        var options = await GetOptionsAsync(state);
        var match = options.FirstOrDefault(o => o.Type.Equals(deliveryType, StringComparison.OrdinalIgnoreCase))
                 ?? options.First();
        return match.Fee;
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

    // ── All 20 Lagos LGAs (for city autocomplete when state = Lagos) ──────────
    public static readonly string[] LagosLGAs =
    {
        "Agege", "Ajeromi-Ifelodun", "Alimosho", "Amuwo-Odofin", "Apapa",
        "Badagry", "Epe", "Eti-Osa", "Ibeju-Lekki", "Ifako-Ijaiye",
        "Ikeja", "Ikorodu", "Kosofe", "Lagos Island", "Lagos Mainland",
        "Mushin", "Ojo", "Oshodi-Isolo", "Shomolu", "Surulere",
        // Popular areas (commonly used instead of LGA names)
        "Victoria Island", "Lekki", "Ajah", "Sangotedo", "Chevron",
        "Yaba", "Surulere", "Maryland", "Gbagada", "Magodo",
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
