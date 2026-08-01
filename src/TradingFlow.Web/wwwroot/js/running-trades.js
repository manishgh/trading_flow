(() => {
    const page = document.querySelector("[data-running-trades]");
    if (!page) return;

    const dialog = document.getElementById("ExitReviewDialog");
    const status = document.getElementById("TradesConnection");
    const source = page.dataset.source || "all";
    let loading = false;

    const money = value => Number(value || 0).toLocaleString(undefined, { style: "currency", currency: "USD" });
    const tradeKey = trade => `${trade.closeKind}:${trade.jobId || trade.sessionId || "none"}:${trade.ticker}`;
    const plColor = value => Number(value) > 0 ? "#067647" : Number(value) < 0 ? "#B42318" : "#475467";

    page.querySelectorAll("[data-review-exit]").forEach(button => {
        button.addEventListener("click", () => {
            dialog.querySelector("[data-exit-close-kind]").value = button.dataset.closeKind || "";
            dialog.querySelector("[data-exit-job-id]").value = button.dataset.jobId || "";
            dialog.querySelector("[data-exit-session-id]").value = button.dataset.sessionId || "";
            dialog.querySelector("[data-exit-ticker]").value = button.dataset.ticker || "";
            dialog.querySelector("[data-exit-summary]").textContent =
                `Close ${button.dataset.quantity} share(s) of ${button.dataset.ticker} near ${money(button.dataset.currentPrice)}?`;
            dialog.querySelector("[data-exit-protection]").textContent = button.dataset.protection || "Not reported";
            dialog.showModal();
        });
    });

    dialog?.querySelector("[data-exit-cancel]")?.addEventListener("click", () => dialog.close());
    page.querySelector("[data-trades-refresh]")?.addEventListener("click", () => window.location.reload());

    async function poll() {
        if (loading) return;
        loading = true;
        try {
            const response = await fetch(`/api/mobile/running-trades?source=${encodeURIComponent(source)}`, {
                headers: { Accept: "application/json" }
            });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const snapshot = await response.json();
            const rows = new Map([...page.querySelectorAll("[data-trade-key]")]
                .map(row => [row.dataset.tradeKey, row]));
            const incoming = new Set(snapshot.trades.map(tradeKey));

            for (const trade of snapshot.trades) {
                const row = rows.get(tradeKey(trade));
                if (!row) continue;
                row.querySelector("[data-trade-current]").textContent = money(trade.currentPrice);
                const pl = row.querySelector("[data-trade-pl]");
                const plPct = row.querySelector("[data-trade-pl-pct]");
                pl.textContent = money(trade.unrealizedPl);
                plPct.textContent = `${Number(trade.unrealizedPlPct).toFixed(2)}%`;
                pl.style.color = plColor(trade.unrealizedPl);
                plPct.style.color = plColor(trade.unrealizedPl);
                row.querySelector("[data-trade-protection]").textContent = trade.protectionSummary;
                const review = row.querySelector("[data-review-exit]");
                review.dataset.currentPrice = trade.currentPrice;
                review.dataset.protection = trade.protectionSummary;
            }

            const total = page.querySelector("[data-total-pl]");
            total.textContent = money(snapshot.totalUnrealizedPl);
            total.style.color = plColor(snapshot.totalUnrealizedPl);
            page.querySelector("[data-trade-count]").textContent = snapshot.count;

            const changed = snapshot.trades.some(trade => !rows.has(tradeKey(trade))) ||
                [...rows.keys()].some(key => !incoming.has(key));
            status.textContent = changed
                ? "Position membership changed. Use Refresh positions to reconcile the list without an automatic focus reset."
                : `Position values checked at ${new Date().toLocaleTimeString()}.`;
        } catch (error) {
            status.textContent = `Position updates disconnected: ${error.message}. Displayed values may be stale.`;
        } finally {
            loading = false;
        }
    }

    window.setInterval(poll, 10000);
})();
