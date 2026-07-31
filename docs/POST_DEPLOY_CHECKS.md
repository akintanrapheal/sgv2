# Post-deploy checks — admin & POS review, 31 July 2026

Commits `21cf785` → `8415397`. Several of these change figures you rely on, so the point of this list
is to confirm the new numbers are *right*, not just that pages load. Work through it over a few
normal trading days; each check says what to expect and what it means if it's wrong.

---

## 1. Refunds — the one that was costing money

**What changed:** a refund now pays back only what the customer actually *tendered*. Points and
gift-card balances are returned separately, so their cash value is no longer also refunded.

**Check:** find (or make) an online order where the customer redeemed loyalty points or paid part
with a gift card. Refund it in full.

| | Before | Now |
|---|---|---|
| ₦20,000 order, ₦8,000 paid by gift card | refunded ₦20,000 cash **and** put ₦8,000 back on the card | refunds ₦12,000 cash, ₦8,000 back on the card |

**Expected:** cash refunded = order total − points value − gift-card amount. The order note on the
timeline states the amount and whether stock was restocked.

**If it's wrong:** stop and tell me the order number, the loyalty/gift-card amounts and what it
refunded. Don't process more refunds on redeemed orders until it's sorted.

## 2. Cash-up — tenders must equal the sale

**What changed:** recorded tenders always sum to the sale total; change is no longer counted as
takings.

**Check:** take one sale on a split tender (part cash, part card) where the customer overpays the
cash portion and gets change. Then close the till.

**Expected:** on the Z-report the cash figure is what's physically in the drawer — cash handed over
*minus* the change you gave. Expected drawer = opening float + cash sales − cash refunds + cash in −
cash out, and your count should match it.

**If it's wrong:** note the sale number and the exact tender amounts before the shift closes.

## 3. Finance — revenue is dated by payment, in Lagos days

**What changed:** revenue counts on the day the *money arrived*, not the day the order was placed;
and a reporting day now runs midnight-to-midnight Lagos time instead of 01:00–01:00.

**Check:** open Finance for a month you know well.

**Expected:** totals differ slightly from any figure you recorded before this deploy. That's the fix
working, not a fault. Specifically:
- A bank transfer placed one day and confirmed another now counts on the **confirmation** day.
- Sales between midnight and 1am now count on the day they actually happened.

**Worth knowing:** figures won't reconcile against exports taken before 31 July. If you need a
like-for-like comparison for a past period, tell me and I'll explain the difference for that range.

## 4. Finance → Leakage — till loyalty is split out

**Check:** take a till sale where the customer redeems points, then open Finance → Leakage.

**Expected:** the redemption appears under **Loyalty**, not Discounts. Before, the POS always showed
₦0 loyalty and the amount sat in Discounts. The overall giveaway total is unchanged.

## 5. Reports → Stock — headline is now cost, not retail

**Expected:** the first tile is stock valued at **cost** (smaller than before); retail sits beside it
labelled "if it all sold at list price". If an amber note says some lines have no cost price, those
are valued at retail and the total is overstated until you fill those in — worth doing before you
give the figure to anyone.

## 6. Reports → Sales, filtered by branch

**Expected:** more orders than before. It was only counting orders collected *at* that branch and
dropping online deliveries the branch fulfilled. It should now agree with Finance's branch table.

## 7. Inventory — the originals from this session

- **Track Stock on a newly created product** → saves instead of "Save failed."
- **Admin → Inventory** → a product with options shows an indigo `N variants` badge with its option
  rows indented underneath, each with its own branch boxes.
- **Admin → Inventory search** → a SKU finds the product.
- **Admin → Products** → the Sale Price column, and the list loads faster (it was fetching
  full-resolution photos for thumbnails).

## 8. Quick sanity sweep

- Order detail → note timestamps read an hour later than before (they were showing UTC).
- Any CSV export opens with columns aligned, including names containing quotes or commas.
- Admin → Integrations still opens for you. **If a staff role had that section granted, it no longer
  does** — payment keys and SMTP are now full-administrator only, by design.

---

## Something looks wrong?

Note the screen, the exact figures, and the order/sale number, then raise it. For anything involving
money — a refund amount, a drawer that won't balance — capture it **before** the session closes or
the record is amended; the original numbers are much easier to reason about than a corrected one.
