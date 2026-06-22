/*
 * Sterlin Glams POS — offline data layer + sale queue (Phase 1 + 2).
 *
 * Phase 1: caches a /Pos/Snapshot (catalogue + store stock + categories + discount reasons +
 *   customers) in IndexedDB and shims window.fetch so the read endpoints fall back to it offline.
 * Phase 2: when /Pos/Checkout can't reach the server, the sale is captured locally (idempotent
 *   client id), the local stock is decremented and a receipt-less "saved offline" success is
 *   returned so the till keeps selling. Queued sales auto-sync the moment the network returns and
 *   via a manual "Sync with cloud" menu button, with a "Successfully synced HH:MM" toast.
 *
 * Loaded before the page's inline script so the shim is active first. Exposes window.SGPOS.
 */
(function () {
  'use strict';
  if (!location.pathname.toLowerCase().startsWith('/pos')) return;

  var DB_NAME = 'sgpos', STORE = 'kv', SNAP_KEY = 'snapshot', QUEUE_KEY = 'queue';
  var mem = null;          // in-memory snapshot
  var queue = [];          // in-memory outbound sale queue
  var syncing = false;
  var realFetch = window.fetch.bind(window);

  // ── IndexedDB (best-effort) ────────────────────────────────────────────────
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
    return openDb().then(function (db) { return new Promise(function (res) {
      var r = db.transaction(STORE, 'readonly').objectStore(STORE).get(key);
      r.onsuccess = function () { res(r.result || null); }; r.onerror = function () { res(null); };
    }); }).catch(function () { return null; });
  }
  function idbPut(key, val) {
    return openDb().then(function (db) { return new Promise(function (res) {
      var tx = db.transaction(STORE, 'readwrite'); tx.objectStore(STORE).put(val, key);
      tx.oncomplete = function () { res(true); }; tx.onerror = function () { res(false); };
    }); }).catch(function () { return false; });
  }
  function saveQueue() { return idbPut(QUEUE_KEY, queue); }

  // ── Local responders (mirror the read endpoints) ───────────────────────────
  function jsonResponse(data) {
    return new Response(JSON.stringify(data), { status: 200, headers: { 'Content-Type': 'application/json; charset=utf-8' } });
  }
  function ci(s) { return (s || '').toString().toLowerCase(); }

  function localSearch(params) {
    if (!mem) return [];
    var q = ci(params.get('q')).trim(), cat = params.get('categoryId');
    return mem.products.filter(function (p) {
      if (cat && String(p.categoryId) !== String(cat)) return false;
      if (!q) return true;
      return ci(p.name).indexOf(q) >= 0 || ci(p.sku).indexOf(q) >= 0 || ci(p.barcode).indexOf(q) >= 0;
    }).slice(0, 40);
  }
  function localCustomers(params) {
    if (!mem) return [];
    var q = ci(params.get('q')).trim(), list = mem.customers;
    if (!q) list = list.slice(0, 20);
    else list = list.filter(function (c) { return ci(c.name).indexOf(q) >= 0 || ci(c.phone).indexOf(q) >= 0 || ci(c.email).indexOf(q) >= 0; }).slice(0, 15);
    return list.map(function (c) { return { id: c.id, name: c.name, phone: c.phone }; });
  }
  function localFor(path, params) {
    var p = path.toLowerCase();
    if (p === '/pos/search') return localSearch(params);
    if (p === '/pos/categories') return mem ? mem.categories : [];
    if (p === '/pos/discountreasons') return mem ? mem.discountReasons : [];
    if (p === '/pos/customersearch') return localCustomers(params);
    return undefined;
  }

  // ── Offline checkout capture ────────────────────────────────────────────────
  function rid() { return (Date.now().toString(36) + Math.random().toString(36).slice(2, 10)); }
  function findProduct(id) { return mem ? mem.products.find(function (p) { return p.id === id; }) : null; }

  function captureOfflineSale(body) {
    var sale;
    try { sale = JSON.parse(body || '{}'); } catch (e) { return { success: false, message: 'Bad sale data.' }; }
    var items = sale.items || [];
    if (!items.length) return { success: false, message: 'Cart is empty.' };

    var subtotal = 0, discount = 0;
    items.forEach(function (it) {
      var prod = findProduct(it.productId);
      var unit = prod ? prod.price : 0;
      if (prod && it.variantId && prod.variants) {
        var v = prod.variants.find(function (x) { return x.id === it.variantId; });
        if (v && v.priceAdjustment) unit += v.priceAdjustment;
      }
      var qty = Math.max(1, it.quantity || 1);
      subtotal += unit * qty;
      discount += Math.max(0, Math.min(it.discountAmount || 0, unit * qty));
    });
    var total = subtotal - discount;
    var tendered = sale.amountTendered > 0 ? sale.amountTendered : total;
    var change = Math.max(0, tendered - total);

    var clientId = rid();
    queue.push({
      clientId: clientId,
      paymentMethod: sale.paymentMethod || 'Cash',
      amountTendered: sale.amountTendered || 0,
      customerUserId: sale.customerUserId || null,
      createdAt: new Date().toISOString(),
      items: items
    });
    saveQueue();

    // Decrement the local stock snapshot so the catalogue reflects what's left this shift.
    items.forEach(function (it) {
      var prod = findProduct(it.productId);
      if (prod) prod.stock = Math.max(0, (prod.stock || 0) - Math.max(1, it.quantity || 1));
    });
    if (mem) idbPut(SNAP_KEY, mem);

    updateSyncUi();
    return { success: true, offline: true, orderNumber: 'OFFLINE-' + clientId.slice(-6).toUpperCase(), total: total, change: change };
  }

  // ── fetch shim ──────────────────────────────────────────────────────────────
  window.fetch = function (input, init) {
    try {
      var method = ((init && init.method) || (input && input.method) || 'GET').toUpperCase();
      var urlStr = typeof input === 'string' ? input : (input && input.url) || '';
      var url = new URL(urlStr, location.origin);
      var path = url.pathname.toLowerCase();

      // Offline-capture checkout: network-first; queue locally only on a true network failure.
      if (method === 'POST' && path === '/pos/checkout' && url.origin === location.origin) {
        return realFetch(input, init).catch(function () {
          return jsonResponse(captureOfflineSale(init && init.body));
        });
      }

      // Read endpoints: network-first, fall back to the snapshot offline.
      if (method === 'GET' && url.origin === location.origin && localFor(path, url.searchParams) !== undefined) {
        return realFetch(input, init).catch(function () { return jsonResponse(localFor(path, url.searchParams) || []); });
      }

      return realFetch(input, init);
    } catch (e) { return realFetch(input, init); }
  };

  // ── Snapshot refresh ──────────────────────────────────────────────────────
  function refreshSnapshot() {
    return realFetch('/Pos/Snapshot', { headers: { 'X-Requested-With': 'fetch' } })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (data) {
        if (!data || data.ok === false) return false;
        // Re-apply pending offline deductions so the fresh snapshot still reflects un-synced sales.
        applyQueueToSnapshot(data);
        mem = data; idbPut(SNAP_KEY, data); updateBanner(); return true;
      })
      .catch(function () { return false; });
  }
  function applyQueueToSnapshot(snap) {
    queue.forEach(function (s) { (s.items || []).forEach(function (it) {
      var prod = snap.products.find(function (p) { return p.id === it.productId; });
      if (prod) prod.stock = Math.max(0, (prod.stock || 0) - Math.max(1, it.quantity || 1));
    }); });
  }

  // ── Sync engine ─────────────────────────────────────────────────────────────
  function token() { return (document.querySelector('input[name="__RequestVerificationToken"]') || {}).value || ''; }

  function sync(manual) {
    if (syncing) return Promise.resolve({ ok: false, busy: true });
    if (!queue.length) { updateSyncUi(); if (manual) toast('Nothing to sync — all sales are up to date.'); return Promise.resolve({ ok: true, synced: 0 }); }
    if (!navigator.onLine) { if (manual) toast("You're offline — sales will sync when you reconnect.", true); return Promise.resolve({ ok: false, offline: true }); }

    syncing = true; updateSyncUi();
    var batch = queue.slice();
    return realFetch('/Pos/SyncSales', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token() },
      body: JSON.stringify({ sales: batch })
    })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (resp) {
        if (!resp || !resp.results) { if (manual) toast('Could not sync right now. Will retry.', true); return { ok: false }; }
        var doneIds = {}, oversold = 0;
        resp.results.forEach(function (res) {
          if (res.success) { doneIds[res.clientId] = true; if (res.oversold) oversold++; }
        });
        queue = queue.filter(function (s) { return !doneIds[s.clientId]; });
        saveQueue();
        var n = Object.keys(doneIds).length;
        var t = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        if (n > 0) toast('Successfully synced ' + n + ' sale' + (n === 1 ? '' : 's') + ' at ' + t + (oversold ? ' · ' + oversold + ' need review' : ''));
        else if (manual) toast('Could not sync right now. Will retry.', true);
        refreshSnapshot();
        return { ok: true, synced: n, oversold: oversold };
      })
      .catch(function () { if (manual) toast('Could not sync right now. Will retry.', true); return { ok: false }; })
      .finally(function () { syncing = false; updateSyncUi(); });
  }

  // ── UI: offline banner, sync indicators, toast ───────────────────────────────
  var bar, toastEl;
  function syncedText() {
    if (!mem || !mem.syncedAt) return 'not yet synced';
    return 'synced ' + new Date(mem.syncedAt).toLocaleString([], { hour: '2-digit', minute: '2-digit', day: '2-digit', month: 'short' });
  }
  function ensureBar() {
    if (bar) return bar;
    bar = document.createElement('div');
    bar.id = 'sgpos-offline-bar';
    bar.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:9999;display:none;background:#92400e;color:#fff;' +
      'font:500 13px/1.4 ui-sans-serif,system-ui,sans-serif;padding:7px 14px;text-align:center;box-shadow:0 1px 4px rgba(0,0,0,.2)';
    document.body.appendChild(bar);
    return bar;
  }
  function updateBanner() {
    var b = ensureBar();
    if (navigator.onLine) { b.style.display = 'none'; document.body.style.paddingTop = ''; }
    else {
      var pending = queue.length ? ' · ' + queue.length + ' sale' + (queue.length === 1 ? '' : 's') + ' waiting to sync' : '';
      b.textContent = '⚠ Offline — selling from saved catalogue (' + syncedText() + ')' + pending + '.';
      b.style.display = 'block'; document.body.style.paddingTop = b.offsetHeight + 'px';
    }
  }
  function updateSyncUi() {
    var badge = document.getElementById('sync-pending-count');
    var status = document.getElementById('sync-status');
    if (badge) { if (queue.length) { badge.textContent = queue.length; badge.classList.remove('hidden'); } else badge.classList.add('hidden'); }
    if (status) status.textContent = syncing ? 'Syncing…' : (queue.length ? (queue.length + ' sale(s) waiting to sync') : ('All sales synced · ' + syncedText()));
    updateBanner();
  }
  function toast(msg, warn) {
    if (!toastEl) {
      toastEl = document.createElement('div');
      toastEl.style.cssText = 'position:fixed;left:50%;bottom:22px;transform:translateX(-50%);z-index:10000;display:none;' +
        'padding:10px 18px;border-radius:8px;color:#fff;font:500 13px/1.4 ui-sans-serif,system-ui,sans-serif;box-shadow:0 4px 16px rgba(0,0,0,.25);max-width:90vw;text-align:center';
      document.body.appendChild(toastEl);
    }
    toastEl.style.background = warn ? '#b45309' : '#047857';
    toastEl.textContent = msg;
    toastEl.style.display = 'block';
    clearTimeout(toastEl._t);
    toastEl._t = setTimeout(function () { toastEl.style.display = 'none'; }, 4200);
  }

  function wireMenuButton() {
    var btn = document.getElementById('menu-sync-btn');
    if (btn && !btn._wired) { btn._wired = true; btn.addEventListener('click', function () { sync(true); }); }
  }

  // ── Init ──────────────────────────────────────────────────────────────────
  function init() {
    Promise.all([idbGet(SNAP_KEY), idbGet(QUEUE_KEY)]).then(function (vals) {
      if (vals[0] && !mem) mem = vals[0];
      if (Array.isArray(vals[1])) queue = vals[1];
      wireMenuButton();
      updateSyncUi();
      if (navigator.onLine) refreshSnapshot().then(function () { if (queue.length) sync(false); });
    });
    window.addEventListener('online', function () { updateBanner(); refreshSnapshot().then(function () { sync(false); }); });
    window.addEventListener('offline', updateBanner);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();

  window.SGPOS = {
    refresh: refreshSnapshot,
    snapshot: function () { return mem; },
    queue: function () { return queue.slice(); },
    pending: function () { return queue.length; },
    sync: function () { return sync(true); },
    lastSyncedText: syncedText,
    isOnline: function () { return navigator.onLine; }
  };
})();
