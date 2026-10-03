/*
 * SLCharts v3 — a tiny, dependency-free SVG chart renderer for the admin/inventory backends.
 * Static render (no entrance animation, no Chart.js) but INTERACTIVE:
 *   • hover any point / bar / slice for a tooltip
 *   • ZOOM on the time-series charts (line/area/bar): drag across the chart to zoom into a range,
 *     use the +/−/⟲ buttons (top-right), Ctrl/⌘ + mouse-wheel, or double-click to reset.
 * Same API as before so the views don't change:
 *
 *   SLCharts.line(id, labels, [{label, data, money}])
 *   SLCharts.area(id, labels, [{label, data, money}])
 *   SLCharts.bar(id, labels, data, { money, label, hrefs })
 *   SLCharts.hbar(id, labels, data, { money })          // not zoomable (ranking chart)
 *   SLCharts.doughnut(id, labels, data, { money, colors })
 */
(function () {
    var palette = ['#10b981', '#0ea5e9', '#f59e0b', '#ef4444', '#8b5cf6', '#ec4899', '#14b8a6', '#64748b'];
    var GRID = '#f0f0f0', TXT = '#6b7280';
    var NAIRA = String.fromCharCode(0x20A6); // ₦ via code point — never garbles regardless of charset
    var W = 640, PADL = 64, PADR = 14;       // viewBox width + horizontal plot padding (shared geometry)

    function esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]; }); }
    function money(v) { return NAIRA + Math.round(v).toLocaleString('en-US'); }
    function num(v) { return Math.round(v).toLocaleString('en-US'); }
    function fmt(v, isMoney) { return isMoney ? money(v) : num(v); }
    function niceMax(max) {
        if (max <= 0) return 1;
        var pow = Math.pow(10, Math.floor(Math.log10(max)));
        var n = max / pow, step = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
        return step * pow;
    }

    // ── Tooltip (shared, follows the cursor) ───────────────────────────────────
    var tip;
    function ensureTip() {
        if (!tip) {
            tip = document.createElement('div');
            tip.style.cssText = 'position:fixed;pointer-events:none;z-index:9999;background:#111827;color:#fff;' +
                'font:12px ui-sans-serif,system-ui,-apple-system,Segoe UI,sans-serif;padding:5px 9px;border-radius:6px;' +
                'box-shadow:0 2px 10px rgba(0,0,0,.25);opacity:0;transition:opacity .08s;white-space:nowrap;max-width:260px';
            document.body.appendChild(tip);
        }
        return tip;
    }
    function moveTip(x, y) {
        var t = ensureTip(), pad = 14;
        var w = t.offsetWidth, vw = window.innerWidth;
        var left = x + pad; if (left + w + 4 > vw) left = x - w - pad;
        t.style.left = Math.max(4, left) + 'px';
        t.style.top = (y + pad) + 'px';
    }
    function showTip(text, x, y) { var t = ensureTip(); t.textContent = text; t.style.opacity = '1'; moveTip(x, y); }
    function hideTip() { if (tip) tip.style.opacity = '0'; }

    // One-time CSS for hover affordances + zoom chrome.
    (function injectCss() {
        var s = document.createElement('style');
        s.textContent = '.slc-chart{position:relative}'
            + '.slc-chart [data-tip]{cursor:pointer;transition:opacity .1s}'
            + '.slc-chart rect[data-tip]:hover{fill-opacity:1}'
            + '.slc-chart path[data-tip]:hover{opacity:.85}'
            + '.slc-chart .slc-hit{fill:transparent}'
            + '.slc-chart .slc-pt{transition:r .1s}'
            + '.slc-chart .slc-hit:hover + .slc-pt{r:4}'
            + '.slc-zoomable{cursor:crosshair;user-select:none;-webkit-user-select:none;touch-action:pan-y}'
            + '.slc-sel{position:absolute;top:0;bottom:0;background:rgba(14,165,233,.14);border-left:1px solid rgba(14,165,233,.7);border-right:1px solid rgba(14,165,233,.7);pointer-events:none;display:none}'
            + '.slc-zoom{position:absolute;top:6px;right:6px;display:flex;gap:3px;opacity:0;transition:opacity .12s;z-index:2}'
            + '.slc-chart:hover .slc-zoom{opacity:1}'
            + '.slc-zoom button{width:24px;height:24px;border:1px solid #e5e7eb;background:#fff;color:#374151;border-radius:6px;'
            + 'font:600 13px ui-sans-serif,system-ui,sans-serif;line-height:1;cursor:pointer;box-shadow:0 1px 2px rgba(0,0,0,.06);padding:0}'
            + '.slc-zoom button:hover{background:#f9fafb;border-color:#d1d5db}'
            + '.slc-zoom button:disabled{opacity:.4;cursor:default}'
            + '.slc-range{position:absolute;bottom:4px;left:50%;transform:translateX(-50%);font:11px ui-sans-serif,system-ui,sans-serif;'
            + 'color:#6b7280;background:rgba(255,255,255,.85);padding:1px 8px;border-radius:999px;pointer-events:none;opacity:0;transition:opacity .12s}'
            + '.slc-chart:hover .slc-range{opacity:1}';
        document.head.appendChild(s);
    })();

    // Replace the placeholder <canvas id> with a div holding the SVG, and wire tooltip delegation.
    // Returns the mounted box so callers can layer zoom controls on top.
    function mount(id, svg) {
        var host = document.getElementById(id);
        if (!host) return null;
        var box = document.createElement('div');
        box.className = 'slc-chart';
        box.innerHTML = svg;
        host.parentNode.replaceChild(box, host);
        box.addEventListener('mouseover', function (e) {
            var el = e.target.closest && e.target.closest('[data-tip]');
            if (el) showTip(el.getAttribute('data-tip'), e.clientX, e.clientY);
        });
        box.addEventListener('mousemove', function (e) {
            if (tip && tip.style.opacity === '1') moveTip(e.clientX, e.clientY);
        });
        box.addEventListener('mouseout', function (e) {
            if (e.target.closest && e.target.closest('[data-tip]')) hideTip();
        });
        // Drill-through: a bar/segment/label carrying data-href navigates on click.
        box.addEventListener('click', function (e) {
            var el = e.target.closest && e.target.closest('[data-href]');
            if (el) { var h = el.getAttribute('data-href'); if (h) window.location.href = h; }
        });
        return box;
    }
    // data-href (+ pointer cursor) for a clickable mark, or '' when no link for this index.
    function hrefAttr(hrefs, i) {
        return (hrefs && hrefs[i]) ? (' data-href="' + esc(hrefs[i]) + '" style="cursor:pointer"') : '';
    }
    // Plain-text tooltip carried on a data attribute. SVG attrs set via innerHTML aren't entity-
    // decoded on read here, so we keep it literal (just neutralise the quote that delimits the attr).
    function tipText(label, v, isMoney) { return String(label) + ': ' + fmt(v, isMoney); }
    function tipAttr(label, v, isMoney) { return 'data-tip="' + tipText(label, v, isMoney).replace(/"/g, '”') + '"'; }
    function svgOpen(h) { return '<svg viewBox="0 0 640 ' + h + '" width="100%" preserveAspectRatio="xMidYMid meet" font-family="ui-sans-serif,system-ui,-apple-system,Segoe UI,sans-serif">'; }

    function cartesian(labels, maxVal, isMoney, draw) {
        var H = 230, padL = PADL, padR = PADR, padT = 12, padB = 28;
        var x0 = padL, x1 = W - padR, y0 = padT, y1 = H - padB;
        var top = niceMax(maxVal), ticks = 4;
        var s = svgOpen(H);
        for (var i = 0; i <= ticks; i++) {
            var gy = y1 - (y1 - y0) * (i / ticks), val = top * (i / ticks);
            s += '<line x1="' + x0 + '" y1="' + gy.toFixed(1) + '" x2="' + x1 + '" y2="' + gy.toFixed(1) + '" stroke="' + GRID + '" stroke-width="1"/>';
            s += '<text x="' + (x0 - 6) + '" y="' + (gy + 3).toFixed(1) + '" text-anchor="end" font-size="10" fill="' + TXT + '">' + fmt(val, isMoney) + '</text>';
        }
        // Evenly spaced x-axis labels that always include both endpoints (no doubled-up last label
        // colliding with the previous tick). Cap the number of labels to what fits the longest one.
        var n = labels.length, maxLen = 0;
        for (var k = 0; k < n; k++) { var L = String(labels[k]).length; if (L > maxLen) maxLen = L; }
        var approxPx = maxLen * 6 + 16;
        var fit = Math.max(2, Math.floor((x1 - x0) / approxPx));
        var count = Math.min(n, 8, fit), seen = {};
        for (var t = 0; t < count; t++) {
            var j = count <= 1 ? 0 : Math.round(t * (n - 1) / (count - 1));
            if (seen[j]) continue;
            seen[j] = 1;
            var lx = n === 1 ? (x0 + x1) / 2 : x0 + (x1 - x0) * (j / (n - 1));
            var anchor = n === 1 ? 'middle' : (j === 0 ? 'start' : (j === n - 1 ? 'end' : 'middle'));
            s += '<text x="' + lx.toFixed(1) + '" y="' + (y1 + 16) + '" text-anchor="' + anchor + '" font-size="10" fill="' + TXT + '">' + esc(labels[j]) + '</text>';
        }
        s += draw({ x0: x0, x1: x1, y0: y0, y1: y1, top: top, n: n });
        return s + '</svg>';
    }

    // ── Interactive zoom for the time-series charts ─────────────────────────────
    // A zoomable chart keeps its full data and renders a visible [s..e] index window. Dragging across
    // the plot, the zoom buttons, Ctrl+wheel and double-click all adjust that window and re-render.
    var MINSPAN = 1;  // keep at least 2 points (span = e - s) visible
    function plotFrac(box, clientX) {
        var r = box.getBoundingClientRect();
        if (r.width <= 0) return 0;
        var vx = (clientX - r.left) / r.width * W;            // px → viewBox x
        var f = (vx - PADL) / (W - PADR - PADL);              // viewBox x → plot fraction
        return Math.max(0, Math.min(1, f));
    }
    function makeZoomable(box, total, renderWindow) {
        try { makeZoomableImpl(box, total, renderWindow); }
        catch (err) { if (window.console) console.warn('SLCharts zoom disabled:', err); }
    }
    function makeZoomableImpl(box, total, renderWindow) {
        if (!box || total < 3) return;                        // nothing to zoom on 1–2 points
        var st = { s: 0, e: total - 1 };
        var svgWrap = box.firstChild;
        if (svgWrap) svgWrap.classList.add('slc-zoomable');

        var sel = document.createElement('div'); sel.className = 'slc-sel'; box.appendChild(sel);
        var range = document.createElement('div'); range.className = 'slc-range'; box.appendChild(range);
        var ctrls = document.createElement('div'); ctrls.className = 'slc-zoom';
        ctrls.innerHTML = '<button type="button" data-z="in" title="Zoom in">+</button>'
            + '<button type="button" data-z="out" title="Zoom out">−</button>'
            + '<button type="button" data-z="reset" title="Reset zoom">⟲</button>';
        box.appendChild(ctrls);

        function idxAt(f) { return Math.round(st.s + f * (st.e - st.s)); }
        function clampWindow(s, e) {
            s = Math.max(0, Math.round(s)); e = Math.min(total - 1, Math.round(e));
            if (e - s < MINSPAN) { // keep a minimum span, nudged inside bounds
                var mid = (s + e) / 2;
                s = Math.max(0, Math.round(mid - MINSPAN / 2));
                e = Math.min(total - 1, s + MINSPAN);
                s = Math.max(0, e - MINSPAN);
            }
            return { s: s, e: e };
        }
        function redraw() {
            var svg = renderWindow(st.s, st.e);
            // Re-render in place: keep the box (delegated listeners live there), swap the SVG wrapper.
            box.innerHTML = svg;
            box.appendChild(sel); box.appendChild(range); box.appendChild(ctrls);
            svgWrap = box.firstChild; if (svgWrap) svgWrap.classList.add('slc-zoomable');
            var zoomed = st.s > 0 || st.e < total - 1;
            range.textContent = (st.e - st.s + 1) + ' of ' + total + (zoomed ? ' · double-click to reset' : '');
            ctrls.querySelector('[data-z=reset]').disabled = !zoomed;
            ctrls.querySelector('[data-z=out]').disabled = !zoomed;
            ctrls.querySelector('[data-z=in]').disabled = (st.e - st.s) <= MINSPAN;
        }
        function zoomBy(factor, centerFrac) {
            var span = st.e - st.s;
            var center = st.s + (centerFrac == null ? 0.5 : centerFrac) * span;
            var half = span * factor / 2;
            st = clampWindow(center - half, center + half); redraw();
        }

        // Zoom buttons.
        ctrls.addEventListener('click', function (e) {
            var b = e.target.closest('button'); if (!b) return;
            e.stopPropagation();
            var z = b.getAttribute('data-z');
            if (z === 'in') zoomBy(0.6);
            else if (z === 'out') zoomBy(1.8);
            else { st = { s: 0, e: total - 1 }; redraw(); }
        });

        // Ctrl / ⌘ + wheel to zoom (plain wheel still scrolls the page).
        box.addEventListener('wheel', function (e) {
            if (!(e.ctrlKey || e.metaKey)) return;
            e.preventDefault();
            zoomBy(e.deltaY < 0 ? 0.8 : 1.25, plotFrac(box, e.clientX));
        }, { passive: false });

        // Drag across the plot to select a range → zoom into it.
        var dragging = false, downF = 0, downX = 0, moved = false;
        function down(clientX) { dragging = true; moved = false; downX = clientX; downF = plotFrac(box, clientX); hideTip(); }
        function move(clientX) {
            if (!dragging) return;
            if (Math.abs(clientX - downX) > 3) moved = true;
            var r = box.getBoundingClientRect();
            var a = Math.min(downX, clientX), b = Math.max(downX, clientX);
            sel.style.display = 'block';
            sel.style.left = (Math.max(r.left, a) - r.left) + 'px';
            sel.style.width = (Math.min(r.right, b) - Math.max(r.left, a)) + 'px';
        }
        function up(clientX) {
            if (!dragging) return;
            dragging = false; sel.style.display = 'none'; sel.style.width = '0';
            if (!moved) return;                                // a click, not a drag
            var f2 = plotFrac(box, clientX);
            var i1 = idxAt(Math.min(downF, f2)), i2 = idxAt(Math.max(downF, f2));
            if (i2 - i1 >= 1) { st = clampWindow(i1, i2); redraw(); }
        }
        box.addEventListener('mousedown', function (e) { if (e.button === 0 && !e.target.closest('.slc-zoom')) down(e.clientX); });
        window.addEventListener('mousemove', function (e) { move(e.clientX); });
        window.addEventListener('mouseup', function (e) { up(e.clientX); });
        box.addEventListener('dblclick', function () { st = { s: 0, e: total - 1 }; redraw(); });
        // Touch: drag to select a range.
        box.addEventListener('touchstart', function (e) { if (e.touches.length === 1) down(e.touches[0].clientX); }, { passive: true });
        box.addEventListener('touchmove', function (e) { if (dragging && e.touches.length === 1) { move(e.touches[0].clientX); } }, { passive: true });
        box.addEventListener('touchend', function (e) { up((e.changedTouches[0] || {}).clientX || downX); });

        redraw();
    }

    // Build the <svg> string for a line/area chart over a given labels+series slice.
    function seriesSvg(kind, labels, series) {
        var isMoney = series.some(function (s) { return s.money; });
        var max = 0;
        series.forEach(function (se) { (se.data || []).forEach(function (v) { if (v > max) max = v; }); });

        function spline(P) {
            if (!P.length) return '';
            if (P.length < 3) return 'M' + P.map(function (p) { return p.x + ',' + p.y; }).join(' L');
            var d = 'M' + P[0].x.toFixed(1) + ',' + P[0].y.toFixed(1);
            for (var i = 0; i < P.length - 1; i++) {
                var p0 = P[i - 1] || P[i], p1 = P[i], p2 = P[i + 1], p3 = P[i + 2] || p2;
                var c1x = p1.x + (p2.x - p0.x) / 6, c1y = p1.y + (p2.y - p0.y) / 6;
                var c2x = p2.x - (p3.x - p1.x) / 6, c2y = p2.y - (p3.y - p1.y) / 6;
                d += ' C' + c1x.toFixed(1) + ',' + c1y.toFixed(1) + ' ' + c2x.toFixed(1) + ',' + c2y.toFixed(1)
                   + ' ' + p2.x.toFixed(1) + ',' + p2.y.toFixed(1);
            }
            return d;
        }

        return cartesian(labels, max, isMoney, function (p) {
            var out = '';
            series.forEach(function (se, si) {
                var col = se.color || palette[si % palette.length], data = se.data || [], n = data.length;
                var X = function (i) { return n === 1 ? (p.x0 + p.x1) / 2 : p.x0 + (p.x1 - p.x0) * (i / (n - 1)); };
                var Y = function (v) { return p.y1 - (p.y1 - p.y0) * (v / p.top); };
                if (kind === 'area') {
                    var P = data.map(function (v, i) { return { x: X(i), y: Y(v) }; });
                    var path = spline(P);
                    if (n > 1 && si === 0) {
                        var gid = 'slc-ag-' + si + '-' + Math.random().toString(36).slice(2, 7);
                        out += '<defs><linearGradient id="' + gid + '" gradientUnits="userSpaceOnUse" x1="0" y1="'
                             + p.y0 + '" x2="0" y2="' + p.y1 + '">'
                             + '<stop offset="0" stop-color="' + col + '" stop-opacity="0.32"/>'
                             + '<stop offset="1" stop-color="' + col + '" stop-opacity="0"/></linearGradient></defs>';
                        out += '<path d="' + path + ' L' + P[n - 1].x.toFixed(1) + ',' + p.y1
                             + ' L' + P[0].x.toFixed(1) + ',' + p.y1 + ' Z" fill="url(#' + gid + ')"/>';
                    }
                    out += '<path d="' + path + '" fill="none" stroke="' + col + '" stroke-width="2.5" stroke-linejoin="round" stroke-linecap="round"/>';
                } else {
                    var pts = data.map(function (v, i) { return X(i).toFixed(1) + ',' + Y(v).toFixed(1); });
                    if (n > 1 && si === 0)
                        out += '<polygon points="' + p.x0 + ',' + p.y1 + ' ' + pts.join(' ') + ' ' + p.x1 + ',' + p.y1 + '" fill="' + col + '" fill-opacity="0.08"/>';
                    out += '<polyline points="' + pts.join(' ') + '" fill="none" stroke="' + col + '" stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/>';
                }
                data.forEach(function (v, i) {
                    var cx = X(i).toFixed(1), cy = Y(v).toFixed(1);
                    out += '<circle class="slc-hit" cx="' + cx + '" cy="' + cy + '" r="14" ' + tipAttr(labels[i], v, isMoney) + '/>';
                    out += '<circle class="slc-pt" cx="' + cx + '" cy="' + cy + '" r="2.5" fill="' + col + '" pointer-events="none"/>';
                });
            });
            return out;
        });
    }

    function sliceSeries(series, s, e) {
        return series.map(function (se) {
            return { label: se.label, color: se.color, money: se.money, data: (se.data || []).slice(s, e + 1) };
        });
    }

    var SLCharts = {
        palette: palette,

        line: function (id, labels, series) {
            series = series || []; labels = labels || [];
            var box = mount(id, seriesSvg('line', labels, series));
            makeZoomable(box, labels.length, function (s, e) {
                return seriesSvg('line', labels.slice(s, e + 1), sliceSeries(series, s, e));
            });
            return {};
        },

        area: function (id, labels, series) {
            series = series || []; labels = labels || [];
            var box = mount(id, seriesSvg('area', labels, series));
            makeZoomable(box, labels.length, function (s, e) {
                return seriesSvg('area', labels.slice(s, e + 1), sliceSeries(series, s, e));
            });
            return {};
        },

        bar: function (id, labels, data, opts) {
            opts = opts || {}; data = data || []; labels = labels || [];
            function build(labs, dat, hrefs) {
                var isMoney = !!opts.money, max = Math.max.apply(null, dat.concat([0]));
                return cartesian(labs, max, isMoney, function (p) {
                    var out = '', n = dat.length, slot = (p.x1 - p.x0) / Math.max(1, n), bw = Math.min(48, slot * 0.6);
                    dat.forEach(function (v, i) {
                        var cx = p.x0 + slot * (i + 0.5), h = (p.y1 - p.y0) * (v / p.top);
                        out += '<rect x="' + (cx - bw / 2).toFixed(1) + '" y="' + (p.y1 - h).toFixed(1) + '" width="' + bw.toFixed(1) + '" height="' + h.toFixed(1) + '" rx="3" fill="' + palette[0] + '" fill-opacity="0.85" ' + tipAttr(labs[i], v, isMoney) + hrefAttr(hrefs, i) + '/>';
                    });
                    return out;
                });
            }
            var box = mount(id, build(labels, data, opts.hrefs || []));
            makeZoomable(box, labels.length, function (s, e) {
                return build(labels.slice(s, e + 1), data.slice(s, e + 1), (opts.hrefs || []).slice(s, e + 1));
            });
            return {};
        },

        hbar: function (id, labels, data, opts) {
            opts = opts || {}; data = data || [];
            var isMoney = !!opts.money, n = data.length, hrefs = opts.hrefs || [];
            var max = niceMax(Math.max.apply(null, data.concat([0])));
            var rowH = 30, padT = 8, padB = 8, labelW = 150, valW = 92;
            var H = padT + padB + n * rowH, x0 = labelW, x1 = W - valW;
            var s = svgOpen(H);
            data.forEach(function (v, i) {
                var cy = padT + i * rowH, barY = cy + 5, bh = rowH - 12, w = (x1 - x0) * (v / max), col = palette[i % palette.length];
                var full = String(labels[i]), ha = hrefAttr(hrefs, i);
                s += '<text x="' + (labelW - 8) + '" y="' + (cy + rowH / 2 + 3).toFixed(1) + '" text-anchor="end" font-size="11" fill="#374151"' + ha + '>' + esc(full.length > 22 ? full.slice(0, 21) + '…' : full) + '</text>';
                s += '<rect x="' + x0 + '" y="' + barY.toFixed(1) + '" width="' + Math.max(0, w).toFixed(1) + '" height="' + bh + '" rx="3" fill="' + col + '" fill-opacity="0.85" ' + tipAttr(full, v, isMoney) + ha + '/>';
                s += '<text x="' + (x1 + 6) + '" y="' + (cy + rowH / 2 + 3).toFixed(1) + '" font-size="10" fill="' + TXT + '">' + fmt(v, isMoney) + '</text>';
            });
            mount(id, s + '</svg>');
            return {};
        },

        doughnut: function (id, labels, data, opts) {
            opts = opts || {}; data = data || []; labels = labels || [];
            var isMoney = !!opts.money, hrefs = opts.hrefs || [];
            var colors = opts.colors || labels.map(function (_, i) { return palette[i % palette.length]; });
            var total = data.reduce(function (a, b) { return a + (b || 0); }, 0) || 1;
            var H = 200, cx = 110, cy = 100, rO = 80, rI = 50;
            var s = svgOpen(H), ang = -Math.PI / 2;
            if (data.every(function (v) { return !v; })) {
                s += '<circle cx="' + cx + '" cy="' + cy + '" r="' + ((rO + rI) / 2) + '" fill="none" stroke="' + GRID + '" stroke-width="' + (rO - rI) + '"/>';
            } else {
                data.forEach(function (v, i) {
                    if (!v) return;
                    var frac = v / total, a2 = ang + frac * 2 * Math.PI, large = frac > 0.5 ? 1 : 0;
                    var pt = function (r, a) { return (cx + r * Math.cos(a)).toFixed(2) + ' ' + (cy + r * Math.sin(a)).toFixed(2); };
                    var tipv = 'data-tip="' + (labels[i] + ': ' + fmt(v, isMoney) + ' (' + Math.round(frac * 100) + '%)').replace(/"/g, '”') + '"';
                    s += '<path d="M ' + pt(rO, ang) + ' A ' + rO + ' ' + rO + ' 0 ' + large + ' 1 ' + pt(rO, a2) +
                         ' L ' + pt(rI, a2) + ' A ' + rI + ' ' + rI + ' 0 ' + large + ' 0 ' + pt(rI, ang) + ' Z" fill="' + colors[i % colors.length] + '" ' + tipv + hrefAttr(hrefs, i) + '/>';
                    ang = a2;
                });
            }
            var ly = 24, lx = 230;
            labels.forEach(function (lab, i) {
                var ha = hrefAttr(hrefs, i);
                s += '<rect x="' + lx + '" y="' + (ly - 9) + '" width="10" height="10" rx="2" fill="' + colors[i % colors.length] + '"' + ha + '/>';
                s += '<text x="' + (lx + 16) + '" y="' + ly + '" font-size="11" fill="#374151"' + ha + '>' + esc(lab) + '</text>';
                s += '<text x="620" y="' + ly + '" text-anchor="end" font-size="11" fill="' + TXT + '">' + fmt(data[i] || 0, isMoney) + '</text>';
                ly += 22;
            });
            mount(id, s + '</svg>');
            return {};
        }
    };

    window.SLCharts = SLCharts;
})();
