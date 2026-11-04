// Fills the top-right account figures from the shared portfolio API and refreshes them every 30 seconds.
(() => {
    const strip = document.querySelector('[data-account-strip]');
    if (!strip || !window.tradingFlowFetch) return;
    const invested = strip.querySelector('[data-account-invested]');
    const cash = strip.querySelector('[data-account-cash]');
    const money = value => value == null ? '—' : Number(value).toLocaleString(undefined, { style: 'currency', currency: 'USD', maximumFractionDigits: 0 });
    async function refresh() {
        try {
            const response = await window.tradingFlowFetch('/api/v1/portfolio', { headers: { Accept: 'application/json' } });
            if (!response.ok) throw new Error(`Account read failed (${response.status}).`);
            const account = await response.json();
            invested.textContent = account.available ? money(account.invested) : '—';
            cash.textContent = account.available ? money(account.cash) : '—';
            strip.title = account.detail + ' · read ' + new Date(account.readAtUtc).toLocaleTimeString();
        } catch (error) {
            invested.textContent = cash.textContent = '—';
            strip.title = error.message + ' Values are unknown, not zero.';
        }
    }
    refresh();
    setInterval(refresh, 30000);
})();
