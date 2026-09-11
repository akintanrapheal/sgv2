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

            foreach (var r in rows)
            {
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
                    _log.LogWarning("Customer import: create failed for {Email}: {Errors}",
                        email, string.Join("; ", res.Errors.Select(e => e.Description)));
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
