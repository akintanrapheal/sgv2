using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using SterlingLams.Web.Controllers;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>
/// Security regressions. Pen-test finding: the POS PIN sign-in and the admin change-register PIN
/// bypass Identity lockout (manual hash verify), so they MUST stay behind the per-IP "auth"
/// rate-limit policy or a 4–8 digit PIN becomes brute-forceable. These tests fail if the
/// [EnableRateLimiting("auth")] attribute is ever removed.
/// </summary>
public class SecurityTests
{
    // Checks (without a hard reference to the AspNetCore.RateLimiting assembly) that a method
    // carries [EnableRateLimiting("auth")].
    private static bool HasAuthRateLimit(MethodInfo? m)
        => m != null && m.GetCustomAttributes(inherit: true).Any(a =>
            a.GetType().Name == "EnableRateLimitingAttribute"
            && (a.GetType().GetProperty("PolicyName")?.GetValue(a) as string) == "auth");

    private static MethodInfo? Action(System.Type controller, string name, params System.Type[] argTypes)
        => controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == name
                && (argTypes.Length == 0 || m.GetParameters().Select(p => p.ParameterType).SequenceEqual(argTypes)));

    [Fact]
    public void PosController_Login_is_rate_limited()
        => Assert.True(HasAuthRateLimit(Action(typeof(PosController), "Login", typeof(string), typeof(string))),
            "PosController.Login must keep [EnableRateLimiting(\"auth\")] — PIN login bypasses Identity lockout.");

    [Fact]
    public void PosController_ChangeRegister_is_rate_limited()
        => Assert.True(HasAuthRateLimit(Action(typeof(PosController), "ChangeRegister", typeof(string))),
            "PosController.ChangeRegister must keep [EnableRateLimiting(\"auth\")] — admin PIN is brute-forceable otherwise.");

    [Fact]
    public void AccountController_Login_POST_is_rate_limited()
    {
        var post = typeof(AccountController).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "Login")
            .FirstOrDefault(m => m.GetParameters().Any(p => p.ParameterType.Name == "LoginViewModel"));
        Assert.True(HasAuthRateLimit(post), "AccountController.Login (POST) must keep [EnableRateLimiting(\"auth\")].");
    }

    // Double-spend / oversell invariant: stock can never be driven negative — the deduction throws
    // instead, so two concurrent sales of the last unit can't both succeed.
    [Fact]
    public async Task ApplyAsync_rejects_oversell_to_prevent_double_spend()
    {
        using var t = new TestDb();
        var store = t.SeedStore("Abuja", "Abuja", "Gwarimpa");
        var p = t.SeedProduct();
        t.SetStock(p.Id, store.Id, onHand: 1);

        var svc = new StockService(t.Db);
        await svc.ApplyAsync(p.Id, null, store.Id, -1, StockMovementType.Sale, "ORD-1");
        await t.Db.SaveChangesAsync();

        // The "second buyer" of the last unit must be rejected, not allowed to go negative.
        await Assert.ThrowsAsync<InsufficientStockException>(async () =>
        {
            await svc.ApplyAsync(p.Id, null, store.Id, -1, StockMovementType.Sale, "ORD-2");
            await t.Db.SaveChangesAsync();
        });
        Assert.Equal(0, t.Inv(p.Id, store.Id).QuantityOnHand);
    }
}
