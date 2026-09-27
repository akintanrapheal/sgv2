using System.Text.Json;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Infrastructure;

/// <summary>Parses the <c>lookbook.campaigns</c> JSON setting into campaign objects (shared by the admin
/// editor and the storefront lookbook page).</summary>
public static class LookbookData
{
    public static List<LookbookCampaign> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<LookbookCampaign>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch { return new(); }
    }
}
