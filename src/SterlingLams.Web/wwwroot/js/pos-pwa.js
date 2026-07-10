/*
 * Registers the POS service worker. External file (not inline) so it's cacheable and CSP-clean.
 * updateViaCache:'none' makes the browser always re-check pos-sw.js from the network, so a new
 * service worker ships promptly even though static assets are cached for a day.
 */
(function () {
  if (!('serviceWorker' in navigator)) return;
  window.addEventListener('load', function () {
    navigator.serviceWorker
      .register('/pos-sw.js', { scope: (window.__posBase || '/Pos'), updateViaCache: 'none' })
      .catch(function (err) { console.warn('POS service worker registration failed:', err); });
  });
})();
