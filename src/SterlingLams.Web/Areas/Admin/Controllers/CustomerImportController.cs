using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    /// <summary>
    /// One-time importer for customers exported from the old WooCommerce site (the Webtoffee
    /// "Users/Customers" CSV, which carries <c>user_pass</c> — the phpass hash). Creates a native
    /// account per customer with the WordPress hash preserved, so they sign in with their EXISTING
    /// password and are transparently upgraded on first login (see WordPressPasswordHasher).
    /// Grantable section ("CustomerImport"): view to preview, "CustomerImport:manage" to Apply.
    /// (The owner account bypasses all section checks.) Dry-run preview first, then Apply.
    /// </summary>
    public class CustomerImportController : AdminBaseController
    {
        protected override string? Section => "CustomerImport"; // grantable; manage required to Apply

        private readonly UserManager<ApplicationUser> _users;
        private readonly ApplicationDbContext _db;
        private readonly ILogger<CustomerImportController> _log;

        public CustomerImportController(UserManager<ApplicationUser> users, ApplicationDbContext db,
            ILogger<CustomerImportController> log)
        {
            _users = users;
            _db = db;
            _log = log;
        }

        public IActionResult Index() => View();

        // Old WordPress staff roles — never import these as customers.
        private static readonly HashSet<string> StaffRoles = new(StringComparer.OrdinalIgnoreCase)
            { "administrator", "editor", "author", "shop_manager", "contributor" };

        [HttpPost]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Preview(IFormFile? file) => RunAsync(file, apply: false);

        [HttpPost]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Apply(IFormFile? file) => RunAsync(file, apply: true);

        private async Task<IActionResult> RunAsync(IFormFile? file, bool apply)
        {
            if (file == null || file.Length == 0)
                return Json(new { ok = false, error = "Please choose the exported customers CSV file." });

            List<Dictionary<string, string>> rows;
            try
            {
                await using var s = file.OpenReadStream();
                rows = ParseCsv(s);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = "Couldn't read the CSV: " + ex.Message });
            }

            // Dedupe against accounts that already exist (by normalised email) + within the file itself.
            var existing = new HashSet<string>(
                await _db.Users.Where(u => u.NormalizedEmail != null).Select(u => u.NormalizedEmail!).ToListAsync(),
                StringComparer.Ordinal);
            var seenInFile = new HashSet<string>(StringComparer.Ordinal);

            int wouldCreate = 0, created = 0, skippedExisting = 0, skippedStaff = 0,
                skippedNoEmail = 0, skippedNoHash = 0, failed = 0, withAddress = 0;
            var samples = new List<object>();
            var rowNum = 0;

            foreach (var r in rows)
            {
                rowNum++;   // 1-based row position, used for logging instead of the customer's email (PII)
                string G(params string[] keys)
                {
                    foreach (var k in keys)
                        if (r.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
                    return "";
                }

                var email = G("user_email", "billing_email");
                if (email.Length == 0 || !email.Contains('@')) { skippedNoEmail++; continue; }

                var roleSet = G("roles").Split(new[] { ',', ';', '|' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (roleSet.Any(StaffRoles.Contains)) { skippedStaff++; continue; }

                var hash = G("user_pass");
                if (!IsSupportedHash(hash)) { skippedNoHash++; continue; }

                var norm = _users.NormalizeEmail(email);
                if (existing.Contains(norm) || !seenInFile.Add(norm)) { skippedExisting++; continue; }

                var first = G("first_name", "billing_first_name");
                var last = G("last_name", "billing_last_name");
                var phone = G("billing_phone", "shipping_phone");
                var hasAddr = G("billing_address_1").Length > 0;
                if (hasAddr) withAddress++;

                if (samples.Count < 12)
                    samples.Add(new { email, name = $"{first} {last}".Trim(), phone, hasAddress = hasAddr });

                if (!apply) { wouldCreate++; continue; }

                // ── Apply: create the native account, carrying the WordPress hash ──
                DateTime.TryParse(G("user_registered"), out var reg);
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FirstName = first,
                    LastName = last,
                    PhoneNumber = phone.Length > 0 ? phone : null,
                    CreatedAt = reg == default ? DateTime.UtcNow : DateTime.SpecifyKind(reg, DateTimeKind.Utc),
                };

                var res = await _users.CreateAsync(user);
                if (!res.Succeeded)
                {
                    failed++;
                    // Don't log the customer's email (PII). The row number lets the admin find the
                    // failing record in their file without writing personal data to the logs.
                    _log.LogWarning("Customer import: create failed for row {Row}: {Errors}",
                        rowNum, string.Join("; ", res.Errors.Select(e => e.Description)));
                    continue;
                }

                // Preserve the phpass hash so the first login verifies + upgrades it.
                user.PasswordHash = hash;
                await _users.UpdateAsync(user);

                if (hasAddr)
                {
                    _db.Addresses.Add(new Address
                    {
                        UserId = user.Id,
                        Label = "Billing",
                        FullName = $"{G("billing_first_name")} {G("billing_last_name")}".Trim(),
                        Phone = G("billing_phone"),
                        Line1 = G("billing_address_1"),
                        Line2 = G("billing_address_2").Length > 0 ? G("billing_address_2") : null,
                        City = G("billing_city"),
                        State = G("billing_state"),
                        Country = G("billing_country").Length > 0 ? G("billing_country") : "Nigeria",
                        PostalCode = G("billing_postcode").Length > 0 ? G("billing_postcode") : null,
                        IsDefault = true,
                    });
                    await _db.SaveChangesAsync();
                }

                existing.Add(norm);
                created++;
            }

            if (apply)
                await LogAsync("Import", "Customer", null,
                    $"Imported {created} customer(s) from CSV (skipped {skippedExisting} existing, "
                    + $"{skippedStaff} staff, {skippedNoEmail} no-email, {skippedNoHash} no-hash, {failed} failed).");

            return Json(new
            {
                ok = true,
                applied = apply,
                totalRows = rows.Count,
                wouldCreate = apply ? created : wouldCreate,
                created,
                existing = skippedExisting,
                staff = skippedStaff,
                noEmail = skippedNoEmail,
                noHash = skippedNoHash,
                failed,
                withAddress,
                samples,
            });
        }

        // ── EposNow loyalty-customer import ────────────────────────────────────────────────────────
        // Reconciles the old in-store (EposNow) customer list against the live customers (WordPress
        // imports + website + POS), by phone then email, so nobody is duplicated. For a match it fills
        // any BLANK name/phone/email (never overwrites) and records their loyalty points + transaction
        // stats; for a new contact it creates a phone-first account (no login) the same way POS does.
        // Expects the cleaned CSV: FirstName,LastName,Phone,Email,Points,TotalTransactions,LastTransactionDate,SignupDate.
        public IActionResult Loyalty() => View();

        [HttpPost, ValidateAntiForgeryToken]
        public Task<IActionResult> PreviewLoyalty(IFormFile? file) => RunLoyaltyAsync(file, apply: false);

        [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(60_000_000)]
        public Task<IActionResult> ApplyLoyalty(IFormFile? file) => RunLoyaltyAsync(file, apply: true);

        private static string NormPhone(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return "";
            var s = new string(p.Where(char.IsDigit).ToArray());
            if (s.StartsWith("234") && s.Length >= 12) s = "0" + s[3..];
            if (s.Length == 10) s = "0" + s;
            return s;
        }

        private static DateTime? ParseDt(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            // Clean CSV writes ISO "yyyy-MM-dd HH:mm:ss"; fall back to a general parse.
            if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d))
                return DateTime.SpecifyKind(d, DateTimeKind.Utc);
            return null;
        }

        private async Task<IActionResult> RunLoyaltyAsync(IFormFile? file, bool apply)
        {
            if (file == null || file.Length == 0)
                return Json(new { ok = false, error = "Please choose the cleaned EposNow customers CSV." });

            List<Dictionary<string, string>> rows;
            try { await using var s = file.OpenReadStream(); rows = ParseCsv(s); }
            catch (Exception ex) { return Json(new { ok = false, error = "Couldn't read the CSV: " + ex.Message }); }

            // Existing customers, minimal projection → phone/email/username lookups for matching + dedupe.
            var existing = await _db.Users
                .Select(u => new { u.Id, u.PhoneNumber, u.NormalizedEmail, u.NormalizedUserName })
                .ToListAsync();
            var phoneMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var emailMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var u in existing)
            {
                var np = NormPhone(u.PhoneNumber);
                if (np.Length == 11 && !phoneMap.ContainsKey(np)) phoneMap[np] = u.Id;
                if (!string.IsNullOrEmpty(u.NormalizedEmail)) emailMap.TryAdd(u.NormalizedEmail, u.Id);
                if (!string.IsNullOrEmpty(u.NormalizedUserName)) usedNames.Add(u.NormalizedUserName);
            }

            int wouldCreate = 0, wouldUpdate = 0, skippedNoContact = 0, pointCredits = 0;
            long totalPoints = 0;
            var newUsers = new List<ApplicationUser>();
            var newUserPoints = new Dictionary<string, (int pts, DateTime? last)>(StringComparer.Ordinal);
            var matchRows = new List<(string userId, string fn, string ln, string email, string phone, int pts, int tx, DateTime? last)>();
            var newEmails = new HashSet<string>(StringComparer.Ordinal);
            var samples = new List<object>();

            foreach (var r in rows)
            {
                string G(string k) => r.TryGetValue(k, out var v) ? (v ?? "").Trim() : "";
                var fn = G("FirstName"); var ln = G("LastName");
                var phoneRaw = G("Phone"); var email = G("Email").ToLowerInvariant();
                var ph = NormPhone(phoneRaw);
                var pts = int.TryParse(G("Points"), out var p) ? Math.Max(0, p) : 0;
                var tx = int.TryParse(G("TotalTransactions"), out var t) ? Math.Max(0, t) : 0;
                var last = ParseDt(G("LastTransactionDate"));
                var signup = ParseDt(G("SignupDate"));

                if (ph.Length != 11 && (email.Length == 0 || !email.Contains('@'))) { skippedNoContact++; continue; }
                var normEmail = email.Contains('@') ? _users.NormalizeEmail(email) : null;

                // Match an existing customer (phone first, then email) → update; else create new.
                string? matchId = (ph.Length == 11 && phoneMap.TryGetValue(ph, out var pid)) ? pid
                    : (normEmail != null && emailMap.TryGetValue(normEmail, out var eid)) ? eid : null;

                if (matchId != null)
                {
                    wouldUpdate++;
                    if (pts > 0) { pointCredits++; totalPoints += pts; }
                    if (apply) matchRows.Add((matchId, fn, ln, email, ph, pts, tx, last));
                    if (samples.Count < 12) samples.Add(new { action = "update", name = $"{fn} {ln}".Trim(), phone = ph, email, points = pts });
                    continue;
                }

                wouldCreate++;
                if (pts > 0) { pointCredits++; totalPoints += pts; }
                if (samples.Count < 12) samples.Add(new { action = "create", name = $"{fn} {ln}".Trim(), phone = ph, email, points = pts });

                if (!apply) continue;

                // Build a phone-first account (mirrors POS quick-add): unique username, no password.
                var canEmail = normEmail != null && !emailMap.ContainsKey(normEmail) && newEmails.Add(normEmail);
                var uname = canEmail ? email : (ph.Length == 11 ? $"pos-{ph}" : $"pos-{Guid.NewGuid():N}");
                var normU = _users.NormalizeName(uname);
                if (!usedNames.Add(normU)) { uname = $"pos-{Guid.NewGuid():N}"; normU = _users.NormalizeName(uname); usedNames.Add(normU); }

                var id = Guid.NewGuid().ToString();
                newUsers.Add(new ApplicationUser
                {
                    Id = id,
                    UserName = uname, NormalizedUserName = normU,
                    Email = canEmail ? email : null, NormalizedEmail = canEmail ? normEmail : null,
                    EmailConfirmed = canEmail,
                    FirstName = fn, LastName = ln,
                    PhoneNumber = ph.Length == 11 ? ph : null,
                    CreatedAt = signup ?? DateTime.UtcNow,
                    TotalTransactions = tx > 0 ? tx : null,
                    LastTransactionAt = last,
                    SecurityStamp = Guid.NewGuid().ToString("N"),
                    ConcurrencyStamp = Guid.NewGuid().ToString(),
                    LockoutEnabled = true,
                });
                if (ph.Length == 11) phoneMap[ph] = id;       // guard against later duplicate phones in-file
                if (canEmail) emailMap[normEmail!] = id;
                if (pts > 0 || last != null) newUserPoints[id] = (pts, last);
            }

            if (!apply)
                return Json(new
                {
                    ok = true, applied = false, totalRows = rows.Count,
                    wouldCreate, wouldUpdate, skippedNoContact,
                    pointCredits, totalPoints, samples
                });

            var now = DateTime.UtcNow;

            // ── Insert new customers in batches (bypasses UserManager for speed on tens of thousands) ──
            const int Batch = 500;
            int created = 0;
            for (int i = 0; i < newUsers.Count; i += Batch)
            {
                var slice = newUsers.Skip(i).Take(Batch).ToList();
                _db.Users.AddRange(slice);
                // Loyalty for the ones carrying points — account + a single migration ledger entry.
                foreach (var u in slice)
                    if (newUserPoints.TryGetValue(u.Id, out var np) && np.pts > 0)
                        _db.LoyaltyAccounts.Add(new LoyaltyAccount
                        {
                            UserId = u.Id, PointsBalance = np.pts, CreatedAt = now, UpdatedAt = now,
                            Entries = { new PointsLedgerEntry { Points = np.pts, Reason = "EposNow migration", CreatedAt = now } }
                        });
                await _db.SaveChangesAsync();
                _db.ChangeTracker.Clear();
                created += slice.Count;
            }

            // ── Update matched customers: fill blanks + set stats; credit points up to the imported total ──
            int updated = 0, credited = 0;
            var matchIds = matchRows.Select(m => m.userId).Distinct().ToList();
            for (int i = 0; i < matchIds.Count; i += Batch)
            {
                var idSlice = matchIds.Skip(i).Take(Batch).ToHashSet();
                var users = await _db.Users.Where(u => idSlice.Contains(u.Id)).ToListAsync();
                var accounts = (await _db.LoyaltyAccounts.Where(a => idSlice.Contains(a.UserId)).ToListAsync())
                    .ToDictionary(a => a.UserId);
                var byId = users.ToDictionary(u => u.Id);

                foreach (var m in matchRows.Where(m => idSlice.Contains(m.userId)))
                {
                    if (!byId.TryGetValue(m.userId, out var u)) continue;
                    // Fill blanks only — never overwrite existing contact data.
                    if (string.IsNullOrWhiteSpace(u.FirstName) && !string.IsNullOrWhiteSpace(m.fn)) u.FirstName = m.fn;
                    if (string.IsNullOrWhiteSpace(u.LastName) && !string.IsNullOrWhiteSpace(m.ln)) u.LastName = m.ln;
                    if (string.IsNullOrWhiteSpace(u.PhoneNumber) && m.phone.Length == 11) u.PhoneNumber = m.phone;
                    if (string.IsNullOrWhiteSpace(u.Email) && m.email.Contains('@'))
                    {
                        var ne = _users.NormalizeEmail(m.email);
                        if (!emailMap.ContainsKey(ne)) { u.Email = m.email; u.NormalizedEmail = ne; u.EmailConfirmed = true; emailMap[ne] = u.Id; }
                    }
                    // Transaction stats: take the higher count / more recent date.
                    if (m.tx > (u.TotalTransactions ?? 0)) u.TotalTransactions = m.tx;
                    if (m.last != null && (u.LastTransactionAt == null || m.last > u.LastTransactionAt)) u.LastTransactionAt = m.last;

                    // Points: credit up to the imported total; never reduce a balance already earned.
                    if (m.pts > 0)
                    {
                        if (!accounts.TryGetValue(m.userId, out var acc))
                        {
                            acc = new LoyaltyAccount { UserId = m.userId, PointsBalance = 0, CreatedAt = now, UpdatedAt = now };
                            _db.LoyaltyAccounts.Add(acc);
                            accounts[m.userId] = acc;
                        }
                        var delta = m.pts - acc.PointsBalance;
                        if (delta > 0)
                        {
                            acc.PointsBalance = m.pts; acc.UpdatedAt = now;
                            acc.Entries.Add(new PointsLedgerEntry { Points = delta, Reason = "EposNow migration", CreatedAt = now });
                            credited++;
                        }
                    }
                    updated++;
                }
                await _db.SaveChangesAsync();
                _db.ChangeTracker.Clear();
            }

            await LogAsync("Import", "Customer", null,
                $"EposNow loyalty import: created {created}, updated {updated} (credited {credited}), skipped {skippedNoContact} no-contact.");

            return Json(new
            {
                ok = true, applied = true, totalRows = rows.Count,
                created, updated, credited, skippedNoContact, totalPoints
            });
        }

        private static bool IsSupportedHash(string h) =>
            h.StartsWith("$P$", StringComparison.Ordinal) || h.StartsWith("$H$", StringComparison.Ordinal)
            || h.StartsWith("$2a$", StringComparison.Ordinal) || h.StartsWith("$2b$", StringComparison.Ordinal)
            || h.StartsWith("$2y$", StringComparison.Ordinal);

        // Robust CSV parse (quoted fields, embedded commas/newlines) — same approach as the product importer.
        private static List<Dictionary<string, string>> ParseCsv(Stream stream)
        {
            var rows = new List<Dictionary<string, string>>();
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            using var parser = new TextFieldParser(reader) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
            parser.SetDelimiters(",");
            if (parser.EndOfData) return rows;

            var headers = (parser.ReadFields() ?? Array.Empty<string>())
                .Select(h => h.Trim().TrimStart('﻿')).ToArray();

            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields() ?? Array.Empty<string>();
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < headers.Length && i < fields.Length; i++)
                    row[headers[i]] = fields[i];
                rows.Add(row);
            }
            return rows;
        }
    }
}
