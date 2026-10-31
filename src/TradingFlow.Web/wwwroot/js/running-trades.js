(() => {
    const summary = document.querySelector("[data-running-trades]");
    const body = document.querySelector(".positions-body");
    if (!summary || !body) return;

    const dialog = document.getElementById("ExitReviewDialog");
    const status = document.getElementById("TradesConnection");
    const source = summary.dataset.source || "all";
    let loading = false;

    // Nullable broker values are unknown, not zero-valued prices or flat P/L.
    const money = value => value == null ? 'Unavailable' : Number(value).toLocaleString(undefined, { style: "currency", currency: "USD" });
    const signedMoney = value => value == null ? 'Unavailable' : `${Number(value) > 0 ? "+" : Number(value) < 0 ? "-" : ""}${money(Math.abs(Number(value)))}`;
    const signedPercent = value => value == null ? 'Unavailable' : `${Number(value) > 0 ? "+" : Number(value) < 0 ? "-" : ""}${Math.abs(Number(value)).toFixed(2)}%`;
    // A broker holding has one row even if its local ownership labels change.
    const tradeKey = trade => `account:${trade.ticker}`;
    const sourceLabel = value => ({ wishlist: "Wishlist", stockpulse: "Stock Pulse", manual: "Manual", account: "Account holding", multiple: "Multiple runs" })[value] || value;
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

    // An absent P/L read must not acquire a positive/negative/flat value label.
    function signedClass(element, value) {
        element.classList.remove("value-good", "value-bad", "value-flat", "up", "down", "flat");
        element.classList.add("value-signed", value > 0 ? "up" : value < 0 ? "down" : "flat");
        element.classList.toggle("unknown", value == null);
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

    // Refresh broker quantities/prices and supplemental labels without substituting local values.
    function updateRow(row, trade) {
        const quantity = row.querySelector('[data-trade-quantity]');
        if (quantity) quantity.textContent = Number(trade.quantity).toLocaleString(undefined, { maximumFractionDigits: 4 });
        const entry = row.querySelector('[data-trade-entry]');
        if (entry) entry.textContent = money(trade.entryPrice);
        const current = row.querySelector("[data-trade-current]");
        if (current) current.textContent = money(trade.currentPrice);
        const pl = row.querySelector("[data-trade-pl]");
        if (pl) {
            pl.textContent = signedMoney(trade.unrealizedPl);
            signedClass(pl, trade.unrealizedPl);
        }
        const plPct = row.querySelector("[data-trade-pl-pct]");
        if (plPct) {
            plPct.textContent = signedPercent(trade.unrealizedPlPct);
            signedClass(plPct, trade.unrealizedPl);
        }
        const protection = row.querySelector("[data-trade-protection]");
        if (protection) {
            protection.textContent = trade.protectionSummary || "Not reported";
            protection.title = trade.protectionSummary || "Not reported";
        }
        const strategy = row.querySelector('[data-trade-strategy]');
        if (strategy) strategy.textContent = trade.strategyName || 'Not linked to a strategy';
        const reference = row.querySelector('[data-trade-reference]');
        if (reference) reference.textContent = trade.reference || 'Not linked to a local run';
        const actions = row.querySelector('[data-trade-actions]');
        if (actions && dialog) {
            if (trade.canReviewExit && !actions.querySelector('[data-review-exit]')) {
                actions.replaceChildren();
                const button = append(actions, 'button', 'Review exit', 'btn btn-danger btn-sm');
                button.type = 'button';button.dataset.reviewExit = '';
            } else if (!trade.canReviewExit && !actions.querySelector('a')) {
                actions.replaceChildren();
                const link = append(actions, 'a', 'Review in Orders', 'btn btn-secondary btn-sm');
                link.href = '/Orders';
            }
        }
        const review = row.querySelector("[data-review-exit]");
        if (review) {
            review.disabled = false;
            review.dataset.ticker = trade.ticker;
            review.dataset.closeKind = trade.closeKind;
            review.dataset.jobId = trade.jobId || '';
            review.dataset.sessionId = trade.sessionId || '';
            review.dataset.currentPrice = trade.currentPrice;
            review.dataset.protection = trade.protectionSummary || "Not reported";
            review.dataset.quantity = trade.quantity;
        }
        const source = row.querySelector('[data-trade-source]');
        if (source) source.textContent = sourceLabel(trade.source);
    }

    // Create one row for a broker holding, including unowned holdings and missing market fields.
    function createRow(trade) {
        const row = document.createElement("tr");
        row.dataset.tradeKey = tradeKey(trade);
        const symbol = append(row, "td");
        append(symbol, "strong", trade.ticker);
        append(symbol, "span", trade.status || "Open", "cell-sub");
        append(append(row, "td"), "span", sourceLabel(trade.source), "tag tag-neutral").dataset.tradeSource = '';
        append(row, "td", Number(trade.quantity).toLocaleString(undefined, { maximumFractionDigits: 4 }), "num").dataset.tradeQuantity = '';
        append(row, "td", money(trade.entryPrice), "num").dataset.tradeEntry = '';
        append(row, "td", "", "num").dataset.tradeCurrent = "";
        append(row, "td", "", "num position-pl").dataset.tradePl = "";
        append(row, "td", "", "num").dataset.tradePlPct = "";
        append(row, "td", trade.strategyName || "Not linked to a strategy").dataset.tradeStrategy = '';
        append(row, "td", "").dataset.tradeProtection = "";
        append(row, "td", trade.reference || "Not linked to a local run", "cell-sub").dataset.tradeReference = '';
        const actions = append(row, "td", "", "num");
        actions.dataset.tradeActions = '';
        if (dialog && trade.canReviewExit) {
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

    // Sort only displayed rows; unavailable numeric values sort after observed values.
    function sortTrades(trades) {
        const params = new URLSearchParams(window.location.search);
        const key = params.get("sort");
        const direction = params.get("dir") === "desc" ? -1 : 1;
        if (!key) return trades;
        const value = trade => key === "symbol" ? trade.ticker : key === "entry" ? trade.entryPrice :
            key === "pl" ? trade.unrealizedPl : trade.unrealizedPlPct;
        return trades.slice().sort((left, right) => {
            const a = value(left);
            const b = value(right);
            if (a == null || b == null) return a == null && b == null ? left.ticker.localeCompare(right.ticker) : a == null ? 1 : -1;
            const compared = typeof a === "string" ? a.localeCompare(b) : a - b;
            return compared !== 0 ? compared * direction : left.ticker.localeCompare(right.ticker);
        });
    }

    // Remove both empty-account and unavailable-account messages when broker data becomes available.
    function removeEmptyState() {
        body.querySelector("[data-trades-empty]")?.remove();
        body.querySelector("[data-trades-unavailable]")?.remove();
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

    // A successful empty result is distinct from a failed account read.
    function renderEmptyState() {
        body.querySelector('[data-trades-unavailable]')?.remove();
        body.querySelector(".table-scroll")?.remove();
        if (body.querySelector("[data-trades-empty]")) return;
        const empty = document.createElement("p");
        empty.className = "empty-state";
        empty.dataset.tradesEmpty = "";
        empty.textContent = source === "all"
            ? "The broker reported no open holdings in the bound paper account."
            : "No account holdings match this source. Switch the source filter to All.";
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

    // All figures come from full-account totals, regardless of the active source filter.
    function updateSummary(snapshot) {
        const banner = document.querySelector('[data-trades-banner][data-account-read-error="true"]');
        if (snapshot.available) banner?.remove();
        const total = summary.querySelector("[data-total-pl]");
        if (total) {
            total.textContent = signedMoney(snapshot.totalUnrealizedPl);
            signedClass(total, snapshot.totalUnrealizedPl);
        }
        const count = summary.querySelector("[data-trade-count]");
        if (count) count.textContent = snapshot.slotsUsed == null ? 'Unknown' : snapshot.slotsUsed;
        const exposure = summary.querySelector('[data-gross-exposure]');
        if (exposure) exposure.textContent = money(snapshot.grossExposure);
        const slots = summary.querySelector('[data-slots-used]');
        if (slots) slots.textContent = snapshot.slotsUsed == null ? 'Unknown' : snapshot.slotsUsed + ' of ' + (snapshot.maxConcurrentPositions ?? 'unknown');
        const unprotected = summary.querySelector('[data-unprotected-count]');
        if (unprotected) unprotected.textContent = snapshot.unprotectedCount ?? 'Unknown';
        const unknown = summary.querySelector('[data-unknown-protection]');
        if (unknown) { unknown.textContent = (snapshot.unknownProtectionCount ?? 'Unknown') + ' unknown';unknown.hidden = !(snapshot.unknownProtectionCount > 0); }
        const detail = document.querySelector('[data-account-detail]');
        if (detail) detail.textContent = snapshot.detail + ' · ' + snapshot.readAtUtc + '. Source filters change the rows, not account totals.';
    }

    // A failed account read cannot be presented as an empty portfolio or keep enabled stale exit buttons.
    function renderUnavailable(snapshot) {
        body.querySelector('.table-scroll')?.remove();
        removeEmptyState();
        const message = append(body, 'p', snapshot.detail + ' This does not mean the account has no positions.', 'empty-state');
        message.dataset.tradesUnavailable = '';
    }

    // Refresh a coherent broker snapshot and its read status, withholding empty-account claims after failures.
    async function poll() {
        if (loading) return;
        loading = true;
        try {
            const response = await window.tradingFlowFetch(`/api/v1/running-trades?source=${encodeURIComponent(source)}`, {
                headers: { Accept: "application/json" }
            });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const snapshot = await response.json();
            updateSummary(snapshot);
            if (snapshot.available) {
                reconcile(snapshot.trades);
                connection("live", `Account holdings read at ${new Date(snapshot.readAtUtc).toLocaleTimeString()}.`);
            } else {
                renderUnavailable(snapshot);
                connection('stale', snapshot.detail);
            }
        } catch (error) {
            body.querySelectorAll('[data-review-exit]').forEach(button => button.disabled = true);
            connection("stale", `Position updates disconnected: ${error.message}. Displayed values may be stale.`);
        } finally {
            loading = false;
        }
    }

    summary.addEventListener("tradingflow:refresh", poll);
    void poll();
    window.setInterval(poll, 10000);
})();
