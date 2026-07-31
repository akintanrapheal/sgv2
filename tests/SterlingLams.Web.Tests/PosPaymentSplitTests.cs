using SterlingLams.Web.Controllers;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>
/// Every till sale records what was tendered, per method, in OrderPayments. Those rows are what the
/// Z-report totals and what Finance splits its payment channels by, so they must sum to the sale
/// total — change handed back is not revenue. Drives PosController.BuildPayments directly.
/// </summary>
public class PosPaymentSplitTests
{
    private static List<PosController.PaymentPart> Parts(params (string Method, decimal Amount)[] parts) =>
        parts.Select(p => new PosController.PaymentPart { Method = p.Method, Amount = p.Amount }).ToList();

    [Fact]
    public void A_single_cash_tender_records_the_sale_total_and_the_change()
    {
        var (rows, provider, tendered, change) =
            PosController.BuildPayments(null, "Cash", fallbackTendered: 25_000m, total: 20_000m);

        Assert.Equal(20_000m, rows.Sum(r => r.Amount));
        Assert.Equal("Cash", provider);
        Assert.Equal(25_000m, tendered);
        Assert.Equal(5_000m, change);
    }

    [Fact]
    public void A_split_that_exactly_covers_the_sale_keeps_both_rows()
    {
        var (rows, provider, _, change) =
            PosController.BuildPayments(Parts(("Cash", 5_000m), ("Card", 15_000m)), "Cash", 0m, 20_000m);

        Assert.Equal(20_000m, rows.Sum(r => r.Amount));
        Assert.Equal("Split", provider);
        Assert.Equal(0m, change);
        Assert.Equal(15_000m, rows.Single(r => r.Method == "Card").Amount);
    }

    [Fact]
    public void Change_comes_out_of_the_cash_row_so_the_drawer_reconciles()
    {
        // ₦20,000 sale: ₦15,000 on card, ₦10,000 cash handed over, ₦5,000 change back.
        var (rows, _, tendered, change) =
            PosController.BuildPayments(Parts(("Card", 15_000m), ("Cash", 10_000m)), "Cash", 0m, 20_000m);

        Assert.Equal(20_000m, rows.Sum(r => r.Amount));
        Assert.Equal(5_000m, rows.Single(r => r.Method == "Cash").Amount);   // what stays in the drawer
        Assert.Equal(15_000m, rows.Single(r => r.Method == "Card").Amount);
        Assert.Equal(25_000m, tendered);
        Assert.Equal(5_000m, change);
    }

    [Fact]
    public void Overpaying_on_a_non_cash_tender_still_records_only_the_sale_total()
    {
        // No cash row to take the change out of. The excess used to stay on the record, inflating
        // the Z-report and the finance channel split.
        var (rows, _, tendered, change) =
            PosController.BuildPayments(Parts(("Transfer", 25_000m)), "Cash", 0m, 20_000m);

        Assert.Equal(20_000m, rows.Sum(r => r.Amount));
        Assert.Equal(25_000m, tendered);
        Assert.Equal(5_000m, change);
    }

    [Fact]
    public void Change_larger_than_the_cash_row_is_taken_off_the_other_tenders_too()
    {
        // ₦10,000 sale paid ₦2,000 cash + ₦13,000 card: ₦5,000 change exceeds the cash row.
        var (rows, _, _, change) =
            PosController.BuildPayments(Parts(("Cash", 2_000m), ("Card", 13_000m)), "Cash", 0m, 10_000m);

        Assert.Equal(10_000m, rows.Sum(r => r.Amount));
        Assert.Equal(5_000m, change);
        Assert.DoesNotContain(rows, r => r.Amount < 0);
    }

    [Fact]
    public void Zero_and_blank_tender_lines_are_dropped_before_anything_else()
    {
        var parts = Parts(("Cash", 20_000m), ("Card", 0m), ("", 5_000m));
        var (rows, provider, _, _) = PosController.BuildPayments(parts, "Cash", 0m, 20_000m);

        Assert.Single(rows);
        Assert.Equal("Cash", provider);
        Assert.Equal(20_000m, rows[0].Amount);
    }
}
