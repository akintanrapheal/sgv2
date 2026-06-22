/*
 * Sterlin Glams POS — offline data layer (Phase 1).
 *
 * Makes the till search / scan / build a cart with no network by:
 *  1. pulling a full snapshot (catalog + store stock + categories + discount reasons + customers)
 *     from /Pos/Snapshot into IndexedDB whenever online, and
 *  2. installing a window.fetch shim so the read endpoints the page already calls
 *     (/Pos/Search, /Pos/Categories, /Pos/DiscountReasons, /Pos/CustomerSearch) transparently fall
 *     back to that snapshot when the network is unreachable — returning the SAME JSON shapes, so the
 *     existing page code is untouched.
 *
 * Loaded BEFORE the page's inline script so the shim is in place before any fetch runs.
 * Phase 2 will add the offline sale queue + sync UX on top of window.SGPOS exposed here.
 */
(function () {
  'use strict';
  if (!location.pathname.toLowerCase().startsWith('/pos')) return;

  var DB_NAME = 'sgpos', STORE = 'kv', KEY = 'snapshot';
  var mem = null;            // in-memory snapshot
  var realFetch = window.fetch.bind(window);

  // ── IndexedDB (best-effort; degrades to in-memory if unavailable) ──────────
  function openDb() {
    return new Promise(function (resolve, reject) {
      try {
        var req = indexedDB.open(DB_NAME, 1);
        req.onupgradeneeded = function () { req.result.createObjectStore(STORE); };
        req.onsuccess = function () { resolve(req.result); };
        req.onerror = function () { reject(req.error); };
      } catch (e) { reject(e); }
    });
  }
  function idbGet(key) {
    return openDb().then(function (db) {
      return new Promise(function (resolve) {
        var r = db.transaction(STORE, 'readonly').objectStore(STORE).get(key);
        r.onsuccess = function () { resolve(r.result || null); };
        r.onerror = function () { resolve(null); };
      });
    }).catch(function () { return null; });
  }
  function idbPut(key, val) {
    return openDb().then(function (db) {
      return new Promise(function (resolve) {
        var tx = db.transaction(STORE, 'readwrite');
        tx.objectStore(STORE).put(val, key);
        tx.oncomplete = function () { resolve(true); };
        tx.onerror = function () { resolve(false); };
      });
    }).catch(function () { return false; });
  }

  // ── Local responders (mirror the server endpoints) ─────────────────────────
  function jsonResponse(data) {
    return new Response(JSON.stringify(data), {
      status: 200, headers: { 'Content-Type': 'application/json; charset=utf-8' }
    });
  }
  function ci(s) { return (s || '').toString().toLowerCase(); }

  function localSearch(params) {
    if (!mem) return [];
    var q = ci(params.get('q')).trim();
    var cat = params.get('categoryId');
    var out = mem.products.filter(function (p) {
      if (cat && String(p.categoryId) !== String(cat)) return false;
      if (!q) return true;
      return ci(p.name).indexOf(q) >= 0 || ci(p.sku).indexOf(q) >= 0 || ci(p.barcode).indexOf(q) >= 0;
    });
    return out.slice(0, 40);
  }
  function localCustomers(params) {
    if (!mem) return [];
    var q = ci(params.get('q')).trim();
    var list = mem.customers;
    if (!q) list = list.slice(0, 20);
    else list = list.filter(function (c) {
      return ci(c.name).indexOf(q) >= 0 || ci(c.phone).indexOf(q) >= 0 || ci(c.email).indexOf(q) >= 0;
    }).slice(0, 15);
    return list.map(function (c) { return { id: c.id, name: c.name, phone: c.phone }; });
  }

  // Map an intercepted GET path to a local-data builder (null = don't intercept).
  function localFor(path, params) {
    var p = path.toLowerCase();
    if (p === '/pos/search') return localSearch(params);
    if (p === '/pos/categories') return mem ? mem.categories : [];
    if (p === '/pos/discountreasons') return mem ? mem.discountReasons : [];
    if (p === '/pos/customersearch') return localCustomers(params);
    return undefined;
  }

  // ── fetch shim: network-first, fall back to the local snapshot when offline ──
  window.fetch = function (input, init) {
    try {
      var method = ((init && init.method) || (input && input.method) || 'GET').toUpperCase();
      var urlStr = typeof input === 'string' ? input : (input && input.url) || '';
      var url = new URL(urlStr, location.origin);
      var intercept = method === 'GET' && url.origin === location.origin &&
        localFor(url.pathname, url.searchParams) !== undefined;

      if (!intercept) return realFetch(input, init);

      return realFetch(input, init).catch(function () {
        var data = localFor(url.pathname, url.searchParams);
        return jsonResponse(data || []);
      });
    } catch (e) {
      return realFetch(input, init);
    }
  };

  // ── Snapshot refresh ────────────────────────────────────────────────────────
  function refreshSnapshot() {
    return realFetch('/Pos/Snapshot', { headers: { 'X-Requested-With': 'fetch' } })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (data) {
        if (!data || data.ok === false) return false;
        mem = data;
        idbPut(KEY, data);
        updateBanner();
        return true;
      })
      .catch(function () { return false; });
  }

  // ── Offline banner + "last synced" ───────────────────────────────────────────
  var bar;
  function syncedText() {
    if (!mem || !mem.syncedAt) return 'not yet synced';
    var d = new Date(mem.syncedAt);
    return 'synced ' + d.toLocaleString([], { hour: '2-digit', minute: '2-digit', day: '2-digit', month: 'short' });
  }
  function ensureBar() {
    if (bar) return bar;
    bar = document.createElement('div');
    bar.id = 'sgpos-offline-bar';
    bar.style.cssText =
      'position:fixed;top:0;left:0;right:0;z-index:9999;display:none;' +
      'background:#92400e;color:#fff;font:500 13px/1.4 ui-sans-serif,system-ui,sans-serif;' +
      'padding:7px 14px;text-align:center;box-shadow:0 1px 4px rgba(0,0,0,.2)';
    document.body.appendChild(bar);
    return bar;
  }
  function updateBanner() {
    var b = ensureBar();
    if (navigator.onLine) {
      b.style.display = 'none';
      document.body.style.paddingTop = '';
    } else {
      b.textContent = '⚠ Offline — selling from saved catalogue (' + syncedText() + '). Sales will sync when you reconnect.';
      b.style.display = 'block';
      document.body.style.paddingTop = b.offsetHeight + 'px';
    }
  }

  // ── Init ──────────────────────────────────────────────────────────────────
  function init() {
    idbGet(KEY).then(function (saved) {
      if (saved && !mem) mem = saved;       // seed from cache (instant offline)
      updateBanner();
      if (navigator.onLine) refreshSnapshot();
    });
    window.addEventListener('online', function () { updateBanner(); refreshSnapshot(); });
    window.addEventListener('offline', updateBanner);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();

  // Exposed for Phase 2 (offline sales + sync UX).
  window.SGPOS = {
    refresh: refreshSnapshot,
    snapshot: function () { return mem; },
    lastSyncedText: syncedText,
    isOnline: function () { return navigator.onLine; }
  };
})();
