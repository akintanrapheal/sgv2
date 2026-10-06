"""
Regenerates src/SterlingLams.Web/Infrastructure/LegacyProductSlugMap.cs.

Why this exists
---------------
The old WooCommerce site served products at /shop/{slug}/ and those URLs are still in Google's index.
After the migration, 514 of the 1,108 indexed product URLs no longer resolved to their product: 315 were
bounced to /products?search=... (a soft 404 - Google drops the URL and the ranking with it) and 199
returned a hard 404. Together they accounted for ~NGN 343M of order value in the 24 months before the
migration, so each one needs to land on its replacement with a single 301.

This script decides those targets from the pre-migration WordPress dump plus the live sitemap:

  * retired-but-renamed  -> /products/{new-slug}, matched STRICTLY (one slug's words contain the other's,
                            or >= 0.88 string similarity). Loose matching would drop a shopper on an
                            unrelated item, which Google also treats as a soft 404.
  * genuinely gone       -> /products?category={its old category}, a real indexable listing.
  * no safe target       -> left out of the map on purpose, so it 404s honestly.

Products that still exist under their original slug are NOT emitted: those 404d only because
"hide out of stock" used to 404 sold-out items, which ProductsController no longer does.

Usage
-----
    python generate_slug_map.py [path-to-database.sql] [path-to-export-dir]

The export dir is where audit_* CSVs from the URL audit live; it also receives redirect_map_final.csv
(the reviewable version of what was written into the .cs file). Re-run after a catalogue re-slug.
"""
import csv, difflib, os, re, sys, urllib.parse, urllib.request
from collections import Counter, defaultdict

DUMP = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\DELL\Downloads\sterlinglams-com-20260928-102353-wq92chimg1b5\database.sql"
EXPORT = sys.argv[2] if len(sys.argv) > 2 else r"C:\Users\DELL\Downloads\sterlinglams-com-20260928-102353-wq92chimg1b5\export"
OUT_CS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..",
                      "src", "SterlingLams.Web", "Infrastructure", "LegacyProductSlugMap.cs")
SITEMAP = "https://sterlinglams.com/sitemap.xml"
UA = "Mozilla/5.0 (compatible; SterlinGlams-SlugMap/1.0)"

# All-in-One WP Migration rewrites the real table prefix to this placeholder in its dump.
PFX = "SERVMASK_PREFIX_"
ESC = {'n': '\n', 'r': '\r', 't': '\t', '0': '\0', '\\': '\\', "'": "'", '"': '"', 'Z': '\x1a', 'b': '\b'}


def parse_tuple(s):
    """Values of a single `INSERT ... VALUES (...)` row, honouring MySQL string escaping."""
    vals, i, n = [], 0, len(s)
    while i < n:
        while i < n and s[i] in ' ,':
            i += 1
        if i >= n:
            break
        if s[i] == "'":
            i += 1
            buf = []
            while i < n:
                ch = s[i]
                if ch == '\\':
                    nx = s[i + 1] if i + 1 < n else ''
                    buf.append(ESC.get(nx, nx)); i += 2; continue
                if ch == "'":
                    if i + 1 < n and s[i + 1] == "'":
                        buf.append("'"); i += 2; continue
                    i += 1; break
                buf.append(ch); i += 1
            vals.append(''.join(buf))
        else:
            j = i
            while j < n and s[j] != ',':
                j += 1
            tok = s[i:j].strip()
            vals.append(None if tok == 'NULL' else tok)
            i = j
    return vals


def inner(line):
    k = line.find("VALUES (")
    s = line[k + 8:].rstrip()
    if s.endswith(';'):
        s = s[:-1]
    if s.endswith(')'):
        s = s[:-1]
    return s


def old_product_categories(dump):
    """old product slug -> its WooCommerce category slug (used as the fallback redirect target)."""
    p_terms = "INSERT INTO `%sterms` VALUES (" % PFX
    p_tt = "INSERT INTO `%sterm_taxonomy` VALUES (" % PFX
    p_tr = "INSERT INTO `%sterm_relationships` VALUES (" % PFX
    terms, tt, rel = {}, {}, defaultdict(list)
    with open(dump, encoding='utf-8', errors='replace', newline='') as f:
        for line in f:
            if line.startswith(p_terms):
                v = parse_tuple(inner(line))
                if len(v) >= 3:
                    terms[v[0]] = v[2]
            elif line.startswith(p_tt):
                v = parse_tuple(inner(line))
                if len(v) >= 3:
                    tt[v[0]] = (v[1], v[2])
            elif line.startswith(p_tr):
                v = parse_tuple(inner(line))
                if len(v) >= 2:
                    rel[v[0]].append(v[1])
    out = {}
    for r in csv.DictReader(open(os.path.join(EXPORT, 'old_product_urls.csv'), encoding='utf-8-sig')):
        for ttid in rel.get(r['product_id'], []):
            t = tt.get(ttid)
            if t and t[1] == 'product_cat' and t[0] in terms:
                out[r['slug'].lower()] = terms[t[0]]
                break
    return out


