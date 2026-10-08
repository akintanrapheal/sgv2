using System.Net;
using System.Text;
using SterlingLams.Web.Infrastructure;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

/// <summary>
/// Builds the rich, WooCommerce-style order email body (order-details box, product table with
/// thumbnails, subtotal/shipping/total/payment rows, billing + shipping addresses). Returned HTML
/// is the inner content; IEmailService wraps it in the branded shell. Used by the real order
/// confirmation and the Email Customizer preview so both look identical.
/// </summary>
public static class OrderEmailTemplate
{
    public record Item(string Name, string? Variant, int Quantity, decimal LineTotal, string? ImageUrl, string? Sku = null);

    /// <summary>Replaces {order}, {date}, {name} tokens in the editable intro (HTML-encoded values).</summary>
    public static string ApplyPlaceholders(string intro, string orderNumber, DateTime date, string customerName)
        => WebUtility.HtmlEncode(intro ?? "")
            .Replace("{order}", $"<strong>{WebUtility.HtmlEncode(orderNumber)}</strong>")
            .Replace("{date}", WebUtility.HtmlEncode(date.ToString("MMMM d, yyyy")))
            .Replace("{name}", WebUtility.HtmlEncode(customerName));

    private const string Accent = "#b03a6e";   // muted pink for labels
    private const string Line   = "#e7e5e4";

