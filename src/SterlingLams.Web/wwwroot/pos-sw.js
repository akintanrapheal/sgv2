/*
 * Sterlin Glams POS — service worker (Phase 0: installable app + shell caching).
 * Scope: /Pos. Makes the till load instantly and survive a flaky/lost connection by serving the
 * cached app shell + static assets. It does NOT yet cache catalog data or queue offline sales —
 * that's Phase 1/2 (IndexedDB + sync). POSTs and JSON endpoints are always network-only here.
 *
 * Bump CACHE when shell assets change so old caches are cleared on activate.
 */
const CACHE = 'sgpos-shell-v2';

// Same-origin static assets that make up the shell. (Versionless URLs resolve to the current file;
// the page references them with ?v=<hash>, but the bare path returns the same bytes.)
const PRECACHE = [
  '/css/app.css',
  '/js/pos-pwa.js',
  '/js/pos-offline.js',
  '/js/jsbarcode.min.js',
  '/pos.webmanifest',
  '/icons/pos-192.png',
  '/icons/pos-512.png',
  '/favicon-32.png',
  '/apple-touch-icon.png'
];

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches.open(CACHE)
      // addAll fails the whole install if any URL 404s; add individually + tolerate misses.
      .then((cache) => Promise.allSettled(PRECACHE.map((u) => cache.add(u))))
      .then(() => self.skipWaiting())
  );
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', (event) => {
  const req = event.request;
  if (req.method !== 'GET') return; // never touch POST/checkout/etc.

  const url = new URL(req.url);
  if (url.origin !== self.location.origin) return; // let cross-origin (fonts/CDN) pass through

  // App navigations (the POS pages): network-first so staff get fresh server state when online,
  // fall back to the cached page when offline (shell still loads instead of the browser error page).
  if (req.mode === 'navigate') {
    event.respondWith(
      fetch(req)
        .then((res) => { cachePut(req, res.clone()); return res; })
        .catch(() => caches.match(req).then((c) => c || caches.match('/Pos')))
    );
    return;
  }

  // Static shell assets: stale-while-revalidate (instant from cache, refresh in the background).
  if (/\.(css|js|png|jpg|jpeg|svg|webp|woff2?|ttf|ico|webmanifest)$/i.test(url.pathname)) {
    event.respondWith(
      caches.match(req).then((cached) => {
        const network = fetch(req).then((res) => { cachePut(req, res.clone()); return res; }).catch(() => cached);
        return cached || network;
      })
    );
  }
  // Everything else (JSON API endpoints): default network — Phase 1/2 will add offline data.
});

function cachePut(req, res) {
  if (!res || res.status !== 200 || res.type === 'opaque') return;
  caches.open(CACHE).then((cache) => cache.put(req, res)).catch(() => {});
}
