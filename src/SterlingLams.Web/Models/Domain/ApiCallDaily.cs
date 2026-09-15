namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// Persistent daily tally of internal API/AJAX calls, one row per (day, label) where label is a store
/// name for POS traffic or "Online" for the storefront. Written in batched flushes (not per request)
/// by <see cref="SterlingLams.Web.Infrastructure.ApiCallPersistenceService"/>. Powers the historical
/// "API calls over time" chart (day / week / month). Day is the Lagos (WAT) calendar date.
/// </summary>
public class ApiCallDaily
{
    public int Id { get; set; }
    public DateOnly Day { get; set; }
    public string Label { get; set; } = "";
    public int Count { get; set; }
}
