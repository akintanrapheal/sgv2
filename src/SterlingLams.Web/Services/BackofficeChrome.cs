using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

/// <summary>
/// Supplies the current signed-in user's back-office "chrome" (avatar, name, initials, personal
/// accent colour) to the staff layouts. Scoped + memoised so a page render hits the DB at most once.
/// </summary>
public class BackofficeChrome
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly IHttpContextAccessor _http;
    private ApplicationUser? _user;
    private bool _loaded;

    public BackofficeChrome(UserManager<ApplicationUser> users, IHttpContextAccessor http)
    {
        _users = users;
        _http = http;
    }

    public async Task<ApplicationUser?> UserAsync()
    {
        if (_loaded) return _user;
        _loaded = true;
        var principal = _http.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated == true)
            _user = await _users.GetUserAsync(principal);
        return _user;
    }

    /// <summary>The user's chosen accent hex, or "" when unset/invalid (layouts then keep the default pink).</summary>
    public async Task<string> AccentAsync()
    {
        var a = (await UserAsync())?.ThemeAccent;
        return IsHex(a) ? a! : "";
    }

    public async Task<string?> AvatarAsync() => (await UserAsync())?.AvatarUrl;

    public async Task<string> NameAsync()
    {
        var u = await UserAsync();
        var n = u?.FullName;
        return string.IsNullOrWhiteSpace(n) ? (u?.UserName ?? "Account") : n!;
    }

    public async Task<string> EmailAsync() => (await UserAsync())?.Email ?? "";

    /// <summary>The staff member's back-office home (used by the "Back" link so it never falls to the
    /// storefront). Uses the configured secret <see cref="Infrastructure.StaffPaths"/> prefix + that
    /// backend's landing controller, so it resolves correctly even when the areas are hidden behind
    /// unguessable paths in production.</summary>
    public async Task<string> HomeAsync()
    {
        var u = await UserAsync();
        if (u == null) return "/";
        var roles = await _users.GetRolesAsync(u);
        if (roles.Contains("Admin")) return $"/{Infrastructure.StaffPaths.Admin}/Dashboard";
        if (roles.Contains("Inventory")) return $"/{Infrastructure.StaffPaths.Inventory}/Overview";
        if (roles.Contains("Social Media")) return $"/{Infrastructure.StaffPaths.Marketing}/Dashboard";
        return roles.Any() ? $"/{Infrastructure.StaffPaths.Admin}/Dashboard" : "/";
    }

    public async Task<string> InitialsAsync()
    {
        var u = await UserAsync();
        var f = (u?.FirstName ?? "").Trim();
        var l = (u?.LastName ?? "").Trim();
        var s = $"{(f.Length > 0 ? f[0] : ' ')}{(l.Length > 0 ? l[0] : ' ')}".Trim();
        if (!string.IsNullOrEmpty(s)) return s.ToUpperInvariant();
        var e = u?.Email ?? "?";
        return e.Length > 0 ? e[0].ToString().ToUpperInvariant() : "?";
    }

    public static bool IsHex(string? s) =>
        !string.IsNullOrEmpty(s) && Regex.IsMatch(s, "^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$");
}
