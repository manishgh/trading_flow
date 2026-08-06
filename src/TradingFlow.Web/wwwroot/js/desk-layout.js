/* Desk view preferences: layout, row density, and optional columns.

   All three are per-operator choices about how to read the same data, so they
   live in localStorage and are reconciled over the server-rendered default.
   The server still renders a usable grid without this file running - it sets
   the baseline, and this only moves it. */
(() => {
    const root = document.querySelector("[data-desk-root]");
    if (!root) return;

    const KEYS = {
        layout: "tradingflow.desk.layout",
        density: "tradingflow.desk.density",
        columns: "tradingflow.desk.columns"
    };

    function read(key, fallback) {
        try {
            const value = window.localStorage.getItem(key);
            return value === null ? fallback : value;
        } catch {
            return fallback;
        }
    }

    function write(key, value) {
        try {
            window.localStorage.setItem(key, value);
        } catch {
            /* Private-mode or storage-disabled: apply for this document only. */
        }
    }

    /* --- Layout ---------------------------------------------------------- */

    const ladder = root.querySelector("[data-desk-ladder]");
    const grid = root.querySelector("[data-desk-grid]");
    const focus = root.querySelector("[data-desk-focus]");

    function applyLayout(layout) {
        const isFocus = layout === "focus";
        root.dataset.layout = isFocus ? "focus" : "rail";
        if (ladder) ladder.hidden = !isFocus;
        if (focus) focus.hidden = !isFocus;
        if (grid) grid.hidden = isFocus;
        for (const button of root.ownerDocument.querySelectorAll("[data-desk-layout]")) {
            button.setAttribute("aria-pressed", button.dataset.deskLayout === root.dataset.layout ? "true" : "false");
        }
    }

    for (const button of document.querySelectorAll("[data-desk-layout]")) {
        button.addEventListener("click", () => {
            write(KEYS.layout, button.dataset.deskLayout);
            applyLayout(button.dataset.deskLayout);
        });
    }
    applyLayout(read(KEYS.layout, "rail"));

    /* --- Density ---------------------------------------------------------- */

    function applyDensity(density) {
        const value = density === "compact" ? "compact" : "comfortable";
        for (const table of document.querySelectorAll(".trade-desk-table")) {
            table.dataset.density = value;
        }
        for (const button of document.querySelectorAll("[data-desk-density]")) {
            button.setAttribute("aria-pressed", button.dataset.deskDensity === value ? "true" : "false");
        }
    }

    for (const button of document.querySelectorAll("[data-desk-density]")) {
        button.addEventListener("click", () => {
            write(KEYS.density, button.dataset.deskDensity);
            applyDensity(button.dataset.deskDensity);
        });
    }
    applyDensity(read(KEYS.density, "comfortable"));

    /* --- Optional columns -------------------------------------------------
       Stored as a comma-separated allowlist. An unknown key in storage is
       ignored rather than hiding a column that no longer exists, so a stale
       preference cannot break a grid after a column set changes. */

    const toggles = Array.from(document.querySelectorAll("[data-desk-column-toggle]"));

    function visibleColumns() {
        const stored = read(KEYS.columns, null);
        if (stored === null) {
            return new Set(toggles
                .filter(toggle => toggle.getAttribute("aria-pressed") === "true")
                .map(toggle => toggle.value));
        }
        return new Set(stored.split(",").filter(Boolean));
    }

    function applyColumns(keys) {
        for (const toggle of toggles) {
            const shown = keys.has(toggle.value);
            toggle.setAttribute("aria-pressed", shown ? "true" : "false");
            for (const cell of document.querySelectorAll(`[data-column="${toggle.value}"]`)) {
                cell.hidden = !shown;
            }
        }
    }

    for (const toggle of toggles) {
        toggle.addEventListener("click", () => {
            const keys = visibleColumns();
            if (keys.has(toggle.value)) {
                keys.delete(toggle.value);
            } else {
                keys.add(toggle.value);
            }
            write(KEYS.columns, Array.from(keys).join(","));
            applyColumns(keys);
        });
    }
    applyColumns(visibleColumns());

    /* --- Ticket size presets and derived figures --------------------------
       These propose a quantity. They never submit, and the server sizes
       nothing from them: it re-checks whatever is posted. */

    const ticket = document.querySelector("[data-desk-ticket]");
    if (!ticket) return;

    const quantity = ticket.querySelector("[name='ticket.Quantity']");
    const limit = ticket.querySelector("[name='ticket.LimitPrice']");
    const stop = ticket.querySelector("[name='ticket.StopLossPrice']");
    const target = ticket.querySelector("[name='ticket.TakeProfitPrice']");
    const notionalNode = ticket.querySelector("[data-ticket-notional]");
    const riskNode = ticket.querySelector("[data-ticket-risk]");
    const rNode = ticket.querySelector("[data-ticket-r]");
    const quantityLabel = ticket.querySelector("[data-ticket-quantity-label]");

    const money = new Intl.NumberFormat(undefined, { style: "currency", currency: "USD" });

    function numberOf(node) {
        const value = Number.parseFloat(node?.value ?? "");
        return Number.isFinite(value) ? value : null;
    }

    function recalculate() {
        const qty = numberOf(quantity);
        const limitPrice = numberOf(limit);
        const stopPrice = numberOf(stop);
        const targetPrice = numberOf(target);

        if (notionalNode) {
            notionalNode.textContent = qty !== null && limitPrice !== null
                ? money.format(qty * limitPrice)
                : "—";
        }
        const perShare = limitPrice !== null && stopPrice !== null ? limitPrice - stopPrice : null;
        if (riskNode) {
            riskNode.textContent = perShare !== null && perShare > 0 && qty !== null
                ? money.format(perShare * qty)
                : "—";
        }
        if (rNode) {
            rNode.textContent = perShare !== null && perShare > 0 && targetPrice !== null
                ? ((targetPrice - limitPrice) / perShare).toFixed(2)
                : "—";
        }
        if (quantityLabel && qty !== null) {
            quantityLabel.textContent = String(qty);
        }
    }

    for (const node of [quantity, limit, stop, target]) {
        node?.addEventListener("input", recalculate);
    }
    recalculate();

    for (const preset of ticket.querySelectorAll("[data-size-preset]")) {
        preset.addEventListener("click", () => {
            const limitPrice = numberOf(limit);
            if (!quantity || limitPrice === null || limitPrice <= 0) return;

            const mode = preset.dataset.sizePreset;
            if (mode === "risk") {
                // 1R sizes to the account risk budget over the stop distance. The
                // budget is not on this screen, so the preset states what it is
                // sizing against rather than inventing a balance: it uses the
                // current notional as the ceiling and the stop distance as the risk.
                const stopPrice = numberOf(stop);
                const perShare = stopPrice === null ? null : limitPrice - stopPrice;
                if (perShare === null || perShare <= 0) return;
                const budget = numberOf(quantity) * limitPrice * 0.01;
                quantity.value = String(Math.max(1, Math.floor(budget / perShare)));
            } else {
                const fraction = Number.parseFloat(mode);
                const current = numberOf(quantity) ?? 1;
                quantity.value = String(Math.max(1, Math.floor(current * fraction)));
            }
            recalculate();
        });
    }

    /* Ticket token expiry. The server rejects an expired token regardless; this
       only stops the operator staring at a button that will fail. */
    const expiry = document.querySelector("[data-ticket-expiry]");
    if (expiry?.dataset.expiresAt) {
        const expiresAt = Date.parse(expiry.dataset.expiresAt);
        const tick = () => {
            const seconds = Math.max(0, Math.round((expiresAt - Date.now()) / 1000));
            expiry.textContent = seconds > 0
                ? `Ticket token expires in ${seconds} s…`
                : "Ticket token has expired. Review again to get a fresh one.";
            if (seconds <= 0) window.clearInterval(timer);
        };
        const timer = window.setInterval(tick, 1000);
        tick();
    }
})();
