using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Controllers;

[Authorize]   // reviews require a signed-in customer
public class ReviewsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ISettingsService _settings;

    public ReviewsController(ApplicationDbContext db, ISettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int productId, int rating, string? title, string body, string? slug)
    {
        var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == productId && p.IsActive);
        if (product == null) return NotFound();
        var backSlug = string.IsNullOrWhiteSpace(slug) ? product.Slug : slug;

        if (!await _settings.GetBoolAsync("reviews.enabled", true))
        {
            TempData["ReviewError"] = "Reviews are currently disabled.";
            return RedirectToAction("Detail", "Products", new { slug = backSlug });
        }

        rating = Math.Clamp(rating, 1, 5);
        body = (body ?? "").Trim();
        if (body.Length < 3)
        {
            TempData["ReviewError"] = "Please write a short review before submitting.";
            return RedirectToAction("Detail", "Products", new { slug = backSlug });
        }

        var uid = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(uid)) return Challenge();

        // One review per customer per product.
        if (await _db.ProductReviews.AnyAsync(r => r.ProductId == productId && r.UserId == uid))
        {
            TempData["ReviewError"] = "You have already reviewed this product.";
            return RedirectToAction("Detail", "Products", new { slug = backSlug });
        }

        var user = await _db.Users.Where(u => u.Id == uid)
            .Select(u => new { u.FirstName, u.LastName, u.Email }).FirstOrDefaultAsync();
        var name = user == null ? "Customer"
            : ($"{user.FirstName} {user.LastName}".Trim() is { Length: > 0 } fn ? fn : (user.Email ?? "Customer"));

        // Verified buyer = a paid order by this customer that contains this product.
        var verified = await _db.OrderItems.AnyAsync(oi => oi.ProductId == productId
            && oi.Order.UserId == uid && oi.Order.IsPaid);

        var autoApprove = await _settings.GetBoolAsync("reviews.auto_approve", false);

        _db.ProductReviews.Add(new ProductReview
        {
            ProductId = productId,
            UserId = uid,
            AuthorName = name,
            Rating = rating,
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            Body = body,
            IsVerifiedBuyer = verified,
            IsApproved = autoApprove,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        TempData["ReviewSuccess"] = autoApprove
            ? "Thank you! Your review has been posted."
            : "Thank you! Your review has been submitted and will appear once approved.";
        return RedirectToAction("Detail", "Products", new { slug = backSlug });
    }
}
