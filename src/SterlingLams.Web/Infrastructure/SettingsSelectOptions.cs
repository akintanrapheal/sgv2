namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Option lists for settings rendered as a dropdown (Type = "select") in Admin → Settings. Keyed by
/// setting key. Also the source of truth for validating a posted value on the storefront side.
/// </summary>
public static class SettingsSelectOptions
{
    public static readonly IReadOnlyDictionary<string, (string Value, string Label)[]> Map =
        new Dictionary<string, (string Value, string Label)[]>
        {
            ["home.diamond.animation"] = new[]
            {
                ("glow",  "Glow & Sparkle"),
                ("pulse", "Pulse (grow & shrink)"),
                ("float", "Float (gentle bob)"),
                ("spin",  "Spin"),
                ("none",  "Static (no motion)"),
            },
        };

    public static (string Value, string Label)[] For(string key) =>
        Map.TryGetValue(key, out var opts) ? opts : System.Array.Empty<(string, string)>();

    /// <summary>Returns <paramref name="value"/> if it's a valid option for the key, else the first option.</summary>
    public static string Coerce(string key, string? value)
    {
        var opts = For(key);
        if (opts.Length == 0) return value ?? "";
        return opts.Any(o => o.Value == value) ? value! : opts[0].Value;
    }
}
