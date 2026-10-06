using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.Marketing;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>
/// The Meta Conversions API payload. Meta hashes the same values on its side and compares digests, so a
/// value normalised even slightly differently from their rules simply fails to match and the sale goes
/// unattributed — silently. These pin the normalisation, the hashing, and the event id that keeps the
/// server copy from being counted as a second purchase alongside the browser pixel.
/// </summary>
public class MetaConversionsApiTests
{
    // ── normalisation ────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("  Buyer@Example.COM ", "buyer@example.com")]
    [InlineData("already@lower.com", "already@lower.com")]
    public void Email_is_trimmed_and_lowercased(string input, string expected)
        => Assert.Equal(expected, MetaCapi.NormalizeEmail(input));

    [Theory]
    // Nigerian numbers are typed locally with a trunk 0; Meta holds them internationally, so the 0 has
    // to become the 234 country code or the customer never matches.
    [InlineData("08012345678", "2348012345678")]
    [InlineData("0801 234 5678", "2348012345678")]
    [InlineData("+234 801 234 5678", "2348012345678")]
    [InlineData("2348012345678", "2348012345678")]
    [InlineData("8012345678", "2348012345678")]   // trunk 0 omitted
    [InlineData("+44 20 7946 0958", "442079460958")] // other countries pass through as dialled
    public void Phone_becomes_international_digits_only(string input, string expected)
        => Assert.Equal(expected, MetaCapi.NormalizePhone(input));

    [Theory]
    [InlineData("Nigeria", "ng")]
    [InlineData("NG", "ng")]
    [InlineData("  nigeria  ", "ng")]
    [InlineData("GB", "gb")]
    [InlineData("United Kingdom", null)] // not a 2-letter code; better to send nothing than a bad match
    public void Country_becomes_a_two_letter_code(string input, string? expected)
        => Assert.Equal(expected, MetaCapi.NormalizeCountry(input));

    [Theory]
    [InlineData("Lekki Phase 1", "lekkiphase1")]
    [InlineData("  Ikeja  ", "ikeja")]
    [InlineData("!!!", null)]
    public void Place_keeps_only_letters_and_digits(string input, string? expected)
        => Assert.Equal(expected, MetaCapi.NormalizePlace(input));

    [Theory]
    [InlineData("O'Brien", "obrien")]
    [InlineData("  Rapheal ", "rapheal")]
    public void Name_drops_punctuation_and_lowercases(string input, string expected)
        => Assert.Equal(expected, MetaCapi.NormalizeName(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_values_hash_to_nothing_rather_than_a_digest_of_empty(string? input)
        => Assert.Null(MetaCapi.Hash(input));

    [Fact]
    public void Hash_is_lowercase_hex_sha256()
    {
        // Known vector: SHA-256("abc"). A wrong casing or encoding here breaks every match silently.
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            MetaCapi.Hash("abc"));
    }

    [Fact]
    public void SplitName_prefers_what_the_customer_typed_on_the_order()
    {
        Assert.Equal(("rapheal", "akintan"), Lower(MetaCapi.SplitName("Rapheal Akintan", "Acct", "Name")));
        // Multi-word surnames stay whole.
        Assert.Equal(("ada", "nwosu obi"), Lower(MetaCapi.SplitName("Ada Nwosu Obi", null, null)));
        // Nothing on the order → fall back to the account.
        Assert.Equal(("acct", "name"), Lower(MetaCapi.SplitName(null, "Acct", "Name")));

        static (string?, string?) Lower((string? a, string? b) t) => (t.a?.ToLowerInvariant(), t.b?.ToLowerInvariant());
    }

    // ── the dedup contract ───────────────────────────────────────────────────────────────────────
    [Fact]
    public void The_event_id_is_deterministic_for_an_order()
    {
        // The browser pixel builds this same string in Confirmation.cshtml. If the two ever diverge,
        // Meta counts one sale as two and the reported revenue doubles.
        Assert.Equal("purchase-SG-1001", MetaCapi.PurchaseEventId("SG-1001"));
        Assert.Equal(MetaCapi.PurchaseEventId("SG-1001"), MetaCapi.PurchaseEventId(" SG-1001 "));
    }

    [Theory]
    [InlineData("1234567890123456", true)]
    [InlineData("12345678", true)]
    [InlineData("1234567", false)]        // too short
    [InlineData("12345678a", false)]      // not numeric
    [InlineData("", false)]
    public void Pixel_ids_are_validated_before_use(string id, bool ok)
        => Assert.Equal(ok, MetaCapi.LooksLikePixelId(id));

    // ── payload ──────────────────────────────────────────────────────────────────────────────────
    private static JsonElement Payload(string testCode = "")
    {
        var order = new Order
        {
            OrderNumber = "SG-2001",
            Currency = "NGN",
            Total = 52500m,
            UserId = "user-123",
            CreatedAt = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc),
            PaidAt = new DateTime(2026, 10, 5, 10, 30, 0, DateTimeKind.Utc),
            User = new ApplicationUser { Email = "Buyer@Example.com", FirstName = "Acct", LastName = "Name" },
            DeliveryAddress = new Address
            {
                FullName = "Rapheal Akintan", Phone = "08012345678",
                Line1 = "1 Ikota Lane", City = "Lekki", State = "Lagos", Country = "Nigeria"
            },
            Items = new List<OrderItem>
            {
                new() { ProductId = 11, ProductName = "Glitz Bangle", Quantity = 2, UnitPrice = 20000m },
                new() { ProductId = 12, ProductName = "Ball Choker",  Quantity = 1, UnitPrice = 12500m }
            }
        };

        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("sterlinglams.com");
        http.Request.Path = "/Checkout/Confirmation";
        http.Request.Headers.UserAgent = "Mozilla/5.0 (test)";
        http.Request.Headers.Cookie = "_fbp=fb.1.123.456; _fbc=fb.1.123.abc";
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("102.89.1.1");

        var body = MetaConversionsApi.BuildPurchase(order, http, testCode);
        return JsonDocument.Parse(JsonSerializer.Serialize(body)).RootElement;
    }

