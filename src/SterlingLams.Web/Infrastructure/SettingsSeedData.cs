using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Infrastructure;

public static class SettingsSeedData
{
    public static async Task SeedAsync(ApplicationDbContext db, ILogger logger)
    {
        var definitions = GetAllSettings();
        var existingKeys = await db.SiteSettings.Select(s => s.Key).ToListAsync();
        var toAdd = definitions.Where(d => !existingKeys.Contains(d.Key)).ToList();

        if (toAdd.Count > 0)
        {
            db.SiteSettings.AddRange(toAdd);
            await db.SaveChangesAsync();
            logger.LogInformation("Seeded {Count} site settings.", toAdd.Count);
        }

        // One-time migrations: update stale defaults / types
        var announcementColor = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == "announcement.bg_color");
        if (announcementColor != null && (announcementColor.Value == "bg-neutral-900" || string.IsNullOrWhiteSpace(announcementColor.Value)))
            announcementColor.Value = "bg-brand-500";

        // Ensure hero_image_url uses type "image" so the admin shows a file picker
        var heroImg = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == "homepage.hero_image_url");
        if (heroImg != null && heroImg.Type == "url")
            heroImg.Type = "image";

        await db.SaveChangesAsync();
    }

    private static List<SiteSetting> GetAllSettings() => new()
    {
        // ── General ──────────────────────────────────────────────────────────
        new() { Key = "general.site_name",       Group = "General", Label = "Site Name",          Type = "text",    Value = "Sterlin Glams",                                  Description = "Displayed in the browser tab and emails.",    SortOrder = 1 },
        new() { Key = "general.tagline",          Group = "General", Label = "Tagline",            Type = "text",    Value = "Luxury Jewellery. Timeless Elegance.",           Description = "Short brand tagline used in meta descriptions.", SortOrder = 2 },
        new() { Key = "general.contact_email",    Group = "General", Label = "Contact Email",      Type = "email",   Value = "info@sterlinglams.com",                          Description = "Displayed in the footer and contact page.",   SortOrder = 3 },
        new() { Key = "general.contact_phone",    Group = "General", Label = "Contact Phone",      Type = "tel",     Value = "+234 1 234 5678",                                Description = "Phone number shown to customers.",            SortOrder = 4 },
        new() { Key = "general.whatsapp_number",  Group = "General", Label = "WhatsApp Number",    Type = "tel",     Value = "",                                               Description = "Include country code, e.g. +2348012345678.",  SortOrder = 5 },
        new() { Key = "general.instagram_url",    Group = "General", Label = "Instagram URL",      Type = "url",     Value = "",                                               Description = "Full URL to your Instagram page.",            SortOrder = 6 },
        new() { Key = "general.facebook_url",     Group = "General", Label = "Facebook URL",       Type = "url",     Value = "",                                               Description = "Full URL to your Facebook page.",             SortOrder = 7 },
        new() { Key = "general.tiktok_url",       Group = "General", Label = "TikTok URL",         Type = "url",     Value = "",                                               Description = "Full URL to your TikTok profile.",            SortOrder = 8 },

        // ── Announcement Bar ─────────────────────────────────────────────────
        new() { Key = "announcement.enabled",     Group = "Announcement Bar", Label = "Show Announcement Bar",  Type = "boolean",  Value = "true",  Description = "Toggle the top banner on all pages.",                         SortOrder = 1 },
        new() { Key = "announcement.text",        Group = "Announcement Bar", Label = "Announcement Text",      Type = "text",     Value = "COMPLIMENTARY SHIPPING ON ORDERS OVER ₦150,000  |  IN-STORE PICKUP AVAILABLE", Description = "Text shown in the top banner. Keep it short.", SortOrder = 2 },
        new() { Key = "announcement.bg_color",    Group = "Announcement Bar", Label = "Background Colour",      Type = "text",     Value = "bg-brand-500",    Description = "Tailwind class: bg-brand-500, bg-neutral-900, bg-red-600, bg-emerald-700, etc.", SortOrder = 3 },

        // ── Shipping & Delivery ───────────────────────────────────────────────
        // Lagos & Abuja — Express
        new() { Key = "shipping.lagos_abuja_express_fee",  Group = "Shipping", Label = "Lagos & Abuja Express Fee (N)",         Type = "number", Value = "4000",               Description = "Express delivery fee for Lagos and Abuja FCT.",                SortOrder = 1 },
        new() { Key = "shipping.lagos_abuja_express_days", Group = "Shipping", Label = "Lagos & Abuja Express Timeframe",       Type = "text",   Value = "24 - 48 hours",      Description = "Timeframe shown to customers for express delivery.",           SortOrder = 2 },
        // Lagos & Abuja — Standard
        new() { Key = "shipping.lagos_abuja_standard_fee", Group = "Shipping", Label = "Lagos & Abuja Standard Fee (N)",        Type = "number", Value = "2000",               Description = "Standard delivery fee for Lagos and Abuja FCT.",               SortOrder = 3 },
        new() { Key = "shipping.lagos_abuja_standard_days",Group = "Shipping", Label = "Lagos & Abuja Standard Timeframe",      Type = "text",   Value = "2 - 4 working days", Description = "Timeframe shown to customers for standard delivery.",          SortOrder = 4 },
        // National — Standard
        new() { Key = "shipping.national_standard_fee",   Group = "Shipping", Label = "Nationwide Standard Fee (N)",           Type = "number", Value = "7500",               Description = "Delivery fee for all other Nigerian states.",                  SortOrder = 5 },
        new() { Key = "shipping.national_standard_days",  Group = "Shipping", Label = "Nationwide Standard Timeframe",         Type = "text",   Value = "2 - 5 working days", Description = "Timeframe shown to customers for nationwide standard delivery.", SortOrder = 6 },

        // ── Notifications ─────────────────────────────────────────────────────
        new() { Key = "notifications.admin_email",     Group = "Notifications", Label = "Admin Email",                    Type = "email",   Value = "rapheal@sterlinglamslogistics.com", Description = "Receives new order and low stock alerts.",         SortOrder = 1 },
        new() { Key = "notifications.new_order",       Group = "Notifications", Label = "New Order Alerts",               Type = "boolean", Value = "true",  Description = "Send email to admin when a new order is placed.",          SortOrder = 2 },
        new() { Key = "notifications.low_stock",       Group = "Notifications", Label = "Low Stock Alerts",               Type = "boolean", Value = "true",  Description = "Send email to admin when stock falls below threshold.",     SortOrder = 3 },
        new() { Key = "notifications.order_confirmed", Group = "Notifications", Label = "Customer Order Confirmation",    Type = "boolean", Value = "true",  Description = "Send order confirmation email to customer after payment.",  SortOrder = 4 },

        // ── Store Operations ──────────────────────────────────────────────────
        new() { Key = "store.accepting_orders",    Group = "Store", Label = "Accepting Orders",         Type = "boolean", Value = "true",  Description = "Turn off to temporarily stop customers from placing orders.",          SortOrder = 1 },
        new() { Key = "store.maintenance_mode",    Group = "Store", Label = "Maintenance Mode",         Type = "boolean", Value = "false", Description = "Shows a maintenance page to all visitors (admin still works).",        SortOrder = 2 },
        new() { Key = "store.out_of_stock_msg",   Group = "Store", Label = "Out of Stock Message",     Type = "text",    Value = "This item is currently out of stock. Check back soon.", Description = "Shown on product pages when stock is 0.", SortOrder = 3 },
        new() { Key = "store.pickup_available",    Group = "Store", Label = "In-Store Pickup Available",Type = "boolean", Value = "true",  Description = "Allow customers to choose store pickup at checkout.",                   SortOrder = 4 },
        new() { Key = "store.currency_symbol",     Group = "Store", Label = "Currency Symbol",          Type = "text",    Value = "N",     Description = "Symbol shown next to prices (e.g. N, $, PS).",                          SortOrder = 5 },

        // ── Homepage ──────────────────────────────────────────────────────────
        new() { Key = "homepage.hero_headline",    Group = "Homepage", Label = "Hero Headline",          Type = "text",    Value = "Timeless Elegance",                                    Description = "Large text on the homepage hero section.",                              SortOrder = 1 },
        new() { Key = "homepage.hero_subtext",     Group = "Homepage", Label = "Hero Subtext",           Type = "text",    Value = "Luxury jewellery crafted for the discerning woman. Discover our newest arrivals.", Description = "Smaller text below the hero headline.", SortOrder = 2 },
        new() { Key = "homepage.hero_cta",         Group = "Homepage", Label = "Hero Button Text",       Type = "text",    Value = "Shop Collection",                                      Description = "Text on the main call-to-action button.",                               SortOrder = 3 },
        new() { Key = "homepage.hero_image_url",   Group = "Homepage", Label = "Hero Campaign Image URL",Type = "image",   Value = "",                                                     Description = "Upload a campaign photo (or paste a URL). Replaces the hero gradient background. Recommended: full-width landscape, at least 1920×1080px.", SortOrder = 4 },
        new() { Key = "homepage.show_featured",    Group = "Homepage", Label = "Show Featured Section",  Type = "boolean", Value = "true",                                                 Description = "Show the Featured Pieces section.",                                     SortOrder = 5 },
        new() { Key = "homepage.featured_heading", Group = "Homepage", Label = "Featured Section Heading",Type = "text",  Value = "Featured Pieces",                                      Description = "Heading for the featured products section.",                             SortOrder = 6 },
        new() { Key = "homepage.store_banner_text",Group = "Homepage", Label = "Store Banner Subtext",   Type = "text",   Value = "Experience our jewellery in person at any of our three Lagos boutiques.", Description = "Text in the dark store-finder banner.",              SortOrder = 7 },
    };
}
