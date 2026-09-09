using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Marketing.Controllers;

/// <summary>Refer-a-friend overview for the marketing team — totals + recent referrals.</summary>
public class ReferralsController : MarketingAreaController
{
    private readonly ApplicationDbContext _db;
    public ReferralsController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> Index(int page = 1)
    {
        const int pageSize = 40;
        ViewData["Title"] = "Referrals";
        ViewBag.Total = await _db.Referrals.CountAsync();
        ViewBag.Rewarded = await _db.Referrals.CountAsync(r => r.Status == ReferralStatus.Rewarded);
        ViewBag.PointsGiven = await _db.Referrals.Where(r => r.Status == ReferralStatus.Rewarded)
            .SumAsync(r => (int?)(r.ReferrerPoints + r.RefereePoints)) ?? 0;

        if (page < 1) page = 1;
        ViewBag.Page = page;
        ViewBag.TotalPages = (int)Math.Ceiling((int)ViewBag.Total / (double)pageSize);
        var rows = await _db.Referrals.AsNoTracking()
            .Include(r => r.Referrer).Include(r => r.Referee)
            .OrderByDescending(r => r.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new ReferralRow
            {
                Referrer = r.Referrer != null ? (r.Referrer.Email ?? r.Referrer.FullName) : r.ReferrerUserId,
                Referee = r.Referee != null ? (r.Referee.Email ?? r.Referee.FullName) : r.RefereeUserId,
                Code = r.Code,
                Status = r.Status.ToString(),
                Points = r.Status == ReferralStatus.Rewarded ? r.ReferrerPoints + r.RefereePoints : 0,
                CreatedAt = r.CreatedAt,
                RewardedAt = r.RewardedAt
            })
            .ToListAsync();
        return View(rows);
    }

    public class ReferralRow
    {
        public string Referrer { get; set; } = "";
        public string Referee { get; set; } = "";
        public string Code { get; set; } = "";
        public string Status { get; set; } = "";
        public int Points { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? RewardedAt { get; set; }
    }
}
