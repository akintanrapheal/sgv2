using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Models.ViewModels;
using SterlingLams.Web.Services;
using SterlingLams.Web.Services.Payment;
using Microsoft.EntityFrameworkCore;

namespace SterlingLams.Web.Controllers;

public class CheckoutController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPaymentService _payment;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<CheckoutController> _logger;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly SterlingLams.Web.Services.IOrderFulfilmentService _fulfilment;
    private readonly SterlingLams.Web.Services.ISettingsService _settings;
    private readonly SterlingLams.Web.Services.DeliveryZoneService _zones;
    private readonly SterlingLams.Web.Services.IDiscountService _discounts;
    private readonly SterlingLams.Web.Services.IEmailService _email;
    private readonly SterlingLams.Web.Services.IWhatsAppService _whatsapp;
    private readonly SterlingLams.Web.Services.ILoyaltyService _loyalty;
    private readonly SterlingLams.Web.Services.IGiftCardService _giftCards;
    private readonly SterlingLams.Web.Services.Logistics.ILogisticsDispatchService _logistics;
    private readonly SterlingLams.Web.Services.IStockService _stock;
    private readonly SterlingLams.Web.Services.IAuditService _audit;
    private readonly SterlingLams.Web.Services.IOrderNumberService _orderNumbers;
    private readonly SterlingLams.Web.Services.IZephielClient _zephiel;
    private readonly SterlingLams.Web.Services.IPostHogClient _posthog;
    private readonly SterlingLams.Web.Services.IRetainfulClient _retainful;
    private readonly IDataProtector _confirmTokenProtector;

    public CheckoutController(
        ApplicationDbContext db,
        IPaymentService payment,
        UserManager<ApplicationUser> userManager,
        ILogger<CheckoutController> logger,
        IConfiguration config,
        IWebHostEnvironment env,
        SterlingLams.Web.Services.IOrderFulfilmentService fulfilment,
        SterlingLams.Web.Services.ISettingsService settings,
        SterlingLams.Web.Services.DeliveryZoneService zones,
        SterlingLams.Web.Services.IDiscountService discounts,
        SterlingLams.Web.Services.IEmailService email,
        SterlingLams.Web.Services.IWhatsAppService whatsapp,
        SterlingLams.Web.Services.ILoyaltyService loyalty,
        SterlingLams.Web.Services.IGiftCardService giftCards,
        SterlingLams.Web.Services.Logistics.ILogisticsDispatchService logistics,
        SterlingLams.Web.Services.IStockService stock,
        SterlingLams.Web.Services.IAuditService audit,
        SterlingLams.Web.Services.IOrderNumberService orderNumbers,
        IDataProtectionProvider dataProtection,
        SterlingLams.Web.Services.IZephielClient zephiel,
        SterlingLams.Web.Services.IPostHogClient posthog,
        SterlingLams.Web.Services.IRetainfulClient retainful)
    {
        _db = db;
        _payment = payment;
        _userManager = userManager;
        _logger = logger;
        _config = config;
        _env = env;
        _fulfilment = fulfilment;
        _settings = settings;
        _zones = zones;
        _discounts = discounts;
        _email = email;
        _whatsapp = whatsapp;
        _loyalty = loyalty;
        _giftCards = giftCards;
        _logistics = logistics;
        _stock = stock;
        _audit = audit;
        _orderNumbers = orderNumbers;
        _zephiel = zephiel;
        _posthog = posthog;
        _retainful = retainful;
        _confirmTokenProtector = dataProtection.CreateProtector("Checkout.Confirmation.v1");
    }

    /// <summary>Opaque, tamper-proof token tying a viewer to a specific order's confirmation page —
    /// lets a guest (who isn't signed in) see their own confirmation without exposing every order
    /// to anyone who guesses an order number.</summary>
    private string ConfirmationToken(string orderNumber) => _confirmTokenProtector.Protect(orderNumber);

    /// <summary>Shared customer-care mailboxes that staff use to place orders on behalf of many different
    /// customers. These are exempt from the "an account already exists — please sign in" guard in guest
    /// checkout, so care can keep ordering with them even though they're registered accounts.</summary>
    private static readonly HashSet<string> CustomerCareEmails = new(StringComparer.OrdinalIgnoreCase)
    {
        "websitecustomer@gmail.com",
        "websitecare@gmail.com",
    };

    private bool ConfirmationTokenValid(string orderNumber, string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        try { return _confirmTokenProtector.Unprotect(token) == orderNumber; }
        catch { return false; }
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var cart = await GetCartAsync();
        if (cart.IsEmpty) return RedirectToAction("Index", "Cart");

        if (!await _settings.GetBoolAsync("store.accepting_orders", true))
        {
            TempData["Error"] = "We're not accepting online orders right now. Please check back soon.";
            return RedirectToAction("Index", "Cart");
        }

        var user = await _userManager.GetUserAsync(User);

        // Re-apply automatic promotion (in case the customer skipped the cart page)
        if (string.IsNullOrEmpty(cart.AppliedDiscountCode) || cart.IsAutomaticDiscount)
        {
            var auto = await _discounts.FindAutomaticAsync(cart, user?.Id);
            if (auto != null)
            {
                cart.AppliedDiscountCode = auto.Code;
                cart.DiscountDescription = auto.Description;
                cart.DiscountAmount      = auto.Amount;
                cart.FreeShipping        = auto.FreeShipping;
                cart.FreeShippingLagosAbujaOnly = auto.FreeShippingLagosAbujaOnly;
                cart.IsAutomaticDiscount = true;
                SaveCart(cart);
            }
        }

        var stores = await _db.Stores.Where(s => s.IsActive && s.IsPublic).ToListAsync();

        // Build delivery pricing JSON for client-side zone detection (incl. same-day eligibility for this cart)
        var pricingJson = await BuildDeliveryPricingJsonAsync(cart, stores);

        var vm = new CheckoutViewModel
        {
            Cart = cart,
            Subtotal = cart.Subtotal,
            DiscountAmount = cart.DiscountAmount,
            AppliedDiscountCode = cart.AppliedDiscountCode,
            DiscountDescription = cart.DiscountDescription,
            DeliveryFee = 0,   // updated client-side when delivery type selected
            DeliveryPricingJson = pricingJson,
            NigerianStates = SterlingLams.Web.Services.DeliveryZoneService.NigerianStates,
            LagosLGAs = SterlingLams.Web.Services.DeliveryZoneService.LagosLGAs,
            PaystackPublicKey = await _settings.GetAsync("payment.paystack.public_key", _config["Payment:Paystack:PublicKey"] ?? ""),
            PickupAvailable = await _settings.GetBoolAsync("store.pickup_available", true),
            AvailableStores = stores.Select(s => new StorePickupOptionViewModel
            {
                StoreId = s.Id,
                StoreName = s.Name,
                Address = s.Address,
                OpeningHours = s.OpeningHours,
                AllItemsAvailable = true
            }).ToList()
        };

        // Saved addresses (signed-in customers): list them and prefill the form with the default.
        if (user != null)
        {
            var saved = await _db.Addresses.Where(a => a.UserId == user.Id && !a.IsArchived)
                .OrderByDescending(a => a.IsDefault).ThenBy(a => a.Id).ToListAsync();
            vm.SavedAddresses = saved;
            var def = saved.FirstOrDefault(a => a.IsDefault) ?? saved.FirstOrDefault();
            if (def != null)
            {
                vm.SelectedAddressId = def.Id;
                vm.DeliveryAddress = new DeliveryAddressViewModel
                {
                    FullName = def.FullName, Phone = def.Phone, Line1 = def.Line1, Line2 = def.Line2,
                    City = def.City, State = def.State, Country = def.Country, PostalCode = def.PostalCode
                };
            }
        }

        // Loyalty redemption (signed-in customers only).
        if (user != null && await _loyalty.RedemptionEnabledAsync())
        {
            var balance = await _loyalty.GetBalanceAsync(user.Id);
            if (balance > 0)
            {
                var pointValue = await _loyalty.PointValueAsync();
                vm.LoyaltyAvailable = true;
                vm.LoyaltyPointsBalance = balance;
                vm.LoyaltyPointValue = pointValue;
                // Cap the discount at the order subtotal (can't redeem more than the goods are worth).
                vm.LoyaltyMaxDiscount = Math.Min(balance * pointValue, cart.Subtotal);
            }
        }

        vm.GiftCardsAvailable = await _giftCards.RedemptionEnabledAsync();

        return View(vm);
    }

    // True when every item in the cart is in stock across the given zone's branches combined
    // (Lagos = Ikota + Allen; Abuja = the Abuja branch) — the condition for same-day dispatch.
    private async Task<bool> IsCartAvailableInZoneAsync(CartViewModel cart, SterlingLams.Web.Services.DeliveryZone zone, List<Models.Domain.Store> activeStores)
    {
        var zoneStoreIds = activeStores
            .Where(s => SterlingLams.Web.Services.DeliveryZoneService.GetZone(s.State) == zone)
            .Select(s => s.Id).ToList();
        if (zoneStoreIds.Count == 0) return false;
        foreach (var item in cart.Items)
        {
            var avail = 0;
            foreach (var sid in zoneStoreIds)
                avail += await _stock.GetAvailableAsync(item.ProductId, item.VariantId, sid);
            if (avail < item.Quantity) return false;
        }
        return true;
    }

    // Build the client-side delivery-pricing JSON (zone detection + fees).
    private async Task<string> BuildDeliveryPricingJsonAsync(CartViewModel cart, List<Models.Domain.Store> activeStores)
    {
        // Distance zones per state (Lagos/Abuja), each with its own Standard + Express fees and the
        // areas it covers — the checkout resolves the fee from the customer's chosen area.
        var zones = await _zones.GetZonesAsync();
        var byState = zones
            .GroupBy(z => z.State, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(z => new
            {
                name = z.Name,
                standardFee = z.StandardFee, expressFee = z.ExpressFee, sameDayFee = z.SameDayFee,
                standardDays = z.StandardDays, expressDays = z.ExpressDays,
                areas = z.Areas,
            }).ToArray());

        var natStdFee  = await _settings.GetDecimalAsync("shipping.national_standard_fee", 7500);
        var natStdDays = await _settings.GetAsync("shipping.national_standard_days", "2 - 5 working days");

        // Same-day eligibility for THIS cart: enabled + the whole order is in local stock for the city.
        // The daily cut-off is no longer enforced or greyed-out — same-day is always selectable when the
        // location/stock qualify; `cutoff` drives an informational note (order before it = same day,
        // after = next day).
        var sd = await _zones.GetSameDayAsync();
        object sameDay = new { enabled = false };
        if (sd.Enabled)
        {
            sameDay = new
            {
                enabled = true,
                note = sd.Note,            // admin-editable, {cutoff} already substituted
                timeframe = sd.Timeframe,
                // Availability per city (whole order in local stock). The FEE comes from the resolved
                // distance zone (sameDayFee, sent per-zone above), like Express/Standard.
                lagos = new { available = await IsCartAvailableInZoneAsync(cart, SterlingLams.Web.Services.DeliveryZone.Lagos, activeStores) },
                abuja = new { available = await IsCartAvailableInZoneAsync(cart, SterlingLams.Web.Services.DeliveryZone.Abuja, activeStores) },
            };
        }

        // Which delivery methods are switched on (Admin → Settings → Shipping).
        var deliveryEnabled = new
        {
            standard = await _settings.GetBoolAsync("shipping.standard_enabled", true),
            express  = await _settings.GetBoolAsync("shipping.express_enabled", true),
        };

        // Editable checkout colours (Admin → Settings → Shipping).
        var ui = new
        {
            selectFill = await _settings.GetAsync("checkout.select_fill", "#111827"),
            noteColor  = await _settings.GetAsync("shipping.sameday_note_color", "#d97706"),
        };

        // Marketing "from ₦" starting prices per option (the exact zone fee still shows on the right +
        // in the order total). Editable in Admin → Settings → Shipping.
        var fromPrices = new
        {
            sameDay  = await _settings.GetDecimalAsync("shipping.sameday_from", 5000),
            express  = await _settings.GetDecimalAsync("shipping.priority_from", 4000),
            standard = await _settings.GetDecimalAsync("shipping.standard_from", 2000),
        };

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            zones = byState,   // { "Lagos": [ { name, standardFee, expressFee, sameDayFee, standardDays, expressDays, areas[] } ], "Abuja": [...] }
            national = new[]
            {
                new { type = "Standard", label = "Glams Standard Delivery", fee = natStdFee, timeframe = natStdDays },
            },
            lagosLGAs     = SterlingLams.Web.Services.DeliveryZoneService.LagosLGAs,
            abujaKeywords = new[] { "FCT", "Abuja", "Federal Capital" },
            deliveryEnabled,
            sameDay,
            ui,
            fromPrices,
        });
    }

    // ── Delivery-timeframe preview ──────────────────────────────────────────────
    public class DelayedItemDto
    {
        public int ProductId { get; set; }
        public int? VariantId { get; set; }
        public string ProductName { get; set; } = "";
        public string SourceStore { get; set; } = "";
        public string Eta { get; set; } = "";
    }

    // Cart items that would ship slowly: the customer's nearby branch (or chosen pickup branch)
    // can't cover them, so they must come from a far branch. Powers the checkout agreement modal
    // and the server-side guard below.
    private async Task<List<DelayedItemDto>> ComputeDelayedItemsAsync(
        CartViewModel cart, FulfillmentChoice fulfilment, string? state, string? city, int? pickupStoreId)
    {
        var result = new List<DelayedItemDto>();
        if (cart.IsEmpty) return result;

        var activeStores = await _db.Stores.Where(s => s.IsActive && s.IsPublic).ToListAsync();
        if (activeStores.Count == 0) return result;
        var crossEta = await _settings.GetAsync("shipping.cross_branch_days", "3 - 5 working days");
        // Inter-branch transfer timeframes for a pickup that needs stock moved to the chosen store:
        // same state/city is quick; a different state takes the nationwide window.
        var localEta = await _settings.GetAsync("shipping.transfer_local_eta", "24 - 48 hours");
        var interstateEta = await _settings.GetAsync("shipping.transfer_interstate_eta", "3 - 5 working days");
        var pickupStore = pickupStoreId.HasValue ? activeStores.FirstOrDefault(s => s.Id == pickupStoreId.Value) : null;

        async Task<Store?> NearestWithStockAsync(int pid, int? vid, int need)
        {
            var ranked = SterlingLams.Web.Services.DeliveryZoneService.RankStoresByProximity(activeStores, state, city);
            foreach (var s in ranked) if (await _stock.GetAvailableAsync(pid, vid, s.Id) >= need) return s;
            foreach (var s in ranked) if (await _stock.GetAvailableAsync(pid, vid, s.Id) > 0) return s;
            return null;
        }

        foreach (var grp in cart.Items.GroupBy(i => (i.ProductId, i.VariantId)))
        {
            var pid = grp.Key.ProductId; var vid = grp.Key.VariantId;
            var need = grp.Sum(i => i.Quantity);
            var name = grp.First().ProductName;

            if (fulfilment == FulfillmentChoice.StorePickup)
            {
                if (!pickupStoreId.HasValue) continue;
                if (await _stock.GetAvailableAsync(pid, vid, pickupStoreId.Value) >= need) continue; // ready at chosen branch
                var src = await NearestWithStockAsync(pid, vid, need);
                // Same state (e.g. two Lagos branches) transfers within 24–48h; a different state takes
                // the inter-state window. Fall back to inter-state when either location is unknown.
                var sameLocal = src != null && pickupStore != null
                    && !string.IsNullOrWhiteSpace(src.State) && !string.IsNullOrWhiteSpace(pickupStore.State)
                    && string.Equals(src.State, pickupStore.State, StringComparison.OrdinalIgnoreCase);
                result.Add(new DelayedItemDto { ProductId = pid, VariantId = vid, ProductName = name,
                    SourceStore = src?.Name.Replace("Sterlin Glams ", "") ?? "another branch",
                    Eta = sameLocal ? localEta : interstateEta });
            }
            else // delivery: "near" = covered by a branch inside the customer's delivery zone
            {
                var zone = SterlingLams.Web.Services.DeliveryZoneService.GetZone(state ?? "");
                // The far-stock agreement only applies to Lagos & Abuja customers (the zones with a
                // physical store, where buyers expect fast local delivery). Customers in other states
                // already expect the standard nationwide timeframe, so never prompt them.
                if (zone == SterlingLams.Web.Services.DeliveryZone.National) continue;
                var nearAvail = 0;
                foreach (var s in activeStores.Where(s => SterlingLams.Web.Services.DeliveryZoneService.GetZone(s.State) == zone))
                    nearAvail += await _stock.GetAvailableAsync(pid, vid, s.Id);
                if (nearAvail >= need) continue;
                var src = await NearestWithStockAsync(pid, vid, need);
                result.Add(new DelayedItemDto { ProductId = pid, VariantId = vid, ProductName = name,
                    SourceStore = src?.Name.Replace("Sterlin Glams ", "") ?? "another branch", Eta = crossEta });
            }
        }
        return result;
    }

    // AJAX: the checkout page calls this when the customer clicks "Proceed to Payment" to decide
    // whether to show the timeframe-agreement modal.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> FulfilmentPreview(string? fulfillmentType, string? state, string? city, int? storeId)
    {
        var cart = await GetCartAsync();
        var choice = string.Equals(fulfillmentType, "StorePickup", StringComparison.OrdinalIgnoreCase)
            ? FulfillmentChoice.StorePickup : FulfillmentChoice.Delivery;
        var delayed = await ComputeDelayedItemsAsync(cart, choice, state, city, storeId);
        var pickupName = choice == FulfillmentChoice.StorePickup && storeId.HasValue
            ? (await _db.Stores.Where(s => s.Id == storeId.Value).Select(s => s.Name).FirstOrDefaultAsync())?.Replace("Sterlin Glams ", "")
            : null;
        return Json(new
        {
            mode = choice == FulfillmentChoice.StorePickup ? "pickup" : "delivery",
            pickupStore = pickupName,
            delayed = delayed.Select(d => new { d.ProductId, d.VariantId, d.ProductName, d.SourceStore, d.Eta })
        });
    }

    // Re-populate the display-only fields the checkout view needs (states, stores, pricing, totals,
    // loyalty). POST model binding only fills the submitted form fields, so this MUST run before
    // re-rendering the checkout view on a validation error — otherwise the State dropdown, delivery
    // options and order summary all come back empty.
    private async Task RehydrateCheckoutDisplayAsync(CheckoutViewModel vm)
    {
        var cart = await GetCartAsync();
        var user = await _userManager.GetUserAsync(User);

        vm.Cart                = cart;
        vm.Subtotal            = cart.Subtotal;
        vm.DiscountAmount      = cart.DiscountAmount;
        vm.AppliedDiscountCode = cart.AppliedDiscountCode;
        vm.DiscountDescription = cart.DiscountDescription;
        var activeStores       = await _db.Stores.Where(s => s.IsActive && s.IsPublic).ToListAsync();
        vm.DeliveryPricingJson = await BuildDeliveryPricingJsonAsync(cart, activeStores);
        vm.NigerianStates      = SterlingLams.Web.Services.DeliveryZoneService.NigerianStates;
        vm.LagosLGAs           = SterlingLams.Web.Services.DeliveryZoneService.LagosLGAs;
        vm.PaystackPublicKey   = await _settings.GetAsync("payment.paystack.public_key", _config["Payment:Paystack:PublicKey"] ?? "");
        vm.PickupAvailable     = await _settings.GetBoolAsync("store.pickup_available", true);
        vm.AvailableStores     = activeStores
            .Select(s => new StorePickupOptionViewModel
            {
                StoreId = s.Id, StoreName = s.Name, Address = s.Address,
                OpeningHours = s.OpeningHours, AllItemsAvailable = true
            }).ToList();

        if (user != null && await _loyalty.RedemptionEnabledAsync())
        {
            var balance = await _loyalty.GetBalanceAsync(user.Id);
            if (balance > 0)
            {
                var pointValue = await _loyalty.PointValueAsync();
                vm.LoyaltyAvailable     = true;
                vm.LoyaltyPointsBalance = balance;
                vm.LoyaltyPointValue    = pointValue;
                vm.LoyaltyMaxDiscount   = Math.Min(balance * pointValue, cart.Subtotal);
            }
        }

        vm.GiftCardsAvailable = await _giftCards.RedemptionEnabledAsync();
    }

    // Re-render checkout after a validation error, with all display data repopulated.
    private async Task<IActionResult> RedisplayCheckoutAsync(CheckoutViewModel vm)
    {
        // Log exactly why checkout bounced, so a "page just reloads" report is diagnosable from logs.
        var errs = ModelState.Where(kv => kv.Value!.Errors.Count > 0)
            .Select(kv => $"{(string.IsNullOrEmpty(kv.Key) ? "(form)" : kv.Key)}: {string.Join("; ", kv.Value!.Errors.Select(e => e.ErrorMessage))}")
            .ToList();
        if (errs.Count > 0)
            _logger.LogWarning("Checkout redisplayed (fulfilment={Fulfilment}, store={StoreId}) — validation: {Errors}",
                vm.FulfillmentType, vm.SelectedStoreId, string.Join(" | ", errs));

        await RehydrateCheckoutDisplayAsync(vm);
        // The customer has already submitted, so keep their fulfilment choice selected on redisplay
        // (a fresh GET leaves both unselected — no auto-select).
        ViewData["FulfilmentPicked"] = true;
        return View("Index", vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PlaceOrder(CheckoutViewModel vm)
    {
        // Store pickup doesn't use the delivery address OR a delivery method, but the form still POSTs
        // those fields empty. They're non-nullable strings, so the framework's implicit "required" fails
        // them ("The State field is required", "The SelectedDeliveryType field is required") and the order
        // silently bounced back to checkout ("just reloads"). Drop those errors for pickup so it can
        // proceed to payment, and default the (unused) delivery type. (Delivery still validates the
        // address via CheckoutViewModel.Validate, and a delivery option via the client submit handler.)
        if (vm.FulfillmentType == FulfillmentChoice.StorePickup)
        {
            foreach (var key in ModelState.Keys
                .Where(k => k.StartsWith("DeliveryAddress", StringComparison.Ordinal) || k == "SelectedDeliveryType").ToList())
                ModelState.Remove(key);
            if (string.IsNullOrWhiteSpace(vm.SelectedDeliveryType)) vm.SelectedDeliveryType = "Standard";
        }

        if (!ModelState.IsValid) return await RedisplayCheckoutAsync(vm);

        var cart = await GetCartAsync();
        if (cart.IsEmpty) return RedirectToAction("Index", "Cart");

        // Store-level gates (admin-toggled in Settings → Store).
        if (!await _settings.GetBoolAsync("store.accepting_orders", true))
        {
            TempData["Error"] = "We're not accepting online orders right now. Please check back soon.";
            return RedirectToAction("Index", "Cart");
        }
        if (vm.FulfillmentType == FulfillmentChoice.StorePickup
            && !await _settings.GetBoolAsync("store.pickup_available", true))
        {
            TempData["Error"] = "In-store pickup isn't available right now. Please choose delivery.";
            return RedirectToAction("Index");
        }

        // Minimum order value (0 = no minimum).
        var minOrder = await _settings.GetDecimalAsync("order.min_value", 0);
        if (minOrder > 0 && cart.Subtotal < minOrder)
        {
            ModelState.AddModelError("", $"Minimum order value is {await _settings.GetAsync("store.currency_symbol", "₦")}{minOrder:N0}. Please add a little more to your bag.");
            return await RedisplayCheckoutAsync(vm);
        }

        // ── Resolve user (authenticated or guest) ──────────────────────────
        ApplicationUser? user = await _userManager.GetUserAsync(User);

        if (user == null)
        {
            // Guest checkout: email is always required (for the confirmation + receipt).
            if (string.IsNullOrWhiteSpace(vm.GuestEmail))
                ModelState.AddModelError("GuestEmail", "Please enter your email address.");
            // Store pickup also needs a name and phone so the branch can reach the customer at collection.
            if (vm.FulfillmentType == FulfillmentChoice.StorePickup)
            {
                if (string.IsNullOrWhiteSpace(vm.GuestName))
                    ModelState.AddModelError("GuestName", "Please enter your name for the pickup.");
                if (string.IsNullOrWhiteSpace(vm.GuestPhone))
                    ModelState.AddModelError("GuestPhone", "Please enter a phone number for the pickup.");
            }
            if (!ModelState.IsValid)
            {
                vm.Cart = cart;
                vm.AvailableStores = (await _db.Stores.Where(s => s.IsActive && s.IsPublic).ToListAsync())
                    .Select(s => new StorePickupOptionViewModel { StoreId = s.Id, StoreName = s.Name, Address = s.Address, OpeningHours = s.OpeningHours, AllItemsAvailable = true }).ToList();
                return await RedisplayCheckoutAsync(vm);
            }

            // A returning customer may check out as a guest with the email on their existing account —
            // attach the order to that account rather than forcing a sign-in (guest confirmation is
            // token-based; no sign-in happens). A new guest shell is created only if no account exists.
            user = await _userManager.FindByEmailAsync(vm.GuestEmail);
            if (user == null)
            {
                var guestName = vm.GuestName?.Trim() ?? "Guest";
                var nameParts = guestName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                user = new ApplicationUser
                {
                    UserName  = vm.GuestEmail,
                    Email     = vm.GuestEmail,
                    FirstName = nameParts.Length > 0 ? nameParts[0] : "Guest",
                    LastName  = nameParts.Length > 1 ? nameParts[1] : string.Empty,
                    PhoneNumber = Infrastructure.PhoneNumbers.Canonical(vm.GuestPhone),
                    IsGuest   = true,
                    CreatedAt = DateTime.UtcNow
                };
                var createResult = await _userManager.CreateAsync(user, Guid.NewGuid().ToString("N") + "Aa1!");
                if (!createResult.Succeeded)
                {
                    ModelState.AddModelError("", "Unable to process guest checkout. Please try again.");
                    vm.Cart = cart;
                    vm.AvailableStores = (await _db.Stores.Where(s => s.IsActive && s.IsPublic).ToListAsync())
                        .Select(s => new StorePickupOptionViewModel { StoreId = s.Id, StoreName = s.Name, Address = s.Address, OpeningHours = s.OpeningHours, AllItemsAvailable = true }).ToList();
                    return await RedisplayCheckoutAsync(vm);
                }
                _logger.LogInformation("Guest account created for checkout: {Email}", SterlingLams.Web.Infrastructure.LogRedact.Email(vm.GuestEmail));
            }
        }

        // Validate store selection for pickup orders
        if (vm.FulfillmentType == FulfillmentChoice.StorePickup)
        {
            if (vm.SelectedStoreId == null || !await _db.Stores.AnyAsync(s => s.Id == vm.SelectedStoreId && s.IsActive && s.IsPublic))
            {
                ModelState.AddModelError("", "Please select a valid store for pickup.");
                vm.Cart = cart;
                vm.AvailableStores = (await _db.Stores.Where(s => s.IsActive && s.IsPublic).ToListAsync())
                    .Select(s => new StorePickupOptionViewModel { StoreId = s.Id, StoreName = s.Name, Address = s.Address, OpeningHours = s.OpeningHours, AllItemsAvailable = true }).ToList();
                return await RedisplayCheckoutAsync(vm);
            }

            // Name, phone and email are mandatory for pickup so the branch can reach the customer at
            // collection. For a signed-in buyer these come from the profile; the GuestPhone field is also
            // shown so a customer with no phone on file can supply one (which we then save to the account).
            var pPhone = !string.IsNullOrWhiteSpace(vm.GuestPhone) ? vm.GuestPhone!.Trim() : user.PhoneNumber;
            if (string.IsNullOrWhiteSpace(user.FullName))
                ModelState.AddModelError("GuestName", "Please enter a name for the pickup.");
            if (string.IsNullOrWhiteSpace(pPhone))
                ModelState.AddModelError("GuestPhone", "Please enter a phone number for the pickup.");
            if (string.IsNullOrWhiteSpace(user.Email))
                ModelState.AddModelError("GuestEmail", "Please enter an email for the pickup.");
            if (!ModelState.IsValid)
            {
                vm.Cart = cart;
                vm.AvailableStores = (await _db.Stores.Where(s => s.IsActive && s.IsPublic).ToListAsync())
                    .Select(s => new StorePickupOptionViewModel { StoreId = s.Id, StoreName = s.Name, Address = s.Address, OpeningHours = s.OpeningHours, AllItemsAvailable = true }).ToList();
                return await RedisplayCheckoutAsync(vm);
            }
            // Backfill a phone the account was missing, so staff + the receipt have it next time.
            if (string.IsNullOrWhiteSpace(user.PhoneNumber) && !string.IsNullOrWhiteSpace(pPhone))
            {
                user.PhoneNumber = Infrastructure.PhoneNumbers.Canonical(pPhone);
                await _userManager.UpdateAsync(user);
            }
        }

        // Validate that all product IDs exist and are active
        var productIds = cart.Items.Select(i => i.ProductId).Distinct().ToList();
        var validProducts = await _db.Products
            .Where(p => productIds.Contains(p.Id) && p.IsActive)
            .Select(p => p.Id)
            .ToListAsync();

        if (validProducts.Count != productIds.Count)
        {
            TempData["Error"] = "One or more items in your cart are no longer available. Please review your bag.";
            return RedirectToAction("Index", "Cart");
        }

        // Overselling is guarded by reserving stock once the order is saved (see below) — that
        // hold is atomic and blocks concurrent orders from claiming the same units.

        // Re-validate the discount server-side (never trust the cached cart amount)
        decimal discountAmount = 0;
        bool   freeShipping    = false;
        string? discountCode   = null;
        if (!string.IsNullOrEmpty(cart.AppliedDiscountCode))
        {
            // Pass the delivery state so a location-restricted code (e.g. free delivery for Lagos/Abuja
            // only) is enforced here — the authoritative point where the fee is charged.
            var discountState = vm.FulfillmentType == FulfillmentChoice.Delivery ? vm.DeliveryAddress?.State : null;
            var dr = cart.IsAutomaticDiscount
                ? await _discounts.FindAutomaticAsync(cart, user.Id, discountState)
                : await _discounts.EvaluateAsync(cart.AppliedDiscountCode, cart, user.Id, discountState);
            if (dr != null && dr.Success)
            {
                discountCode   = dr.Code;
                discountAmount = dr.Amount;
                freeShipping   = dr.FreeShipping;
            }
        }

        // Same-day delivery guard: re-validate server-side (the option is client-rendered). It's valid
        // for a Lagos/Abuja address when the whole order is in local stock. The daily cut-off is NOT
        // enforced here — orders placed after it are simply delivered the next day (shown as a note at
        // checkout). Reject a tampered selection rather than silently charging the same-day fee.
        if (vm.FulfillmentType == FulfillmentChoice.Delivery
            && string.Equals(vm.SelectedDeliveryType, "SameDay", StringComparison.OrdinalIgnoreCase))
        {
            var sd = await _zones.GetSameDayAsync();
            var zone = SterlingLams.Web.Services.DeliveryZoneService.GetZone(vm.DeliveryAddress.State ?? "");
            var activeStores = await _db.Stores.Where(s => s.IsActive && s.IsPublic).ToListAsync();
            var eligible = sd.Enabled
                && (zone == SterlingLams.Web.Services.DeliveryZone.Lagos || zone == SterlingLams.Web.Services.DeliveryZone.Abuja)
                && await IsCartAvailableInZoneAsync(cart, zone, activeStores);
            if (!eligible)
            {
                ModelState.AddModelError("SelectedDeliveryType",
                    "Glams Same-Day Delivery isn't available for this order. It's offered to Lagos & Abuja addresses when every item is in local stock. Please choose another delivery option.");
                return await RedisplayCheckoutAsync(vm);
            }
        }

        // Calculate delivery fee server-side (never trust client-submitted amount)
        decimal deliveryFee = 0;
        if (vm.FulfillmentType == FulfillmentChoice.Delivery)
            deliveryFee = await _zones.CalculateFeeAsync(vm.DeliveryAddress.State, vm.DeliveryAddress.City, vm.SelectedDeliveryType);
        // Free shipping only covers Glams Standard Delivery (3–5 working days). Faster options (Priority,
        // Same-Day) are never waived — a customer can still upgrade for the difference.
        bool freeShippingApplies = freeShipping
            && string.Equals(vm.SelectedDeliveryType, "Standard", StringComparison.OrdinalIgnoreCase);
        if (freeShippingApplies) deliveryFee = 0;   // free-shipping discount waives the fee

        // ── Loyalty redemption ──────────────────────────────────────────────
        // Earmark points + discount now (reduces the amount charged); the actual point deduction
        // happens on payment success (RedeemForOrderAsync) so an abandoned order never loses points.
        int loyaltyPoints = 0;
        decimal loyaltyDiscount = 0m;
        if (vm.RedeemPoints && await _loyalty.RedemptionEnabledAsync())
        {
            var balance = await _loyalty.GetBalanceAsync(user.Id);
            if (balance > 0)
            {
                var pointValue = await _loyalty.PointValueAsync();
                var preTotal = cart.Subtotal - discountAmount + deliveryFee;
                // Cap by points held, by the goods value (after promo), and leave ≥₦1 to charge.
                var cap = Math.Min(Math.Min(balance * pointValue, cart.Subtotal - discountAmount), preTotal - 1m);
                if (cap > 0)
                {
                    loyaltyPoints = (int)Math.Floor(cap / pointValue);
                    loyaltyDiscount = loyaltyPoints * pointValue;
                }
            }
        }

        // ── Gift card redemption ────────────────────────────────────────────
        // Drawn from whatever is left to pay after promo + loyalty. Earmark now; the actual
        // balance draw happens on payment success (RedeemForOrderAsync) so an abandoned order
        // never drains the card. We leave ≥₦1 to charge so the gateway always has a positive
        // amount (full gift-card payment / zero-total checkout is a deferred enhancement).
        string? giftCardCode = null;
        decimal giftCardAmount = 0m;
        if (!string.IsNullOrWhiteSpace(vm.GiftCardCode) && await _giftCards.RedemptionEnabledAsync())
        {
            var lookup = await _giftCards.ValidateAsync(vm.GiftCardCode);
            if (!lookup.Ok)
            {
                ModelState.AddModelError("GiftCardCode", lookup.Message);
                return await RedisplayCheckoutAsync(vm);
            }
            var dueBeforeCard = cart.Subtotal - discountAmount + deliveryFee - loyaltyDiscount;
            var cap = Math.Min(lookup.Balance, dueBeforeCard - 1m);
            if (cap > 0)
            {
                giftCardAmount = Math.Round(cap, 2);
                giftCardCode = lookup.Code;
            }
        }

        // Build order — short sequential number, e.g. SL-30012.
        var orderNumber = await _orderNumbers.NextAsync(OrderChannel.Online);

        // Capture THIS order's buyer name/phone so a shared account (e.g. customer-care shell email used
        // for many different buyers) never makes an order show the wrong person. Prefer what was typed at
        // checkout (guest fields), then the delivery address, then fall back to the account at display time.
        var contactName = !string.IsNullOrWhiteSpace(vm.GuestName) ? vm.GuestName!.Trim()
            : (!string.IsNullOrWhiteSpace(vm.DeliveryAddress?.FullName) ? vm.DeliveryAddress!.FullName.Trim() : null);
        var contactPhone = !string.IsNullOrWhiteSpace(vm.GuestPhone) ? vm.GuestPhone!.Trim()
            : (!string.IsNullOrWhiteSpace(vm.DeliveryAddress?.Phone) ? vm.DeliveryAddress!.Phone.Trim() : null);

        // Capture each line's SKU (variant SKU first, else the product's) on the order so it shows on the
        // emails + receipts — the cart cookie doesn't carry it, so resolve from the catalogue here.
        var lineProductIds = cart.Items.Select(i => i.ProductId).Distinct().ToList();
        var lineVariantIds = cart.Items.Where(i => i.VariantId != null).Select(i => i.VariantId!.Value).Distinct().ToList();
        var productSkus = await _db.Products.Where(p => lineProductIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Sku }).ToDictionaryAsync(p => p.Id, p => p.Sku);
        var variantSkus = lineVariantIds.Count == 0
            ? new Dictionary<int, string?>()
            : await _db.ProductVariants.Where(v => lineVariantIds.Contains(v.Id))
                .Select(v => new { v.Id, v.Sku }).ToDictionaryAsync(v => v.Id, v => v.Sku);
        string? SkuFor(Models.ViewModels.CartItemViewModel i) =>
            (i.VariantId != null && variantSkus.TryGetValue(i.VariantId.Value, out var vs) && !string.IsNullOrWhiteSpace(vs))
                ? vs : productSkus.GetValueOrDefault(i.ProductId);

        var order = new Order
        {
            OrderNumber = orderNumber,
            UserId = user.Id,
            ContactName = contactName,
            ContactPhone = Infrastructure.PhoneNumbers.Canonical(contactPhone),
            FulfillmentType = vm.FulfillmentType == FulfillmentChoice.StorePickup
                ? FulfillmentType.StorePickup
                : FulfillmentType.Delivery,
            PickupStoreId = vm.FulfillmentType == FulfillmentChoice.StorePickup ? vm.SelectedStoreId : null,
            Notes = string.IsNullOrWhiteSpace(vm.OrderNotes) ? null : vm.OrderNotes.Trim(),
            WhatsAppOptIn = vm.WhatsAppOptIn,   // checkout opt-in (default ticked)
            // Order attribution (WooCommerce-style)
            CustomerIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            DeviceType = SterlingLams.Web.Infrastructure.OrderAttributionMiddleware.DeviceFromUserAgent(Request.Headers.UserAgent.ToString()),
            Origin = HttpContext.Session.GetString(SterlingLams.Web.Infrastructure.OrderAttributionMiddleware.OriginKey) ?? "Direct",
            SessionPageViews = HttpContext.Session.GetInt32(SterlingLams.Web.Infrastructure.OrderAttributionMiddleware.PageViewsKey),
            Subtotal = cart.Subtotal,
            DeliveryFee = deliveryFee,
            DeliveryType = vm.FulfillmentType == FulfillmentChoice.Delivery && !string.IsNullOrWhiteSpace(vm.SelectedDeliveryType)
                ? vm.SelectedDeliveryType.Trim() : null,
            DiscountCode = discountCode,
            DiscountAmount = discountAmount,
            LoyaltyPointsRedeemed = loyaltyPoints,
            LoyaltyDiscount = loyaltyDiscount,
            GiftCardCode = giftCardCode,
            GiftCardAmount = giftCardAmount,
            Total = cart.Subtotal - discountAmount + deliveryFee - loyaltyDiscount - giftCardAmount,
            Items = cart.Items.Select(i => new OrderItem
            {
                ProductId = i.ProductId,
                ProductVariantId = i.VariantId,
                ProductName = i.ProductName,
                VariantName = i.VariantName,
                ProductSku = SkuFor(i),
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice
            }).ToList()
        };

        if (vm.FulfillmentType == FulfillmentChoice.Delivery)
        {
            var addr = new Address
            {
                UserId = user.Id,
                FullName = vm.DeliveryAddress.FullName,
                Phone = vm.DeliveryAddress.Phone,
                Line1 = vm.DeliveryAddress.Line1,
                Line2 = vm.DeliveryAddress.Line2,
                City = vm.DeliveryAddress.City,
                State = vm.DeliveryAddress.State,
                Country = vm.DeliveryAddress.Country,
                PostalCode = vm.DeliveryAddress.PostalCode
            };
            _db.Addresses.Add(addr);
            await _db.SaveChangesAsync();
            order.DeliveryAddressId = addr.Id;
        }

        // Stock is never held before payment (first-come-first-served). Re-check live availability
        // right before sending the customer to pay, so if an item already sold out since the cart
        // was loaded we block here — the second buyer sees "sold out" instead of paying. (A truly
        // simultaneous payment for the last unit still slips through and is auto-refunded at the
        // callback; this check stops the common "clicked pay after it sold out" case.)
        var activeStoreIds = await _db.Stores.Where(s => s.IsActive && s.IsPublic).Select(s => s.Id).ToListAsync();
        foreach (var grp in cart.Items.GroupBy(i => (i.ProductId, i.VariantId)))
        {
            var need = grp.Sum(i => i.Quantity);
            var have = 0;
            foreach (var sid in activeStoreIds)
                have += await _stock.GetAvailableAsync(grp.Key.ProductId, grp.Key.VariantId, sid);
            if (have < need)
            {
                TempData["Error"] = $"Sorry — \"{grp.First().ProductName}\" just sold out. Please review your bag.";
                return RedirectToAction("Index", "Cart");
            }
        }

        // Far-stock delivery timeframe: if any item must ship from a branch far from the customer,
        // they must have acknowledged the longer ETA in the modal. (The client shows it; this is the
        // server-side backstop in case the modal is bypassed.)
        var delayedItems = await ComputeDelayedItemsAsync(cart, vm.FulfillmentType,
            vm.DeliveryAddress?.State, vm.DeliveryAddress?.City, vm.SelectedStoreId);
        if (delayedItems.Count > 0 && !vm.TimeframeAcknowledged)
        {
            ModelState.AddModelError("", "Please acknowledge the delivery timeframe for items shipping from another branch.");
            return await RedisplayCheckoutAsync(vm);
        }

        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        SterlingLams.Web.Services.OrderNotes.AddSystem(_db, order.Id,
            $"Order placed by customer ({(order.FulfillmentType == FulfillmentType.StorePickup ? "store pickup" : "delivery")}). Awaiting payment.");
        await _db.SaveChangesAsync();

        try { await _audit.LogAsync("Order", "Order", order.Id.ToString(), $"Online order placed {order.OrderNumber} — ₦{order.Total:N0}"); } catch { }

        // Newsletter opt-in (deduped) when the customer ticked the box.
        if (vm.SubscribeNewsletter && !string.IsNullOrWhiteSpace(user.Email))
        {
            var subEmail = user.Email.Trim().ToLowerInvariant();
            if (!await _db.NewsletterSubscribers.AnyAsync(s => s.Email == subEmail))
            {
                _db.NewsletterSubscribers.Add(new Models.Domain.NewsletterSubscriber { Email = subEmail, CreatedAt = DateTime.UtcNow });
                await _db.SaveChangesAsync();
            }
        }

        // No stock is held before payment — it's committed first-come-first-served when payment
        // lands (FulfilPaidOrderAsync). If an item sells out before this customer pays, the
        // payment is auto-cancelled + refunded at the callback.

        // Snapshot the cart for abandoned-cart recovery (emailed later if payment isn't completed).
        await CaptureAbandonedCartAsync(user.Email, cart);

        // Initiate payment
        var callbackUrl = Url.Action("PaymentCallback", "Checkout", null, Request.Scheme) ?? string.Empty;
        var result = await _payment.InitiatePaymentAsync(new InitiatePaymentRequest
        {
            OrderNumber = order.OrderNumber,
            Amount = order.Total,
            Currency = order.Currency,
            CustomerEmail = user.Email ?? string.Empty,
            CustomerName = user.FullName,
            CallbackUrl = callbackUrl,
            Metadata = new Dictionary<string, string> { ["order_id"] = order.Id.ToString() }
        });

        if (!result.Success)
        {
            _logger.LogError("Payment initiation failed for order {OrderNumber}: {Error}", orderNumber, result.ErrorMessage);

            // In Development, bypass payment gateway and simulate a successful payment
            if (_env.IsDevelopment())
            {
                _logger.LogWarning("[DEV MODE] Redirecting to simulated payment for order {OrderNumber}", orderNumber);
                return RedirectToAction("DevConfirm", new { orderId = order.Id });
            }

            ModelState.AddModelError("", "Payment could not be initiated. Please try again.");
            return await RedisplayCheckoutAsync(vm);
        }

        return Redirect(result.AuthorizationUrl!);
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> PaymentCallback(string reference, string trxref)
    {
        var refToVerify = reference ?? trxref;
        if (string.IsNullOrEmpty(refToVerify)) return RedirectToAction("Index", "Home");

        var result = await _payment.VerifyPaymentAsync(refToVerify);

        if (!result.IsPaid)
        {
            // Payment failed — free the reserved stock so it returns to sale.
            var failed = await _db.Orders.FirstOrDefaultAsync(o => o.OrderNumber == result.OrderNumber);
            if (failed != null) await _fulfilment.ReleaseReservationAsync(failed.Id);
            TempData["Error"] = "Payment could not be verified. Please contact support.";
            return RedirectToAction("Index", "Cart");
        }

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.OrderNumber == result.OrderNumber);
        if (order != null)
        {
            var wasUnpaid = !order.IsPaid;
            var prevStatus = order.Status;
            order.IsPaid = true;
            order.PaidAt = DateTime.UtcNow;
            order.Status = OrderStatus.Confirmed;
            order.PaymentReference = refToVerify;
            order.PaymentProvider = _payment.ProviderName;
            if (wasUnpaid)
            {
                SterlingLams.Web.Services.OrderNotes.AddSystem(_db, order.Id,
                    $"Payment via {_payment.ProviderName} successful (Transaction Reference: {refToVerify}).");
                if (prevStatus != OrderStatus.Confirmed)
                    SterlingLams.Web.Services.OrderNotes.AddSystem(_db, order.Id,
                        $"Order status changed from {prevStatus} to Confirmed (payment received).");
            }
            await _db.SaveChangesAsync();
            // Payment landed → close this buyer's abandoned-cart snapshot so they get no more
            // "you left something in your bag" reminders for a bag they've already paid for.
            var buyer = await _db.Users.Where(u => u.Id == order.UserId)
                .Select(u => new { u.Email, u.FirstName, u.LastName, u.PhoneNumber }).FirstOrDefaultAsync();
            var buyerEmail = buyer?.Email;
            await MarkAbandonedRecoveredAsync(buyerEmail);
            if (wasUnpaid)
            {
                try { await _audit.LogAsync("Payment", "Order", order.Id.ToString(), $"Payment received for {order.OrderNumber} — ₦{order.Total:N0} ({_payment.ProviderName})"); } catch { }
                _ = _whatsapp.NotifyOrderAsync(order.Id, SterlingLams.Web.Services.WhatsAppOrderEvent.PaymentReceived);
                // Retainful "Placed Order" event (win-back / post-purchase automations). Fire-and-forget.
                var itemQty = await _db.OrderItems.Where(i => i.OrderId == order.Id).SumAsync(i => (int?)i.Quantity) ?? 0;
                _ = _retainful.SendOrderPlacedAsync(buyerEmail, buyer?.PhoneNumber, buyer?.FirstName, buyer?.LastName,
                    order.OrderNumber, order.Total, itemQty);
                // Authoritative funnel completion (guarded by wasUnpaid, so it fires once across the
                // return + webhook paths — whichever flips the order to paid first).
                await _posthog.CaptureAsync(order.UserId ?? $"order_{order.OrderNumber}", "order_paid", new
                {
                    value = order.Total, currency = "NGN", order_number = order.OrderNumber,
                    payment_provider = _payment.ProviderName, channel = "online",
                });
            }

            // Commit stock first-come-first-served. If an item sold out before this payment
            // landed, auto-cancel + refund instead of confirming.
            var outcome = await _fulfilment.FulfilPaidOrderAsync(order.Id);
            if (outcome == SterlingLams.Web.Services.FulfilOutcome.SoldOut)
            {
                // Stock was committed first-come-first-served; this payment lost the race. The
                // fulfilment service already cancelled + refunded — just tell the customer.
                TempData["Error"] = $"Sorry — an item in order {order.OrderNumber} sold out just before your payment completed. You've been refunded in full.";
                CartStore.Clear(HttpContext);
                return RedirectToAction("Confirmation", new { orderNumber = result.OrderNumber, token = ConfirmationToken(result.OrderNumber!) });
            }

            await IncrementDiscountUsageAsync(order);
            await _loyalty.RedeemForOrderAsync(order.Id);
            await _giftCards.RedeemForOrderAsync(order.Id);
            await _loyalty.AccrueForOrderAsync(order.Id);
            await _logistics.PushOrderAsync(order.Id);

            await SendOrderEmailsAsync(order.Id);
        }

        // Clear cart
        CartStore.Clear(HttpContext);

        return RedirectToAction("Confirmation", new { orderNumber = result.OrderNumber, token = ConfirmationToken(result.OrderNumber!) });
    }

    /// <summary>
    /// DEV ONLY — simulates a successful payment, confirms the order, and runs in-house fulfilment.
    /// Not available in Production.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> DevConfirm(int orderId)
    {
        if (!_env.IsDevelopment()) return NotFound();

        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();

        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.PickupStore)
            .Include(o => o.DeliveryAddress)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == user.Id);

        if (order == null) return NotFound();

        // Mark as paid
        order.IsPaid = true;
        order.PaidAt = DateTime.UtcNow;
        order.Status = OrderStatus.Confirmed;
        order.PaymentReference = $"SIM-DEV-{order.OrderNumber}";
        order.PaymentProvider = "Simulated (Dev Only)";
        await _db.SaveChangesAsync();
        await MarkAbandonedRecoveredAsync(user.Email);

        var outcome = await _fulfilment.FulfilPaidOrderAsync(order.Id);
        if (outcome == SterlingLams.Web.Services.FulfilOutcome.SoldOut)
        {
            TempData["Error"] = $"Sorry — an item in order {order.OrderNumber} sold out just before your payment completed. You've been refunded in full.";
            CartStore.Clear(HttpContext);
            return RedirectToAction("Confirmation", new { orderNumber = order.OrderNumber, token = ConfirmationToken(order.OrderNumber) });
        }

        await IncrementDiscountUsageAsync(order);
        await _loyalty.RedeemForOrderAsync(order.Id);
        await _giftCards.RedeemForOrderAsync(order.Id);
        await _loyalty.AccrueForOrderAsync(order.Id);
        await _logistics.PushOrderAsync(order.Id);

        await SendOrderEmailsAsync(order.Id);

        CartStore.Clear(HttpContext);
        return RedirectToAction("Confirmation", new { orderNumber = order.OrderNumber, token = ConfirmationToken(order.OrderNumber) });
    }

    /// <summary>
    /// Emails the customer an order confirmation and (optionally) alerts the admin of a new order.
    /// Respects the Notifications settings toggles. Never throws — email must not break checkout.
    /// </summary>
    private async Task SendOrderEmailsAsync(int orderId)
    {
        try
        {
            var order = await _db.Orders.Include(o => o.Items)
                .Include(o => o.DeliveryAddress).Include(o => o.PickupStore).Include(o => o.User)
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return;

            var customerEmail = order.User?.Email
                ?? await _db.Users.Where(u => u.Id == order.UserId).Select(u => u.Email).FirstOrDefaultAsync();

            // Customer confirmation — rich WooCommerce-style layout (editable in Email Customizer).
            if (!string.IsNullOrWhiteSpace(customerEmail)
                && await _settings.GetBoolAsync("notifications.order_confirmed", true))
            {
                var subject = await _settings.GetAsync("email.order_confirmed.subject", "Your order is being processed");
                var intro = await _settings.GetAsync("email.order_confirmed.intro",
                    "Your order {order} ({date}) has been received and is now being processed.");

                // Per-item primary image (absolute URL for email clients).
                var baseUrl = (_config["App:BaseUrl"] ?? "").TrimEnd('/');
                var pids = order.Items.Select(i => i.ProductId).Distinct().ToList();
                var imgMap = await _db.ProductImages.Where(im => pids.Contains(im.ProductId))
                    .GroupBy(im => im.ProductId)
                    .Select(g => new { Pid = g.Key, Url = g.OrderByDescending(x => x.IsPrimary).Select(x => x.Url).FirstOrDefault() })
                    .ToDictionaryAsync(x => x.Pid, x => x.Url);
                string? AbsImg(int pid)
                {
                    var u = imgMap.GetValueOrDefault(pid);
                    if (string.IsNullOrWhiteSpace(u)) return null;
                    return u.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? u
                         : (string.IsNullOrEmpty(baseUrl) ? null : baseUrl + "/" + u.TrimStart('/'));
                }

                var items = order.Items.Select(i =>
                    new SterlingLams.Web.Services.OrderEmailTemplate.Item(
                        i.ProductName, i.VariantName, i.Quantity, i.LineTotal, AbsImg(i.ProductId), i.ProductSku)).ToList();

                var custName = !string.IsNullOrWhiteSpace(order.ContactName) ? order.ContactName!.Trim() : (order.User?.FullName ?? order.DeliveryAddress?.FullName ?? "");
                var (billing, shipping, pickupLabel) = SterlingLams.Web.Services.OrderEmailTemplate.AddressBlocksFor(order, custName, customerEmail);

                // Glams-branded shipping line, e.g. "₦4,000.00 via Glams Priority Delivery (within 48 hrs)".
                var shippingLabel = await _zones.EmailShippingLabelAsync(order, pickupLabel);

                var introHtml = SterlingLams.Web.Services.OrderEmailTemplate.ApplyPlaceholders(
                    intro, "#" + order.OrderNumber, order.CreatedAt, custName);
                var body = SterlingLams.Web.Services.OrderEmailTemplate.Build(
                    heading: subject,
                    introHtml: introHtml,
                    orderNumber: order.OrderNumber,
                    orderDate: order.CreatedAt,
                    items: items,
                    subtotal: order.Subtotal,
                    shippingLabel: shippingLabel,
                    total: order.Total,
                    // Friendly payment line like the old site: the Paystack channels the customer could use.
                    paymentMethod: (order.PaymentProvider ?? "").Contains("Paystack", StringComparison.OrdinalIgnoreCase)
                        ? "Debit/Credit Card, Bank Transfer, USSD, Opay"
                        : (string.IsNullOrWhiteSpace(order.PaymentProvider) ? "—" : order.PaymentProvider),
                    billingLines: billing,
                    shippingLines: shipping);
                await _email.SendAsync(customerEmail!, subject, body, ct: HttpContext.RequestAborted);
            }

            // WhatsApp order confirmation — self-gated by whatsapp.notify.order_confirmed + a customer
            // phone, independent of the email setting above. Fire-and-forget (own scope, never throws).
            _ = _whatsapp.NotifyOrderAsync(order.Id, SterlingLams.Web.Services.WhatsAppOrderEvent.OrderConfirmed);

            // Admin new-order alert
            if (await _settings.GetBoolAsync("notifications.new_order", true))
            {
                // New-order alert recipients: the dedicated new-order list, falling back to the
                // general admin email when that list is left blank.
                var adminEmail = await _settings.GetAsync("notifications.new_order_emails", "");
                if (string.IsNullOrWhiteSpace(adminEmail))
                    adminEmail = await _settings.GetAsync("notifications.admin_email", "");
                if (!string.IsNullOrWhiteSpace(adminEmail))
                {
                    // Rich admin alert — same layout as the customer confirmation (images, colour,
                    // quantities, billing + shipping, pickup vs delivery). Subject/intro editable in
                    // the Email Customizer ("New order alert (admin)").
                    var adminSubjectT = await _settings.GetAsync("email.new_order_admin.subject", "New order {order}");
                    var adminSubject = adminSubjectT.Replace("{order}", order.OrderNumber) + $" — ₦{order.Total:N0}";
                    var adminBody = await BuildAdminOrderAlertAsync(order, customerEmail);
                    var fromAlerts = await _settings.GetAsync("email.from_alerts", "");
                    foreach (var addr in SterlingLams.Web.Infrastructure.EmailRecipients.Split(adminEmail))
                        await _email.SendAsync(addr, adminSubject, adminBody, ct: HttpContext.RequestAborted, fromOverride: fromAlerts);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed sending order emails for order {OrderId}", orderId);
        }
    }

    // Rich "new order received" admin body: mirrors the customer confirmation (product images, colour,
    // quantities, billing + shipping, or "Pickup at …" for store pickup). Subject/intro editable in the
    // Email Customizer ("New order alert (admin)").
    private async Task<string> BuildAdminOrderAlertAsync(Order order, string? customerEmail)
    {
        var baseUrl = (_config["App:BaseUrl"] ?? "").TrimEnd('/');
        var pids = order.Items.Select(i => i.ProductId).Distinct().ToList();
        var imgMap = await _db.ProductImages.Where(im => pids.Contains(im.ProductId))
            .GroupBy(im => im.ProductId)
            .Select(g => new { Pid = g.Key, Url = g.OrderByDescending(x => x.IsPrimary).Select(x => x.Url).FirstOrDefault() })
            .ToDictionaryAsync(x => x.Pid, x => x.Url);
        string? AbsImg(int pid)
        {
            var u = imgMap.GetValueOrDefault(pid);
            if (string.IsNullOrWhiteSpace(u)) return null;
            return u.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? u
                 : (string.IsNullOrEmpty(baseUrl) ? null : baseUrl + "/" + u.TrimStart('/'));
        }
        var items = order.Items.Select(i => new SterlingLams.Web.Services.OrderEmailTemplate.Item(
            i.ProductName, i.VariantName, i.Quantity, i.LineTotal, AbsImg(i.ProductId), i.ProductSku)).ToList();

        var custName = !string.IsNullOrWhiteSpace(order.ContactName) ? order.ContactName!.Trim() : (order.User?.FullName ?? order.DeliveryAddress?.FullName ?? "");
        var (billing, shipping, pickupLabel) = SterlingLams.Web.Services.OrderEmailTemplate.AddressBlocksFor(order, custName, customerEmail);
        var shippingLabel = await _zones.EmailShippingLabelAsync(order, pickupLabel);

        var subjectT = await _settings.GetAsync("email.new_order_admin.subject", "New order {order}");
        var introT = await _settings.GetAsync("email.new_order_admin.intro",
            "A new order has come in — full details below. View it in the admin dashboard under Orders.");
        var heading = subjectT.Replace("{order}", order.OrderNumber);
        var introHtml = SterlingLams.Web.Services.OrderEmailTemplate.ApplyPlaceholders(
                introT, "#" + order.OrderNumber, order.CreatedAt, custName)
            + (string.IsNullOrWhiteSpace(customerEmail) ? ""
               : $"<br/><span style=\"color:#78716c;font-size:13px;\">From {System.Net.WebUtility.HtmlEncode(customerEmail)}</span>");

        return SterlingLams.Web.Services.OrderEmailTemplate.Build(
            heading: heading, introHtml: introHtml, orderNumber: order.OrderNumber, orderDate: order.CreatedAt,
            items: items, subtotal: order.Subtotal, shippingLabel: shippingLabel, total: order.Total,
            paymentMethod: order.PaymentProvider ?? "—", billingLines: billing, shippingLines: shipping);
    }

    /// <summary>Increments the global usage count on the discount code an order used.</summary>
    private async Task IncrementDiscountUsageAsync(Order order)
    {
        if (string.IsNullOrEmpty(order.DiscountCode)) return;
        try
        {
            var dc = await _db.DiscountCodes.FirstOrDefaultAsync(d => d.Code == order.DiscountCode);
            if (dc != null)
            {
                dc.UsedCount++;
                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to increment discount usage for {Code}: {Message}",
                order.DiscountCode, ex.Message);
        }
    }


    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Confirmation(string orderNumber, string? token = null)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.PickupStore)
            .Include(o => o.DeliveryAddress)
            .Include(o => o.User)   // email, for the Meta server-side purchase match below
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);

        if (order == null) return NotFound();

        // Authorise the viewer: the signed-in owner, or anyone holding the order's confirmation
        // token (issued only on the post-payment redirect). Without this, an anonymous visitor
        // could read any order's PII (name/address/phone) just by guessing the order number.
        var userId = _userManager.GetUserId(User);
        var isOwner = userId != null && order.UserId == userId;
        if (!isOwner && !ConfirmationTokenValid(orderNumber, token))
            return NotFound();

        await ReportPurchaseToMetaAsync(order);
        return View(order);
    }

    /// <summary>
    /// Reports the sale to Meta from the server, as well as the pixel firing in the page. The browser
    /// copy is lost to ad blockers and iOS tracking protection on a large share of visitors; this one
    /// leaves our own machine, so the conversion is still credited to the ad that earned it. Both carry
    /// the same event id, so Meta counts the sale once however many copies reach it — which also makes
    /// this safe if the customer refreshes or revisits the page.
    /// </summary>
    private async Task ReportPurchaseToMetaAsync(Order order)
    {
        try
        {
            // Capped well under Meta's own timeout: this runs before the confirmation page is returned,
            // and someone who has just paid should never be left waiting on an ad platform. Losing the
            // event costs attribution; delaying the page costs trust.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await HttpContext.RequestServices
                .GetRequiredService<SterlingLams.Web.Services.Marketing.IMetaConversionsApi>()
                .SendPurchaseAsync(order, HttpContext, cts.Token);
        }
        catch { /* analytics must never break the confirmation page */ }
    }

    // Cart is persisted in a durable 30-day cookie (see CartStore), not volatile server session,
    // so it survives redeploys and long browsing sessions.
    private Task<CartViewModel> GetCartAsync() => CartStore.LoadAsync(HttpContext, _db);

    private void SaveCart(CartViewModel cart) => CartStore.Save(HttpContext, cart);

    /// <summary>Upserts the abandoned-cart snapshot at checkout — now shared with add-to-cart capture
    /// via <see cref="SterlingLams.Web.Services.IAbandonedCartCapture"/>.</summary>
    private async Task CaptureAbandonedCartAsync(string? email, CartViewModel cart) =>
        await HttpContext.RequestServices
            .GetRequiredService<SterlingLams.Web.Services.IAbandonedCartCapture>()
            .CaptureAsync(email, cart);

    /// <summary>Closes the abandoned-cart snapshot once payment lands, so a paid customer never gets a
    /// "you left something in your bag" reminder. Best-effort — never blocks the payment flow.</summary>
    private async Task MarkAbandonedRecoveredAsync(string? email)
    {
        try
        {
            await HttpContext.RequestServices
                .GetRequiredService<SterlingLams.Web.Services.IAbandonedCartCapture>()
                .MarkRecoveredAsync(email);
        }
        catch { /* recovery bookkeeping — never break checkout */ }
    }
}
