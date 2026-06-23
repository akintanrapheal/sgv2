namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// One tender against a POS sale. A normal sale has a single row; a split/mixed payment has several
/// (e.g. part cash, part card). <see cref="Amount"/> is the money that method contributed to the
/// order total — for cash this is what stays in the drawer (change is excluded), so summing the cash
/// rows gives the true drawer figure for cash-up. Legacy orders (before split payments) have no rows
/// and fall back to <see cref="Order.PaymentProvider"/>.
/// </summary>
public class OrderPayment
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;

    /// <summary>Cash / Card / Transfer.</summary>
    public string Method { get; set; } = "Cash";

    public decimal Amount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
