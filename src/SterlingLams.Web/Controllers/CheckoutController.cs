using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Models.ViewModels;
using SterlingLams.Web.Services.Payment;
using Microsoft.EntityFrameworkCore;

namespace SterlingLams.Web.Controllers;

public class CheckoutController : Controller
{
    private const string CartSessionKey = "cart";

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
        SterlingLams.Web.Services.IEmailService email)
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
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var cart = GetCart();
        if (cart.IsEmpty) return RedirectToAction("Index", "Cart");

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
                cart.IsAutomaticDiscount = true;
                SaveCart(cart);
            }
        }

        var stores = await _db.Stores.Where(s => s.IsActive).ToListAsync();

        // Build delivery pricing JSON for client-side zone detection
        var lagosExpressFee   = await _settings.GetDecimalAsync("shipping.lagos_abuja_express_fee",  4000);
        var lagosExpressDays  = await _settings.GetAsync("shipping.lagos_abuja_express_days",        "24 - 48 hours");
        var lagosStdFee       = await _settings.GetDecimalAsync("shipping.lagos_abuja_standard_fee", 2000);
        var lagosStdDays      = await _settings.GetAsync("shipping.lagos_abuja_standard_days",       "2 - 4 working days");
        var natStdFee         = await _settings.GetDecimalAsync("shipping.national_standard_fee",    7500);
        var natStdDays        = await _settings.GetAsync("shipping.national_standard_days",          "2 - 5 working days");

        var pricingJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            lagosAbuja = new[]
            {
                new { type = "Express",  label = "Express Delivery",  fee = lagosExpressFee, timeframe = lagosExpressDays },
                new { type = "Standard", label = "Standard Delivery", fee = lagosStdFee,     timeframe = lagosStdDays     },
            },
            national = new[]
            {
                new { type = "Standard", label = "Standard Delivery", fee = natStdFee, timeframe = natStdDays },
            },
            lagosLGAs       = SterlingLams.Web.Services.DeliveryZoneService.LagosLGAs,
            abujaKeywords   = new[] { "FCT", "Abuja", "Federal Capital" },
        });

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
            PaystackPublicKey = _config["Payment:Paystack:PublicKey"],
            AvailableStores = stores.Select(s => new StorePickupOptionViewModel
            {
                StoreId = s.Id,
                StoreName = s.Name,
                Address = s.Address,
                OpeningHours = s.OpeningHours,
                AllItemsAvailable = true
            }).ToList()
        };

        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PlaceOrder(CheckoutViewModel vm)
    {
        if (!ModelState.IsValid) return View("Index", vm);

        var cart = GetCart();
        if (cart.IsEmpty) return RedirectToAction("Index", "Cart");

        // ── Resolve user (authenticated or guest) ──────────────────────────
        ApplicationUser? user = await _userManager.GetUserAsync(User);

        if (user == null)
        {
            // Guest checkout: require contact fields
            if (string.IsNullOrWhiteSpace(vm.GuestEmail))
            {
                ModelState.AddModelError("GuestEmail", "Please enter your email address.");
                vm.Cart = cart;
                vm.AvailableStores = (await _db.Stores.Where(s => s.IsActive).ToListAsync())
                    .Select(s => new StorePickupOptionViewModel { StoreId = s.Id, StoreName = s.Name, Address = s.Address, OpeningHours = s.OpeningHours, AllItemsAvailable = true }).ToList();
                return View("Index", vm);
            }

            // Look up existing user by email or create a guest account
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
                    PhoneNumber = vm.GuestPhone,
                    CreatedAt = DateTime.UtcNow
                };
                var createResult = await _userManager.CreateAsync(user, Guid.NewGuid().ToString("N") + "Aa1!");
                if (!createResult.Succeeded)
                {
                    ModelState.AddModelError("", "Unable to process guest checkout. Please try again.");
                    vm.Cart = cart;
                    vm.AvailableStores = (await _db.Stores.Where(s => s.IsActive).ToListAsync())
                        .Select(s => new StorePickupOptionViewModel { StoreId = s.Id, StoreName = s.Name, Address = s.Address, OpeningHours = s.OpeningHours, AllItemsAvailable = true }).ToList();
                    return View("Index", vm);
                }
                _logger.LogInformation("Guest account created for checkout: {Email}", vm.GuestEmail);
            }
        }

        // Validate store selection for pickup orders
        if (vm.FulfillmentType == FulfillmentChoice.StorePickup)
        {
            if (vm.SelectedStoreId == null || !await _db.Stores.AnyAsync(s => s.Id == vm.SelectedStoreId && s.IsActive))
            {
                ModelState.AddModelError("SelectedStoreId", "Please select a valid store for pickup.");
                vm.Cart = cart;
                vm.AvailableStores = (await _db.Stores.Where(s => s.IsActive).ToListAsync())
                    .Select(s => new StorePickupOptionViewModel { StoreId = s.Id, StoreName = s.Name, Address = s.Address, OpeningHours = s.OpeningHours, AllItemsAvailable = true }).ToList();
                return View("Index", vm);
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
            var dr = cart.IsAutomaticDiscount
                ? await _discounts.FindAutomaticAsync(cart, user.Id)
                : await _discounts.EvaluateAsync(cart.AppliedDiscountCode, cart, user.Id);
            if (dr != null && dr.Success)
            {
                discountCode   = dr.Code;
                discountAmount = dr.Amount;
                freeShipping   = dr.FreeShipping;
            }
        }

        // Calculate delivery fee server-side (never trust client-submitted amount)
        decimal deliveryFee = 0;
        if (vm.FulfillmentType == FulfillmentChoice.Delivery)
            deliveryFee = await _zones.CalculateFeeAsync(vm.DeliveryAddress.State, vm.SelectedDeliveryType);
        if (freeShipping) deliveryFee = 0;   // free-shipping discount waives the fee

        // Build order
        var orderNumber = $"SL-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Random.Shared.Next(1000, 9999)}";

        var order = new Order
        {
            OrderNumber = orderNumber,
            UserId = user.Id,
            FulfillmentType = vm.FulfillmentType == FulfillmentChoice.StorePickup
                ? FulfillmentType.StorePickup
                : FulfillmentType.Delivery,
            PickupStoreId = vm.FulfillmentType == FulfillmentChoice.StorePickup ? vm.SelectedStoreId : null,
            Subtotal = cart.Subtotal,
            DeliveryFee = deliveryFee,
            DiscountCode = discountCode,
            DiscountAmount = discountAmount,
            Total = cart.Subtotal - discountAmount + deliveryFee,
            Items = cart.Items.Select(i => new OrderItem
            {
                ProductId = i.ProductId,
                ProductVariantId = i.VariantId,
                ProductName = i.ProductName,
                VariantName = i.VariantName,
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

        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        // Reserve the stock (atomic hold across branches). If another order claimed the last
        // units between cart and here, this fails — cancel and send the customer back to the bag.
        if (!await _fulfilment.TryReserveAsync(order.Id))
        {
            order.Status = OrderStatus.Cancelled;
            await _db.SaveChangesAsync();
            TempData["Error"] = "Sorry — one or more items just sold out. Please review your bag.";
            return RedirectToAction("Index", "Cart");
        }

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
            return View("Index", vm);
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
            order.IsPaid = true;
            order.PaidAt = DateTime.UtcNow;
            order.Status = OrderStatus.Confirmed;
            order.PaymentReference = refToVerify;
            order.PaymentProvider = _payment.ProviderName;
            await _db.SaveChangesAsync();

            await IncrementDiscountUsageAsync(order);

            // Deduct stock through the in-house ledger (multi-branch fulfilment).
            await _fulfilment.FulfilPaidOrderAsync(order.Id);

            await SendOrderEmailsAsync(order.Id);
        }

        // Clear cart
        HttpContext.Session.Remove(CartSessionKey);

        return RedirectToAction("Confirmation", new { orderNumber = result.OrderNumber });
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

        await IncrementDiscountUsageAsync(order);

        // Deduct stock through the in-house ledger (multi-branch fulfilment).
        await _fulfilment.FulfilPaidOrderAsync(order.Id);

        await SendOrderEmailsAsync(order.Id);

        HttpContext.Session.Remove(CartSessionKey);
        return RedirectToAction("Confirmation", new { orderNumber = order.OrderNumber });
    }

    /// <summary>
    /// Emails the customer an order confirmation and (optionally) alerts the admin of a new order.
    /// Respects the Notifications settings toggles. Never throws — email must not break checkout.
    /// </summary>
    private async Task SendOrderEmailsAsync(int orderId)
    {
        try
        {
            var order = await _db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return;

            var customerEmail = await _db.Users.Where(u => u.Id == order.UserId).Select(u => u.Email).FirstOrDefaultAsync();

            string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
            var rows = string.Join("", order.Items.Select(i => $@"
                <tr>
                    <td style=""padding:8px 0;border-bottom:1px solid #f0efed;"">{Enc(i.ProductName)}{(string.IsNullOrWhiteSpace(i.VariantName) ? "" : " — " + Enc(i.VariantName))} &times; {i.Quantity}</td>
                    <td align=""right"" style=""padding:8px 0;border-bottom:1px solid #f0efed;white-space:nowrap;"">&#8358;{i.LineTotal:N0}</td>
                </tr>"));
            var summary = $@"
                <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin:20px 0;font-size:14px;"">
                    {rows}
                    <tr><td style=""padding:12px 0 0;font-weight:bold;"">Total</td><td align=""right"" style=""padding:12px 0 0;font-weight:bold;"">&#8358;{order.Total:N0}</td></tr>
                </table>";

            // Customer confirmation
            if (!string.IsNullOrWhiteSpace(customerEmail)
                && await _settings.GetBoolAsync("notifications.order_confirmed", true))
            {
                var body = $@"
                    <h2 style=""font-size:18px;margin:0 0 16px;"">Thank you for your order</h2>
                    <p>Your order <strong>{Enc(order.OrderNumber)}</strong> has been confirmed and is being prepared. Here's a summary:</p>
                    {summary}
                    <p style=""font-size:13px;color:#78716c;"">We'll be in touch with delivery or pickup details. Thank you for shopping with us.</p>";
                await _email.SendAsync(customerEmail!, $"Order {order.OrderNumber} confirmed", body, ct: HttpContext.RequestAborted);
            }

            // Admin new-order alert
            if (await _settings.GetBoolAsync("notifications.new_order", true))
            {
                var adminEmail = await _settings.GetAsync("notifications.admin_email", "");
                if (!string.IsNullOrWhiteSpace(adminEmail))
                {
                    var body = $@"
                        <h2 style=""font-size:18px;margin:0 0 16px;"">New order received</h2>
                        <p>Order <strong>{Enc(order.OrderNumber)}</strong>{(string.IsNullOrWhiteSpace(customerEmail) ? "" : " from " + Enc(customerEmail))}.</p>
                        {summary}
                        <p style=""font-size:13px;color:#78716c;"">View it in the admin dashboard under Orders.</p>";
                    await _email.SendAsync(adminEmail, $"New order {order.OrderNumber} — ₦{order.Total:N0}", body, ct: HttpContext.RequestAborted);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed sending order emails for order {OrderId}", orderId);
        }
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
    public async Task<IActionResult> Confirmation(string orderNumber)
    {
        var userId = _userManager.GetUserId(User);
        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.PickupStore)
            .Include(o => o.DeliveryAddress)
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber
                && (userId == null || o.UserId == userId));

        if (order == null) return NotFound();

        return View(order);
    }

    private CartViewModel GetCart()
    {
        var json = HttpContext.Session.GetString(CartSessionKey);
        if (string.IsNullOrEmpty(json)) return new CartViewModel();
        return JsonSerializer.Deserialize<CartViewModel>(json) ?? new CartViewModel();
    }

    private void SaveCart(CartViewModel cart) =>
        HttpContext.Session.SetString(CartSessionKey, JsonSerializer.Serialize(cart));
}
