(() => {
    const summary = document.querySelector("[data-running-trades]");
    const body = document.querySelector(".positions-body");
    if (!summary || !body) return;

    const dialog = document.getElementById("ExitReviewDialog");
    const status = document.getElementById("TradesConnection");
    const source = summary.dataset.source || "all";
    let loading = false;

    const money = value => Number(value || 0).toLocaleString(undefined, { style: "currency", currency: "USD" });
    const signedMoney = value => `${Number(value || 0) > 0 ? "+" : Number(value || 0) < 0 ? "-" : ""}${money(Math.abs(Number(value || 0)))}`;
    const signedPercent = value => `${Number(value || 0) > 0 ? "+" : Number(value || 0) < 0 ? "-" : ""}${Math.abs(Number(value || 0)).toFixed(2)}%`;
    const tradeKey = trade => `${trade.closeKind}:${trade.jobId || trade.sessionId || "none"}:${trade.ticker}`;
    const sourceLabel = value => ({ wishlist: "Wishlist", stockpulse: "Stock Pulse", manual: "Manual" })[value] || value;
    const columns = [
        ["symbol", "Ticker", ""], [null, "Source", ""], [null, "Qty", "num"],
        ["entry", "Entry", "num"], [null, "Last", "num"], ["pl", "P/L", "num"],
        ["plpct", "P/L %", "num"], [null, "Strategy", ""], [null, "Protection", ""],
        [null, "Run", ""], [null, "Actions", "visually-hidden"]
    ];

    function append(parent, tagName, text, className) {
        const element = document.createElement(tagName);
        if (text != null) element.textContent = text;
        if (className) element.className = className;
        parent.append(element);
        return element;
    }

    function signedClass(element, value) {
        element.classList.remove("value-good", "value-bad", "value-flat", "up", "down", "flat");
        element.classList.add("value-signed", value > 0 ? "up" : value < 0 ? "down" : "flat");
    }

    function connection(state, message) {
        if (!status) return;
        status.textContent = message;
        status.dataset.state = state;
        status.classList.toggle("status-banner", state === "stale");
        status.classList.toggle("error", state === "stale");
        status.classList.toggle("muted", state !== "stale");
    }

    function openExitReview(button) {
        if (!dialog) return;
        dialog.querySelector("[data-exit-close-kind]").value = button.dataset.closeKind || "";
        dialog.querySelector("[data-exit-job-id]").value = button.dataset.jobId || "";
        dialog.querySelector("[data-exit-session-id]").value = button.dataset.sessionId || "";
        dialog.querySelector("[data-exit-ticker]").value = button.dataset.ticker || "";
        dialog.querySelector("[data-exit-summary]").textContent =
            `Close ${button.dataset.quantity} share(s) of ${button.dataset.ticker} near ${money(button.dataset.currentPrice)}?`;
        dialog.querySelector("[data-exit-protection]").textContent = button.dataset.protection || "Not reported";
        dialog.showModal();
    }

    body.addEventListener("click", event => {
        const button = event.target.closest("[data-review-exit]");
        if (button) openExitReview(button);
    });
    dialog?.querySelector("[data-exit-cancel]")?.addEventListener("click", () => dialog.close());
    body.querySelector("[data-trades-banner]")?.focus();

    function updateRow(row, trade) {
        const current = row.querySelector("[data-trade-current]");
        if (current) current.textContent = money(trade.currentPrice);
        const pl = row.querySelector("[data-trade-pl]");
        if (pl) {
            pl.textContent = signedMoney(trade.unrealizedPl);
            signedClass(pl, Number(trade.unrealizedPl || 0));
        }
        const plPct = row.querySelector("[data-trade-pl-pct]");
        if (plPct) {
            plPct.textContent = signedPercent(trade.unrealizedPlPct);
            signedClass(plPct, Number(trade.unrealizedPl || 0));
        }
        const protection = row.querySelector("[data-trade-protection]");
        if (protection) {
            protection.textContent = trade.protectionSummary || "Not reported";
            protection.title = trade.protectionSummary || "Not reported";
        }
        const review = row.querySelector("[data-review-exit]");
        if (review) {
            review.dataset.currentPrice = trade.currentPrice;
            review.dataset.protection = trade.protectionSummary || "Not reported";
            review.dataset.quantity = trade.quantity;
        }
    }

    function createRow(trade) {
        const row = document.createElement("tr");
        row.dataset.tradeKey = tradeKey(trade);
        const symbol = append(row, "td");
        append(symbol, "strong", trade.ticker);
        append(symbol, "span", trade.status || "Open", "cell-sub");
        append(append(row, "td"), "span", sourceLabel(trade.source), "tag tag-neutral");
        append(row, "td", Number(trade.quantity || 0).toLocaleString(undefined, { maximumFractionDigits: 4 }), "num");
        append(row, "td", money(trade.entryPrice), "num");
        append(row, "td", "", "num").dataset.tradeCurrent = "";
        append(row, "td", "", "num position-pl").dataset.tradePl = "";
        append(row, "td", "", "num").dataset.tradePlPct = "";
        append(row, "td", trade.strategyName || "-");
        append(row, "td", "").dataset.tradeProtection = "";
        append(row, "td", trade.reference || "-", "cell-sub");
        const actions = append(row, "td", "", "num");
        if (dialog) {
            const review = append(actions, "button", "Review exit", "btn btn-danger btn-sm");
            review.type = "button";
            review.dataset.reviewExit = "";
            review.dataset.ticker = trade.ticker;
            review.dataset.closeKind = trade.closeKind;
            review.dataset.jobId = trade.jobId || "";
            review.dataset.sessionId = trade.sessionId || "";
            review.dataset.quantity = trade.quantity;
        }
        updateRow(row, trade);
        return row;
    }

    function sortTrades(trades) {
        const params = new URLSearchParams(window.location.search);
        const key = params.get("sort");
        const direction = params.get("dir") === "desc" ? -1 : 1;
        if (!key) return trades;
        const value = trade => key === "symbol" ? trade.ticker : key === "entry" ? Number(trade.entryPrice || 0) :
            key === "pl" ? Number(trade.unrealizedPl || 0) : Number(trade.unrealizedPlPct || 0);
        return trades.slice().sort((left, right) => {
            const a = value(left);
            const b = value(right);
            const compared = typeof a === "string" ? a.localeCompare(b) : a - b;
            return compared !== 0 ? compared * direction : left.ticker.localeCompare(right.ticker);
        });
    }

    function removeEmptyState() {
        body.querySelector("[data-trades-empty]")?.remove();
        body.querySelector(".positions-empty-action")?.remove();
    }

    function ensureTable() {
        let tableBody = body.querySelector(".trade-table tbody");
        if (tableBody) return tableBody;
        removeEmptyState();
        const region = document.createElement("div");
        region.className = "table-scroll";
        region.setAttribute("role", "region");
        region.setAttribute("aria-label", "Open positions table");
        region.tabIndex = 0;
        const table = append(region, "table", null, "grid-table trade-table");
        const headerRow = append(append(table, "thead"), "tr");
        const params = new URLSearchParams(window.location.search);
        for (const [key, label, className] of columns) {
            const header = append(headerRow, "th", null, className);
            header.scope = "col";
            if (!key) {
                append(header, "span", label, className === "visually-hidden" ? "visually-hidden" : "");
                continue;
            }
            const link = append(header, "a", label, "sort-header");
            const next = new URL(window.location.href);
            next.searchParams.set("sort", key);
            next.searchParams.set("dir", params.get("sort") === key && params.get("dir") !== "desc" ? "desc" : "asc");
            link.href = `${next.pathname}${next.search}`;
        }
        tableBody = append(table, "tbody");
        body.insertBefore(region, body.querySelector(".positions-footnote"));
        return tableBody;
    }

    function renderEmptyState() {
        body.querySelector(".table-scroll")?.remove();
        if (body.querySelector("[data-trades-empty]")) return;
        const empty = document.createElement("p");
        empty.className = "empty-state";
        empty.dataset.tradesEmpty = "";
        empty.textContent = source === "all"
            ? "No position is open. Start a run from Paper Lab or review a protected buy on the Trade Desk."
            : "No position is open for this source. Switch the source filter to All or start a matching run.";
        body.insertBefore(empty, body.querySelector(".positions-footnote"));
    }

    function reconcile(trades) {
        const ordered = sortTrades(trades);
        const active = document.activeElement;
        const windowScroll = [window.scrollX, window.scrollY];
        const existingRegion = body.querySelector(".table-scroll");
        const regionScroll = existingRegion ? [existingRegion.scrollLeft, existingRegion.scrollTop] : null;
        if (ordered.length === 0) {
            renderEmptyState();
        } else {
            const tableBody = ensureTable();
            const rows = new Map([...tableBody.querySelectorAll("[data-trade-key]")]
                .map(row => [row.dataset.tradeKey, row]));
            const incoming = new Set(ordered.map(tradeKey));
            for (const [key, row] of rows) if (!incoming.has(key)) row.remove();
            for (const trade of ordered) {
                const key = tradeKey(trade);
                const row = rows.get(key) ?? createRow(trade);
                updateRow(row, trade);
                tableBody.append(row);
            }
        }
        const currentRegion = body.querySelector(".table-scroll");
        if (regionScroll && currentRegion === existingRegion) {
            currentRegion.scrollLeft = regionScroll[0];
            currentRegion.scrollTop = regionScroll[1];
        }
        if (active && document.contains(active)) active.focus({ preventScroll: true });
        window.scrollTo(windowScroll[0], windowScroll[1]);
    }

    function updateSummary(snapshot) {
        const total = summary.querySelector("[data-total-pl]");
        if (total) {
            total.textContent = signedMoney(snapshot.totalUnrealizedPl);
            signedClass(total, Number(snapshot.totalUnrealizedPl || 0));
        }
        const count = summary.querySelector("[data-trade-count]");
        if (count) count.textContent = snapshot.count;
    }

    async function poll() {
        if (loading) return;
        loading = true;
        try {
            const response = await fetch(`/api/v1/running-trades?source=${encodeURIComponent(source)}`, {
                headers: { Accept: "application/json" }
            });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const snapshot = await response.json();
            updateSummary(snapshot);
            reconcile(snapshot.trades);
            connection("live", `Position list checked at ${new Date().toLocaleTimeString()}.`);
        } catch (error) {
            connection("stale", `Position updates disconnected: ${error.message}. Displayed values may be stale.`);
        } finally {
            loading = false;
        }
    }

    summary.addEventListener("tradingflow:refresh", poll);
    void poll();
    window.setInterval(poll, 10000);
})();
