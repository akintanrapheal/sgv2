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
    private readonly SterlingLams.Web.Services.IStockService _stock;
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
        SterlingLams.Web.Services.IStockService stock,
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
        _stock = stock;
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

        // Authoritative overselling guard: total cart qty per product must not exceed the
        // combined on-hand across active branches (the engine can fulfil from any branch).
        var combinedStock = await _db.StoreInventories
            .Where(si => productIds.Contains(si.ProductId) && si.Store.IsActive)
            .GroupBy(si => si.ProductId)
            .Select(g => new { ProductId = g.Key, Qty = g.Sum(x => x.QuantityOnHand) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Qty);

        foreach (var grp in cart.Items.GroupBy(i => i.ProductId))
        {
            var wanted = grp.Sum(i => i.Quantity);
            combinedStock.TryGetValue(grp.Key, out var have);
            if (wanted > have)
            {
                TempData["Error"] = $"Only {have} of \"{grp.First().ProductName}\" {(have == 1 ? "is" : "are")} available. Please adjust your bag.";
                return RedirectToAction("Index", "Cart");
            }
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

            // Deduct stock through the in-house ledger (multi-branch fulfilment).
            await FulfilPaidOrderAsync(order);
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
        await FulfilPaidOrderAsync(order);

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
    /// Fulfils a paid online order against the in-house stock ledger (multi-branch).
    /// Picks the branch nearest the customer, transfers in any units it lacks from other
    /// branches (transfer-then-sell so every branch balance stays correct), then sells the
    /// whole order from that branch. Idempotent — does nothing if already fulfilled.
    /// Failures are logged, never thrown — the customer has already paid.
    /// </summary>
    private async Task FulfilPaidOrderAsync(Order order)
    {
        try
        {
            order = await _db.Orders
                .Include(o => o.Items)
                .Include(o => o.PickupStore)
                .Include(o => o.DeliveryAddress)
                .FirstAsync(o => o.Id == order.Id);

            // Idempotency: a fulfilled order already has its branch + ledger movements.
            if (order.FulfillingStoreId != null) return;

            var activeStores = await _db.Stores.Where(s => s.IsActive).ToListAsync();
            if (activeStores.Count == 0)
            {
                _logger.LogError("No active stores — cannot fulfil order {OrderNumber}.", order.OrderNumber);
                return;
            }

            // 1) Fulfilment branch: pickup → chosen store; delivery → nearest to the customer.
            var ranked = SterlingLams.Web.Services.DeliveryZoneService.RankStoresByProximity(
                activeStores, order.DeliveryAddress?.State, order.DeliveryAddress?.City);
            Store fulfilStore = (order.FulfillmentType == FulfillmentType.StorePickup
                    ? activeStores.FirstOrDefault(s => s.Id == order.PickupStoreId)
                    : null)
                ?? ranked.First();

            // Order of stores to pull from: fulfilment branch first, then nearest others.
            var storeOrder = new List<int> { fulfilStore.Id };
            storeOrder.AddRange(ranked.Where(s => s.Id != fulfilStore.Id).Select(s => s.Id));

            // 2) Allocate each line across branches (StoreInventory is per-product).
            var productIds = order.Items.Select(i => i.ProductId).Distinct().ToList();
            var storeIds = activeStores.Select(s => s.Id).ToList();
            var onHand = (await _db.StoreInventories
                    .Where(si => productIds.Contains(si.ProductId) && storeIds.Contains(si.StoreId))
                    .ToListAsync())
                .ToDictionary(si => (si.ProductId, si.StoreId), si => si.QuantityOnHand);
            int OnHand(int pid, int sid) => onHand.TryGetValue((pid, sid), out var q) ? q : 0;

            // transfers[(sourceStore, product)] = qty to move into the fulfilment branch
            var transfers = new Dictionary<(int store, int product), int>();
            foreach (var line in order.Items)
            {
                var need = line.Quantity;
                foreach (var sid in storeOrder)
                {
                    if (need <= 0) break;
                    var take = Math.Min(need, OnHand(line.ProductId, sid));
                    if (take <= 0) continue;
                    onHand[(line.ProductId, sid)] = OnHand(line.ProductId, sid) - take; // reserve so repeat products don't double-allocate
                    if (sid != fulfilStore.Id)
                    {
                        transfers.TryGetValue((sid, line.ProductId), out var acc);
                        transfers[(sid, line.ProductId)] = acc + take;
                    }
                    need -= take;
                }
                // 3) Shortfall (shouldn't happen — blocked at checkout): hold for staff, don't go negative.
                if (need > 0)
                {
                    order.AdminNotes = $"Fulfilment held {DateTime.UtcNow:yyyy-MM-dd HH:mm}: insufficient stock for {line.ProductName}.";
                    await _db.SaveChangesAsync();
                    _logger.LogWarning("Order {OrderNumber} held — short {Qty} of product {ProductId}.",
                        order.OrderNumber, need, line.ProductId);
                    return;
                }
            }

            // 4) Apply: transfers (remote → fulfilment) then the sale (whole order from fulfilment).
            var products = await _db.Products.Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name);
            var now = DateTime.UtcNow;
            await using var tx = await _db.Database.BeginTransactionAsync();

            foreach (var bySource in transfers.GroupBy(kv => kv.Key.store))
            {
                var sourceStore = activeStores.First(s => s.Id == bySource.Key);
                var transferNumber = $"TRF-{now:yyMMdd}-{now:HHmmssfff}-{sourceStore.Id}";
                var transfer = new StockTransfer
                {
                    TransferNumber = transferNumber,
                    FromStoreId = sourceStore.Id,
                    ToStoreId = fulfilStore.Id,
                    CreatedByUserId = order.UserId,
                    Note = $"Online order {order.OrderNumber}",
                    CreatedAt = now
                };
                foreach (var kv in bySource)
                {
                    var pid = kv.Key.product;
                    var qty = kv.Value;
                    transfer.Items.Add(new StockTransferItem
                    {
                        ProductId = pid,
                        ProductName = products.GetValueOrDefault(pid, $"#{pid}"),
                        Quantity = qty
                    });
                    await _stock.ApplyAsync(pid, null, sourceStore.Id, -qty, StockMovementType.Transfer,
                        transferNumber, $"To {fulfilStore.Name} (order {order.OrderNumber})", order.UserId);
                    await _stock.ApplyAsync(pid, null, fulfilStore.Id, qty, StockMovementType.Transfer,
                        transferNumber, $"From {sourceStore.Name} (order {order.OrderNumber})", order.UserId);
                }
                _db.StockTransfers.Add(transfer);
            }

            foreach (var line in order.Items)
                await _stock.ApplyAsync(line.ProductId, line.ProductVariantId, fulfilStore.Id,
                    -line.Quantity, StockMovementType.Sale, order.OrderNumber,
                    $"Online order {order.OrderNumber}", order.UserId);

            order.FulfillingStoreId = fulfilStore.Id;
            order.Status = order.FulfillmentType == FulfillmentType.StorePickup
                ? OrderStatus.ReadyForPickup
                : OrderStatus.Processing;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            _logger.LogInformation(
                "Order {OrderNumber} fulfilled from {Store} ({Transfers} transfer(s)).",
                order.OrderNumber, fulfilStore.Name, transfers.GroupBy(t => t.Key.store).Count());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fulfilment failed for order {OrderNumber}: {Message}",
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
