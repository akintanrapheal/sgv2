// ─── Mobile Menu ──────────────────────────────────────────────────────────
document.getElementById('mobile-menu-toggle')?.addEventListener('click', () => {
    const menu = document.getElementById('mobile-menu');
    menu?.classList.toggle('hidden');
});

// ─── Cart Badge Update ────────────────────────────────────────────────────
function updateCartBadge(count) {
    const badge = document.getElementById('cart-badge');
    if (!badge) return;
    badge.textContent = count;
    badge.classList.toggle('hidden', count === 0);
}

// ─── Wishlist Toggle (list page) ──────────────────────────────────────────
document.querySelectorAll('.wishlist-toggle').forEach(btn => {
    btn.addEventListener('click', async (e) => {
        e.preventDefault();
        e.stopPropagation();

        const productId = btn.dataset.productId;
        const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value ?? '';

        try {
            const res = await fetch('/Wishlist/Toggle', {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                body: `productId=${productId}&__RequestVerificationToken=${encodeURIComponent(token)}`
            });
            const data = await res.json();
            if (data.success) {
                const svg = btn.querySelector('svg');
                if (svg) {
                    svg.setAttribute('fill', data.added ? 'currentColor' : 'none');
                }
            }
        } catch (err) {
            console.error('Wishlist toggle failed', err);
        }
    });
});
