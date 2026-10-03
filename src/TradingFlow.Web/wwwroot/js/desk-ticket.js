/* The inline order ticket's client-side conveniences, shared by the desk and
   the Orders screen: derived notional, risk and R multiple, size presets, and
   the review token's expiry countdown.

   None of this submits anything or sizes anything on the server's behalf. The
   server re-checks whatever is posted. */
(() => {
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
                // 1R sizes to the account's risk budget - equity times the paper
                // profile's risk percent, rendered by the server - over the stop
                // distance. Without a budget the button is disabled server-side.
                const budget = Number.parseFloat(preset.closest("[data-risk-budget]")?.dataset.riskBudget ?? "");
                const stopPrice = numberOf(stop);
                const perShare = stopPrice === null ? null : limitPrice - stopPrice;
                if (!Number.isFinite(budget) || budget <= 0 || perShare === null || perShare <= 0) return;
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