    /// <summary>A small square product thumbnail for an email line (44px, Cloudinary-optimised when
    /// possible). Returns "" when there's no image. Pass an ABSOLUTE url (email clients need it).
    /// Shared by the order emails and the other transactional emails (abandoned cart, back-in-stock,
    /// transfer request) so the thumbnail looks identical everywhere.</summary>
    public static string Thumb(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return "";
        var src = Img.Cld(imageUrl, 96, 96) ?? imageUrl;   // 2× the 44px display; no-op on non-Cloudinary URLs
        return $@"<img src=""{src}"" width=""44"" height=""44"" alt="""" style=""width:44px;height:44px;object-fit:cover;border:1px solid {Line};border-radius:2px;vertical-align:middle;margin-right:10px;"" />";
    }

    /// <summary>A small labelled address/detail block (used by the branch dispatch email). Blank
    /// lines are dropped; returns "" when there's nothing to show.</summary>
    public static string AddressBlock(string label, IEnumerable<string?> lines)
    {
        var body = string.Join("<br/>", lines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => System.Net.WebUtility.HtmlEncode(l!.Trim())));
        if (body.Length == 0) return "";
        return $@"<div style=""margin:12px 0;font-size:13px;color:#44403c;line-height:1.5;"">
            <div style=""text-transform:uppercase;letter-spacing:.5px;font-size:11px;color:#9ca3af;margin-bottom:2px;"">{System.Net.WebUtility.HtmlEncode(label)}</div>{body}</div>";
    }

    /// <summary>
    /// Shared billing + shipping blocks for the order emails (customer confirmation, website admin alert,
    /// and the manual staff-confirmed alert) so they never drift. The customer PHONE comes from the
    /// delivery address or, for pickup orders (no address), the buyer's account — so the number always
    /// shows. For store pickup the shipping block + the returned label carry the branch's FULL address.
    /// </summary>
    public static (List<string> billing, List<string> shipping, string pickupLabel) AddressBlocksFor(
        Order order, string custName, string? customerEmail)
    {
        var a = order.DeliveryAddress;
        // Prefer this order's own captured contact — a shared account (customer-care shell email) must not
        // make every order show the same stale name/phone.
        if (!string.IsNullOrWhiteSpace(order.ContactName)) custName = order.ContactName!.Trim();
        var phone = !string.IsNullOrWhiteSpace(order.ContactPhone) ? order.ContactPhone!.Trim()
            : (!string.IsNullOrWhiteSpace(a?.Phone) ? a!.Phone
                : (order.User?.PhoneNumber ?? order.Customer?.PhoneNumber));

        var billing = new List<string> { custName };
        if (a != null)
        {
            billing.Add(a.Line1 + (string.IsNullOrWhiteSpace(a.Line2) ? "" : ", " + a.Line2));
            billing.Add($"{a.City}, {a.State}".Trim(' ', ','));
        }
        if (!string.IsNullOrWhiteSpace(phone)) billing.Add("Tel: " + phone);
        if (!string.IsNullOrWhiteSpace(customerEmail)) billing.Add(customerEmail!);

        var shipping = new List<string> { custName };
        if (!string.IsNullOrWhiteSpace(phone)) shipping.Add("Tel: " + phone);
        var pickupLabel = "";
        if (order.FulfillmentType == FulfillmentType.StorePickup)
        {
            var store = order.PickupStore?.Name ?? "our store";
            var storeAddr = order.PickupStore?.Address;
            shipping.Add("Pickup at " + store);
            if (!string.IsNullOrWhiteSpace(storeAddr)) shipping.Add(storeAddr!);
            pickupLabel = string.IsNullOrWhiteSpace(storeAddr) ? $"Store pickup — {store}" : $"Store pickup — {store}, {storeAddr}";
        }
        else if (a != null)
        {
            shipping.Add(a.Line1 + (string.IsNullOrWhiteSpace(a.Line2) ? "" : ", " + a.Line2));
            shipping.Add($"{a.City}, {a.State}".Trim(' ', ','));
        }
        return (billing, shipping, pickupLabel);
    }

    public static string Build(
        string heading,
        string introHtml,
        string orderNumber,
        DateTime orderDate,
        IReadOnlyList<Item> items,
        decimal subtotal,
        string shippingLabel,
        decimal total,
        string paymentMethod,
        IReadOnlyList<string> billingLines,
        IReadOnlyList<string> shippingLines,
        string currency = "₦")
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        string Money(decimal v) => $"{currency}{v:N2}";
        var dateText = orderDate.ToString("MMMM d, yyyy");

        var sb = new StringBuilder();

        // Heading + intro
        sb.Append($@"<h1 style=""font-size:22px;font-weight:bold;margin:0 0 12px;color:#1c1917;"">{E(heading)}</h1>");
        sb.Append($@"<p style=""margin:0 0 8px;color:#44403c;font-size:14px;line-height:1.6;"">{introHtml}</p>");

        // ORDER DETAILS divider
        sb.Append($@"
<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin:28px 0 12px;"">
  <tr>
    <td style=""border-bottom:1px solid {Line};""></td>
    <td style=""padding:0 12px;white-space:nowrap;font-size:11px;letter-spacing:1px;color:#78716c;"">ORDER DETAILS</td>
    <td style=""border-bottom:1px solid {Line};""></td>
  </tr>
</table>");

        // Order number + date
        sb.Append($@"
<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin:0 0 16px;font-size:14px;"">
  <tr>
    <td class=""em-stack"" style=""color:{Accent};font-weight:bold;"">Order Number: <span style=""color:#1c1917;font-weight:normal;"">{E(orderNumber)}</span></td>
    <td class=""em-stack"" align=""right"" style=""color:{Accent};font-weight:bold;padding-top:2px;"">Order Date: <span style=""color:#1c1917;font-weight:normal;"">{E(dateText)}</span></td>
  </tr>
</table>");

        // Product table
        sb.Append($@"
<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""font-size:13px;border-collapse:collapse;"">
  <tr style=""background:#f5f5f4;color:#78716c;font-size:11px;"">
    <td style=""padding:8px 10px;"">PRODUCT</td>
    <td style=""padding:8px 10px;"">QUANTITY</td>
    <td align=""right"" style=""padding:8px 10px;"">PRICE</td>
  </tr>");
        foreach (var it in items)
        {
            var img = Thumb(it.ImageUrl);
            var variant = string.IsNullOrWhiteSpace(it.Variant) ? "" : $@" <span style=""color:#78716c;"">- {E(it.Variant)}</span>";
            // SKU on its OWN line under the product name.
            var skuLine = string.IsNullOrWhiteSpace(it.Sku) ? "" : $@"<div style=""color:#a8a29e;font-size:12px;margin-top:3px;"">#{E(it.Sku)}</div>";
            var name = $@"<span style=""display:inline-block;vertical-align:middle;""><strong style=""color:#1c1917;"">{E(it.Name)}</strong>{variant}{skuLine}</span>";
            sb.Append($@"
  <tr style=""border-bottom:1px solid {Line};"">
    <td style=""padding:12px 10px;"">{img}{name}</td>
    <td style=""padding:12px 10px;color:#44403c;"">{it.Quantity}</td>
    <td align=""right"" style=""padding:12px 10px;white-space:nowrap;color:#1c1917;"">{Money(it.LineTotal)}</td>
  </tr>");
        }

        // Totals rows
        string Row(string label, string value, string bg) => $@"
  <tr style=""background:{bg};"">
    <td style=""padding:10px;font-weight:bold;color:#1c1917;"">{label}</td>
    <td colspan=""2"" align=""right"" style=""padding:10px;color:#1c1917;"">{value}</td>
  </tr>";
        sb.Append(Row("SUBTOTAL:", Money(subtotal), "#ffffff"));
        sb.Append(Row("SHIPPING:", E(shippingLabel), "#fafaf9"));
        sb.Append(Row("TOTAL:", $@"<strong>{Money(total)}</strong>", "#ffffff"));
        sb.Append(Row("PAYMENT METHOD:", E(paymentMethod), "#fafaf9"));
        sb.Append("</table>");

        // Billing + shipping addresses
        string Addr(string title, IReadOnlyList<string> lines)
        {
            var body = lines.Count == 0 ? "<span style=\"color:#a8a29e;\">—</span>"
                : string.Join("<br/>", lines.Select(E));
            return $@"<td class=""em-stack"" valign=""top"" width=""50%"" style=""padding:0 8px 8px;font-size:13px;color:#44403c;line-height:1.6;"">
                <p style=""margin:0 0 6px;font-weight:bold;color:#1c1917;"">{title}</p>{body}</td>";
        }
        sb.Append($@"
<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin-top:28px;"">
  <tr>{Addr("Billing address:", billingLines)}{Addr("Shipping address:", shippingLines)}</tr>
</table>");

        return sb.ToString();
    }

    /// <summary>
    /// Lighter status-update email body (Processing / Ready for pickup / Shipped / Delivered):
    /// heading + intro + a compact order-summary table + optional extra block/button. Shared by the
    /// real status emails (OrdersController) and the Email Customizer preview so both look identical.
    /// </summary>
    public static string BuildStatusUpdate(
        string heading,
        string introHtml,
        string orderNumber,
        IReadOnlyList<Item> items,
        decimal total,
        string currency = "₦",
        string? extraHtml = null,
        string? buttonLabel = null,
        string? buttonHref = null)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        string Money(decimal v) => $"{currency}{v:N0}";

        var sb = new StringBuilder();
        sb.Append($@"<h1 style=""font-size:22px;font-weight:bold;margin:0 0 12px;color:#1c1917;"">{E(heading)}</h1>");
        sb.Append($@"<p style=""margin:0 0 8px;color:#44403c;font-size:14px;line-height:1.6;"">{introHtml}</p>");
        if (!string.IsNullOrEmpty(extraHtml)) sb.Append(extraHtml);

        // ORDER divider
        sb.Append($@"
<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin:24px 0 8px;"">
  <tr>
    <td style=""border-bottom:1px solid {Line};""></td>
    <td style=""padding:0 12px;white-space:nowrap;font-size:11px;letter-spacing:1px;color:#78716c;"">ORDER {E(orderNumber)}</td>
    <td style=""border-bottom:1px solid {Line};""></td>
  </tr>
</table>");

        // Compact item list + total
        sb.Append($@"<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""font-size:14px;border-collapse:collapse;"">");
        foreach (var it in items)
        {
            var variant = string.IsNullOrWhiteSpace(it.Variant) ? "" : $@" <span style=""color:#78716c;"">({E(it.Variant)})</span>";
            // SKU on its OWN line under the product name.
            var skuLine = string.IsNullOrWhiteSpace(it.Sku) ? "" : $@"<div style=""color:#a8a29e;font-size:12px;margin-top:2px;"">#{E(it.Sku)}</div>";
            var nameCol = $@"<span style=""display:inline-block;vertical-align:middle;"">{E(it.Name)}{variant} &times; {it.Quantity}{skuLine}</span>";
            sb.Append($@"<tr><td style=""padding:8px 0;color:#374151;vertical-align:middle;"">{Thumb(it.ImageUrl)}{nameCol}</td><td align=""right"" style=""padding:8px 0;color:#111;vertical-align:middle;"">{Money(it.LineTotal)}</td></tr>");
        }
        sb.Append($@"<tr><td style=""padding-top:8px;border-top:1px solid {Line};font-weight:700;color:#1c1917;"">Total</td><td align=""right"" style=""padding-top:8px;border-top:1px solid {Line};font-weight:700;color:#1c1917;"">{Money(total)}</td></tr>");
        sb.Append("</table>");

        if (!string.IsNullOrWhiteSpace(buttonLabel) && !string.IsNullOrWhiteSpace(buttonHref))
            sb.Append($@"
<table role=""presentation"" cellpadding=""0"" cellspacing=""0"" style=""margin:24px 0 0;""><tr>
  <td style=""background:#ec1c8e;border-radius:2px;""><a href=""{buttonHref}"" style=""display:inline-block;padding:12px 28px;color:#fff;font-size:13px;letter-spacing:1px;text-transform:uppercase;text-decoration:none;"">{E(buttonLabel)}</a></td>
</tr></table>");

        return sb.ToString();
    }
}
