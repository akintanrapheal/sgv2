using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Models.ViewModels;

/// <summary>Values for the shared per-recipient coupon editor partial (campaigns + automations).
/// The inputs post back with the same names both entities bind (CouponEnabled, CouponType, …).</summary>
public record CouponFieldsVm(bool Enabled, DiscountType Type, decimal Value, int ExpiryDays, decimal? MinOrder);