def live_catalogue():
    xml = urllib.request.urlopen(urllib.request.Request(SITEMAP, headers={'User-Agent': UA}),
                                 timeout=60).read().decode('utf-8', 'replace')
    products = sorted({urllib.parse.unquote(m.group(1)).rstrip('/').lower()
                       for m in re.finditer(r'<loc>https://sterlinglams\.com/products/([^<?]+)</loc>', xml)})
    categories = {urllib.parse.unquote(m.group(1)).lower()
                  for m in re.finditer(r'<loc>https://sterlinglams\.com/products\?category=([^<&]+)</loc>', xml)}
    return products, categories


def words(s):
    return set(t for t in re.split(r'[-_]+', s.lower()) if t)


def best_match(old, live_products):
    """Strictest-first match, or (None, 0) when nothing is close enough to be safe."""
    ow = words(old)
    best = []
    for new in live_products:
        nw = words(new)
        shared = ow & nw
        if not shared:
            continue
        subset = ow <= nw or nw <= ow
        ratio = difflib.SequenceMatcher(None, old, new).ratio()
        if (subset and len(shared) >= 2) or ratio >= 0.88:
            best.append((1.0 if subset else ratio, len(shared), new))
    if not best:
        return None, 0.0
    best.sort(reverse=True)
    return best[0][2], best[0][0]


def main():
    prod_cat = old_product_categories(DUMP)
    live_products, live_categories = live_catalogue()
    live_set = set(live_products)
    print("old products with a category: %d | live products: %d | live categories: %d"
          % (len(prod_cat), len(live_products), len(live_categories)))

    audit = list(csv.DictReader(open(os.path.join(EXPORT, 'url_audit_with_revenue.csv'), encoding='utf-8-sig')))
    broken = [r for r in audit if r['verdict'] != 'OK']
    rows = []
    for r in sorted(broken, key=lambda x: -float(x['revenue_last_24mo'] or 0)):
        slug = r['slug'].lower()
        if slug in live_set:
            rows.append({**r, 'target': '', 'confidence': '',
                         'reason': 'still live under this slug (was only 404ing via hide-out-of-stock)'})
            continue
        match, conf = best_match(slug, live_products)
        if match:
            rows.append({**r, 'target': '/products/' + match, 'confidence': '%.2f' % conf,
                         'reason': 'renamed slug (strict match)'})
            continue
        cat = prod_cat.get(slug)
        if cat and cat in live_categories:
            rows.append({**r, 'target': '/products?category=' + cat, 'confidence': '',
                         'reason': 'gone -> its old category'})
        else:
            rows.append({**r, 'target': '', 'confidence': '',
                         'reason': 'gone, no safe target -> 404'})

    print("\n=== map composition ===")
    for reason, n in Counter(r['reason'] for r in rows).most_common():
        rev = sum(float(x['revenue_last_24mo'] or 0) for x in rows if x['reason'] == reason)
        print("  {:<58} {:>4}  NGN {:>12,.0f}".format(reason[:58], n, rev))

    review = os.path.join(EXPORT, 'redirect_map_final.csv')
    with open(review, 'w', newline='', encoding='utf-8-sig') as fo:
        w = csv.DictWriter(fo, fieldnames=list(rows[0].keys()))
        w.writeheader(); w.writerows(rows)
    print("\nreviewable map -> %s" % review)

    entries = sorted((r['slug'].lower(), r['target']) for r in rows if r['target'])
    # Never emit a self-redirect: ProductsController consults this map when a slug has no product, so
    # /products/{same-slug} would 301 to itself forever.
    entries = [(s, t) for s, t in entries if t != '/products/' + s]

    body = "\n".join('        ["%s"] = "%s",' % (s, t) for s, t in entries)
    cs = '''namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Old WooCommerce product slugs whose page no longer exists under that slug, mapped to the page that
/// replaces it. These URLs (/shop/{slug}/ on the old site) are still in Google's index and still carry the
/// ranking this catalogue earns from, so each must resolve with a single 301 rather than a soft-404 bounce
/// to /products?search=, which Google drops from the index.
///
/// Targets are either the renamed product - matched strictly, so a shopper is never dropped on an
/// unrelated item - or, where the product is genuinely gone, its old category listing. Slugs with no safe
/// target are deliberately absent and 404 instead.
///
/// Generated by tools/legacy-url-recovery/generate_slug_map.py; re-run it after a catalogue re-slug
/// rather than editing this file by hand.
/// </summary>
public static class LegacyProductSlugMap
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
%s
    };

    /// <summary>True (with the new path) when <paramref name="slug"/> is a retired product slug.</summary>
    public static bool TryResolve(string? slug, out string target)
    {
        target = "";
        if (string.IsNullOrWhiteSpace(slug)) return false;
        // Assign only on a hit: passing `out target` straight to TryGetValue would null it on a miss,
        // so callers that ignore the bool would see null instead of "".
        if (!Map.TryGetValue(slug.Trim().Trim('/'), out var found)) return false;
        target = found;
        return true;
    }

    public static int Count => Map.Count;
}
''' % body
    dest = os.path.normpath(OUT_CS)
    open(dest, 'w', encoding='utf-8').write(cs)
    print("wrote %s (%d entries)" % (dest, len(entries)))


if __name__ == '__main__':
    main()
