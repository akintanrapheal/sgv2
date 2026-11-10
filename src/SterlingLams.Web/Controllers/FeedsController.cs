using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Infrastructure.Feeds;

namespace SterlingLams.Web.Controllers;

/// <summary>
/// Shopping-channel product feeds, built from one shared engine (<see cref="IProductFeedService"/>):
///   • GET /feeds/google.xml — Google Merchant Center (Google Shopping + Performance Max)
///   • GET /feeds/meta.xml   — Meta/Facebook &amp; Instagram catalogue (shops + dynamic product ads)
/// Both are the Google RSS 2.0 shape (Meta consumes it too); they differ only in the availability token.
/// Public + cached an hour at the edge — the platforms re-fetch on their own schedule (usually daily).
/// </summary>
public class FeedsController : Controller
{
    private const string GoogleNs = "http://base.google.com/ns/1.0";

    private readonly IProductFeedService _feed;
    public FeedsController(IProductFeedService feed) => _feed = feed;

    [HttpGet("/feeds/google.xml")]
    public Task<IActionResult> Google(CancellationToken ct) => Render(FeedFlavor.Google, ct);

    [HttpGet("/feeds/meta.xml")]
    public Task<IActionResult> Meta(CancellationToken ct) => Render(FeedFlavor.Meta, ct);

    private enum FeedFlavor { Google, Meta }

    private async Task<IActionResult> Render(FeedFlavor flavor, CancellationToken ct)
    {
        var items = await _feed.BuildItemsAsync(ct);
        XNamespace g = GoogleNs;

        var channel = new XElement("channel",
            new XElement("title", "Sterlin Glams"),
            new XElement("link", Infrastructure.SeoUrl.Base),
            new XElement("description", "Sterlin Glams — jewellery & accessories product catalogue"));

        foreach (var it in items)
            channel.Add(BuildItem(it, g, flavor));

        var rss = new XElement("rss",
            new XAttribute("version", "2.0"),
            new XAttribute(XNamespace.Xmlns + "g", GoogleNs),
            channel);

        // XElement escapes all text/attribute content, so nothing here needs manual encoding.
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + rss;
        Response.Headers.CacheControl = "public, max-age=3600";
        return Content(xml, "application/xml", Encoding.UTF8);
    }

    private static XElement BuildItem(FeedItem it, XNamespace g, FeedFlavor flavor)
    {
        var avail = it.InStock
            ? (flavor == FeedFlavor.Meta ? "in stock" : "in_stock")
            : (flavor == FeedFlavor.Meta ? "out of stock" : "out_of_stock");

        var item = new XElement("item",
            new XElement(g + "id", it.Id),
            new XElement("title", it.Title),
            new XElement("description", it.Description),
            new XElement("link", it.Link),
            new XElement(g + "image_link", it.ImageLink),
            new XElement(g + "availability", avail),
            new XElement(g + "condition", it.Condition),
            new XElement(g + "price", Money(it.Price, it.Currency)),
            new XElement(g + "brand", it.Brand),
            new XElement(g + "google_product_category", it.GoogleProductCategory));

        foreach (var extra in it.AdditionalImageLinks)
            item.Add(new XElement(g + "additional_image_link", extra));

        if (it.SalePrice is decimal sale)
            item.Add(new XElement(g + "sale_price", Money(sale, it.Currency)));

        if (!string.IsNullOrWhiteSpace(it.Gtin)) item.Add(new XElement(g + "gtin", it.Gtin));
        if (!string.IsNullOrWhiteSpace(it.Mpn)) item.Add(new XElement(g + "mpn", it.Mpn));
        // Tell the platform not to expect a manufacturer identifier when we genuinely have none,
        // otherwise the item is flagged for a missing GTIN.
        if (string.IsNullOrWhiteSpace(it.Gtin) && string.IsNullOrWhiteSpace(it.Mpn))
            item.Add(new XElement(g + "identifier_exists", "no"));

        if (!string.IsNullOrWhiteSpace(it.ItemGroupId)) item.Add(new XElement(g + "item_group_id", it.ItemGroupId));
        if (!string.IsNullOrWhiteSpace(it.ProductType)) item.Add(new XElement(g + "product_type", it.ProductType));
        if (!string.IsNullOrWhiteSpace(it.Material)) item.Add(new XElement(g + "material", it.Material));

        return item;
    }

    private static string Money(decimal value, string currency) =>
        value.ToString("0.00", CultureInfo.InvariantCulture) + " " + currency;
}
