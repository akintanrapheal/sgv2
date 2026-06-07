using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Models.ViewModels;
using SterlingLams.Web.Services.ERPNext;
using SterlingLams.Web.Services.ERPNext.ERPNextModels;
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
    private readonly IERPNextService _erpNext;
    private readonly SterlingLams.Web.Services.ISettingsService _settings;
    private readonly SterlingLams.Web.Services.DeliveryZoneService _zones;
    private readonly SterlingLams.Web.Services.IDiscountService _discounts;

    public CheckoutController(
        ApplicationDbContext db,
        IPaymentService payment,
        UserManager<ApplicationUser> userManager,
        ILogger<CheckoutController> logger,
        IConfiguration config,
        IWebHostEnvironment env,
        IERPNextService erpNext,
        SterlingLams.Web.Services.ISettingsService settings,
        SterlingLams.Web.Services.DeliveryZoneService zones,
        SterlingLams.Web.Services.IDiscountService discounts)
    {
        _db = db;
        _payment = payment;
        _userManager = userManager;
        _logger = logger;
        _config = config;
        _env = env;
        _erpNext = erpNext;
        _settings = settings;
        _zones = zones;
        _discounts = discounts;
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

            // Push stock deduction to ERPNext
            await PushOrderToERPNextAsync(order);
        }

        // Clear cart
        HttpContext.Session.Remove(CartSessionKey);

        return RedirectToAction("Confirmation", new { orderNumber = result.OrderNumber });
    }

    /// <summary>
    /// DEV ONLY — simulates a successful payment, creates ERPNext sales order, and confirms the order.
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

        // Push to ERPNext (SO + stock deduction)
        await PushOrderToERPNextAsync(order);

        HttpContext.Session.Remove(CartSessionKey);
        return RedirectToAction("Confirmation", new { orderNumber = order.OrderNumber });
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

    /// <summary>
    /// Pushes a confirmed website order to ERPNext as a Sales Invoice with update_stock=1.
    /// This is the same document type ERPNext POS creates, so stock levels stay perfectly
    /// in sync between the website and the POS terminal.
    /// Failures are logged but never thrown — order confirmation must not be blocked.
    /// </summary>
    private async Task PushOrderToERPNextAsync(Order order)
    {
        try
        {
            // Reload with items, store, user, and address if not already included
            order = await _db.Orders
                .Include(o => o.Items)
                .Include(o => o.PickupStore)
                .Include(o => o.User)
                .FirstAsync(o => o.Id == order.Id);

            // Determine which warehouse to deduct from:
            //   • Pickup orders  → the chosen store's warehouse
            //   • Delivery orders → the first active store (acts as fulfilment hub)
            string warehouse;
            if (order.PickupStore?.ErpNextWarehouse is { Length: > 0 } pw)
            {
                warehouse = pw;
            }
            else
            {
                var hub = await _db.Stores.Where(s => s.IsActive).FirstOrDefaultAsync();
                warehouse = hub?.ErpNextWarehouse ?? "Sterlin Glams Abuja - SG";
            }

            // Map product IDs → ERPNext item codes (skip products with no code)
            var productIds = order.Items.Select(i => i.ProductId).Distinct().ToList();
            var itemCodes  = await _db.Products
                .Where(p => productIds.Contains(p.Id) && !string.IsNullOrEmpty(p.ErpNextItemCode))
                .ToDictionaryAsync(p => p.Id, p => p.ErpNextItemCode);

            var invoiceItems = order.Items
                .Where(i => itemCodes.ContainsKey(i.ProductId))
                .Select(i => new ERPNextInvoiceItem
                {
                    ItemCode  = itemCodes[i.ProductId],
                    Warehouse = warehouse,
                    Qty       = i.Quantity,
                    Rate      = i.UnitPrice,
                })
                .ToList();

            if (invoiceItems.Count == 0)
            {
                _logger.LogWarning("No ERPNext item codes found for order {OrderNumber} — skipping ERPNext push.",
                    order.OrderNumber);
                return;
            }

            // Build a rich remarks string so the invoice is identifiable in ERPNext
            var customerName  = order.User?.FullName ?? "Website Customer";
            var customerEmail = order.User?.Email ?? "";
            var fulfillment   = order.FulfillmentType == FulfillmentType.StorePickup
                ? $"Pickup: {order.PickupStore?.Name ?? "Store"}"
                : "Delivery";

            var remarks = $"Website order {order.OrderNumber} | {fulfillment} | {customerName} <{customerEmail}>";

            var invoiceName = await _erpNext.CreateSalesInvoiceAsync(new ERPNextSalesInvoiceRequest
            {
                Customer = _config["ERPNext:DefaultCustomer"] ?? "Walk-In Customer",
                PoNo     = order.OrderNumber,   // visible in ERPNext PO No column for easy search
                Remarks  = remarks,
                Items    = invoiceItems,
            });

            order.ErpNextInvoiceName = invoiceName;
            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "ERPNext Sales Invoice {Invoice} created for order {OrderNumber} (warehouse: {Warehouse})",
                invoiceName, order.OrderNumber, warehouse);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("ERPNext push failed for order {OrderNumber}: {Message}",
                order.OrderNumber, ex.Message);
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
