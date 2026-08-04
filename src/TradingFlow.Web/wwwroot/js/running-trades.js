(() => {
    const page = document.querySelector("[data-running-trades]");
    if (!page) return;

    // A locked environment renders neither the review buttons nor the dialog, so
    // every reference to them below has to tolerate their absence.
    const dialog = document.getElementById("ExitReviewDialog");
    const status = document.getElementById("TradesConnection");
    const source = page.dataset.source || "all";
    let loading = false;

    const money = value => Number(value || 0).toLocaleString(undefined, { style: "currency", currency: "USD" });
    const setPlClass = (element, value) => {
        element.classList.remove("value-good", "value-bad", "value-flat");
        element.classList.add(value > 0 ? "value-good" : value < 0 ? "value-bad" : "value-flat");
    };

    const tradeKey = trade => `${trade.closeKind}:${trade.jobId || trade.sessionId || "none"}:${trade.ticker}`;

    // Stale is a state, not a sentence. It reuses the shared status-banner classes so
    // a lost connection looks the same here as it does on every other route.
    function setConnectionState(state, message) {
        status.textContent = message;
        status.dataset.state = state;
        status.classList.toggle("status-banner", state === "stale");
        status.classList.toggle("error", state === "stale");
        status.classList.toggle("muted", state !== "stale");
    }

    // The close POST re-renders the page in place when the environment is locked, so
    // the refusal has to be reachable by keyboard rather than left on <body>.
    page.querySelector("[data-trades-banner]")?.focus();

    page.querySelectorAll("[data-review-exit]").forEach(button => {
        button.addEventListener("click", () => {
            if (!dialog) return;
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
                setPlClass(pl, trade.unrealizedPl);
                setPlClass(plPct, trade.unrealizedPl);
                row.querySelector("[data-trade-protection]").textContent = trade.protectionSummary;
                const review = row.querySelector("[data-review-exit]");
                if (review) {
                    review.dataset.currentPrice = trade.currentPrice;
                    review.dataset.protection = trade.protectionSummary;
                }
            }

            const total = page.querySelector("[data-total-pl]");
            total.textContent = money(snapshot.totalUnrealizedPl);
            setPlClass(total, snapshot.totalUnrealizedPl);
            page.querySelector("[data-trade-count]").textContent = snapshot.count;

            const changed = snapshot.trades.some(trade => !rows.has(tradeKey(trade))) ||
                [...rows.keys()].some(key => !incoming.has(key));
            if (changed) {
                setConnectionState(
                    "changed",
                    "Position membership changed. Use Refresh positions to reconcile the list without an automatic focus reset.");
            } else {
                setConnectionState("live", `Position values checked at ${new Date().toLocaleTimeString()}.`);
            }
        } catch (error) {
            setConnectionState(
                "stale",
                `Position updates disconnected: ${error.message}. Displayed values may be stale.`);
        } finally {
            loading = false;
        }
    }

    window.setInterval(poll, 10000);
})();
