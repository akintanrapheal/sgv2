namespace SterlingLams.Web.Models.ViewModels;

/// <summary>Chrome for a dashboard chart card rendered by Views/Shared/_ChartCard.cshtml.
/// The chart itself is drawn by a small script in the page's @@section Scripts via the SLCharts
/// helper (wwwroot/js/sl-charts.js), targeting <see cref="Id"/>.</summary>
public class ChartCard
{
    /// <summary>The canvas element id the page's SLCharts.* call draws into.</summary>
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    /// <summary>Canvas height attribute (Chart.js keeps it responsive on width).</summary>
    public int Height { get; set; } = 90;
    /// <summary>When true the card shows <see cref="EmptyText"/> instead of a canvas.</summary>
    public bool Empty { get; set; }
    public string EmptyText { get; set; } = "No data to chart yet.";
    /// <summary>Extra classes for the outer card (e.g. column spans).</summary>
    public string WrapperClass { get; set; } = "";
}