    [Fact]
    public void Purchase_carries_the_value_currency_and_contents_meta_needs()
    {
        var ev = Payload().GetProperty("data")[0];
        Assert.Equal("Purchase", ev.GetProperty("event_name").GetString());
        Assert.Equal("website", ev.GetProperty("action_source").GetString());
        Assert.Equal("purchase-SG-2001", ev.GetProperty("event_id").GetString());
        Assert.Equal("https://sterlinglams.com/Checkout/Confirmation", ev.GetProperty("event_source_url").GetString());

        var custom = ev.GetProperty("custom_data");
        Assert.Equal("NGN", custom.GetProperty("currency").GetString());
        Assert.Equal(52500d, custom.GetProperty("value").GetDouble());
        Assert.Equal(3, custom.GetProperty("num_items").GetInt32());
        Assert.Equal("SG-2001", custom.GetProperty("order_id").GetString());
        Assert.Equal(2, custom.GetProperty("contents").GetArrayLength());
    }

    [Fact]
    public void The_event_is_dated_by_when_the_money_landed_not_when_the_page_was_rendered()
    {
        // A refresh days later must not re-date the sale; Meta also rejects events over 7 days old.
        var ev = Payload().GetProperty("data")[0];
        var paid = new DateTimeOffset(new DateTime(2026, 10, 5, 10, 30, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
        Assert.Equal(paid, ev.GetProperty("event_time").GetInt64());
    }

    [Fact]
    public void Identifiers_are_hashed_and_no_raw_pii_is_ever_sent()
    {
        var root = Payload();
        var raw = root.GetRawText();

        // Nothing identifying may appear in clear text.
        Assert.DoesNotContain("buyer@example.com", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Buyer@Example.com", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("08012345678", raw);
        Assert.DoesNotContain("2348012345678", raw);
        Assert.DoesNotContain("Rapheal", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Akintan", raw, StringComparison.OrdinalIgnoreCase);

        var user = root.GetProperty("data")[0].GetProperty("user_data");
        Assert.Equal(MetaCapi.Hash("buyer@example.com"), user.GetProperty("em")[0].GetString());
        Assert.Equal(MetaCapi.Hash("2348012345678"), user.GetProperty("ph")[0].GetString());
        Assert.Equal(MetaCapi.Hash("rapheal"), user.GetProperty("fn")[0].GetString());
        Assert.Equal(MetaCapi.Hash("akintan"), user.GetProperty("ln")[0].GetString());
        Assert.Equal(MetaCapi.Hash("lekki"), user.GetProperty("ct")[0].GetString());
        Assert.Equal(MetaCapi.Hash("ng"), user.GetProperty("country")[0].GetString());
        Assert.Equal(MetaCapi.Hash("user-123"), user.GetProperty("external_id")[0].GetString());
    }

    [Fact]
    public void Click_identifiers_are_sent_unhashed_because_meta_requires_that()
    {
        var user = Payload().GetProperty("data")[0].GetProperty("user_data");
        Assert.Equal("fb.1.123.456", user.GetProperty("fbp").GetString());
        Assert.Equal("fb.1.123.abc", user.GetProperty("fbc").GetString());
        Assert.Equal("102.89.1.1", user.GetProperty("client_ip_address").GetString());
        Assert.Equal("Mozilla/5.0 (test)", user.GetProperty("client_user_agent").GetString());
    }

    [Fact]
    public void A_test_event_code_is_only_attached_when_one_is_set()
    {
        // Left on by accident it would divert real sales into the Test Events stream, where they are
        // never counted as conversions.
        Assert.False(Payload().TryGetProperty("test_event_code", out _));
        Assert.Equal("TEST12345", Payload("TEST12345").GetProperty("test_event_code").GetString());
    }

    [Fact]
    public void An_order_with_no_address_still_produces_a_valid_event()
    {
        // Pickup orders have no delivery address; the sale must still be reported, just with fewer
        // match keys, rather than throwing on the confirmation page.
        var order = new Order
        {
            OrderNumber = "SG-3001", Currency = "NGN", Total = 1000m, UserId = "",
            CreatedAt = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc),
            Items = new List<OrderItem> { new() { ProductId = 7, Quantity = 1, UnitPrice = 1000m } }
        };
        var body = MetaConversionsApi.BuildPurchase(order, new DefaultHttpContext(), "");
        var ev = JsonDocument.Parse(JsonSerializer.Serialize(body)).RootElement.GetProperty("data")[0];

        Assert.Equal("purchase-SG-3001", ev.GetProperty("event_id").GetString());
        Assert.Equal(1000d, ev.GetProperty("custom_data").GetProperty("value").GetDouble());
        Assert.False(ev.GetProperty("user_data").TryGetProperty("em", out _));
    }
}
