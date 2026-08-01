(() => {
    const page = document.querySelector("[data-orders-page]");
    if (!page) return;

    const status = document.getElementById("OrdersConnection");
    const refresh = page.querySelector("[data-orders-refresh]");
    const filter = page.dataset.filter || "all";
    let loading = false;

    const isWorking = state => ["Intent", "Submitted", "Acked", "PartiallyFilled", "CancelPending"].includes(state);
    const matchesFilter = state => filter === "all" ||
        (filter === "working" && isWorking(state)) ||
        (filter === "filled" && state === "Filled") ||
        (filter === "rejected" && state === "Rejected") ||
        (filter === "closed" && ["Canceled", "Expired"].includes(state));

    const formatState = value => value.replace(/([a-z])([A-Z])/g, "$1 $2");
    const formatQuantity = value => Number(value || 0).toLocaleString(undefined, { maximumFractionDigits: 4 });
    const formatMoney = value => Number(value).toLocaleString(undefined, { style: "currency", currency: "USD" });
    const priceText = item => item.fillPrice != null ? `Fill ${formatMoney(item.fillPrice)}` :
        item.limitPrice != null ? `Limit ${formatMoney(item.limitPrice)}` :
        item.stopPrice != null ? `Stop ${formatMoney(item.stopPrice)}` : "Market";

    function updateLocalTimes() {
        page.querySelectorAll("[data-order-time]").forEach(time => {
            const output = time.parentElement?.querySelector("[data-order-local-time]");
            const value = new Date(time.dateTime);
            if (output && !Number.isNaN(value.valueOf())) {
                output.textContent = `${value.toLocaleString()} local`;
            }
        });
    }

    async function poll() {
        if (loading) return;
        loading = true;
        try {
            const response = await fetch("/api/mobile/orders?limit=500", { headers: { Accept: "application/json" } });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const items = (await response.json()).filter(item => matchesFilter(item.state));
            const rows = new Map([...page.querySelectorAll("[data-client-order-id]")]
                .map(row => [row.dataset.clientOrderId, row]));
            const incoming = new Set(items.map(item => item.clientOrderId));

            for (const item of items) {
                const row = rows.get(item.clientOrderId);
                if (!row) continue;
                const stateElement = row.querySelector("[data-order-state]");
                stateElement.textContent = formatState(item.state);
                stateElement.className = `order-state state-${item.state.toLowerCase()}`;
                row.querySelector("[data-order-filled]").textContent = formatQuantity(item.filledQuantity);
                row.querySelector("[data-order-price]").textContent = priceText(item);
                const time = row.querySelector("[data-order-time]");
                time.dateTime = item.updatedAtUtc;
                time.textContent = `${new Date(item.updatedAtUtc).toISOString().slice(5, 19).replace("T", " ")} UTC`;
            }

            const changed = items.some(item => !rows.has(item.clientOrderId)) ||
                [...rows.keys()].some(key => !incoming.has(key));
            status.textContent = changed
                ? "Order membership changed. Use Refresh to reconcile the list without an automatic focus reset."
                : `Order states checked at ${new Date().toLocaleTimeString()}.`;
            updateLocalTimes();
        } catch (error) {
            status.textContent = `Order updates disconnected: ${error.message}. Persisted values remain visible and may be stale.`;
        } finally {
            loading = false;
        }
    }

    refresh?.addEventListener("click", () => window.location.reload());
    updateLocalTimes();
    window.setInterval(poll, 10000);
})();
