(() => {
    const summary = document.querySelector("[data-orders-page]");
    const body = document.querySelector(".orders-body");
    if (!summary || !body) return;

    const status = document.getElementById("OrdersConnection");
    const filter = summary.dataset.filter || "all";
    const cancelTemplate = body.querySelector(".order-actions form")?.cloneNode(true) ?? null;
    let loading = false;

    const workingStates = ["Intent", "Submitted", "Acked", "PartiallyFilled", "CancelPending"];
    const isWorking = state => workingStates.includes(state);
    const matchesFilter = state => filter === "all" ||
        (filter === "working" && isWorking(state)) ||
        (filter === "filled" && state === "Filled") ||
        (filter === "rejected" && state === "Rejected") ||
        (filter === "closed" && ["Canceled", "Expired"].includes(state));
    const quantity = value => Number(value || 0).toLocaleString(undefined, { maximumFractionDigits: 4 });
    const money = value => Number(value).toLocaleString(undefined, { style: "currency", currency: "USD" });
    const price = item => item.fillPrice != null ? `Fill ${money(item.fillPrice)}` :
        item.limitPrice != null ? `Limit ${money(item.limitPrice)}` :
        item.stopPrice != null ? `Stop ${money(item.stopPrice)}` : "Market";
    const columns = [
        ["updated", "Updated", ""], ["symbol", "Symbol", ""], ["status", "Status", ""],
        ["requested", "Requested", "num"], ["filled", "Filled", "num"],
        [null, "Order", ""], [null, "Price", ""], [null, "Strategy", ""],
        [null, "Client order", ""], [null, "Actions", "visually-hidden"]
    ];

    function append(parent, tagName, text, className) {
        const element = document.createElement(tagName);
        if (text != null) element.textContent = text;
        if (className) element.className = className;
        parent.append(element);
        return element;
    }

    function connection(state, message) {
        if (!status) return;
        status.textContent = message;
        status.dataset.state = state;
        status.classList.toggle("status-banner", state === "stale");
        status.classList.toggle("error", state === "stale");
        status.classList.toggle("muted", state !== "stale");
    }

    function addHidden(form, name, value) {
        let input = form.querySelector(`input[name="${name}"]`);
        if (!input) {
            input = document.createElement("input");
            input.type = "hidden";
            input.name = name;
            form.append(input);
        }
        input.value = value ?? "";
    }

    function updateActions(cell, item) {
        cell.replaceChildren();
        if (!cancelTemplate || !isWorking(item.state) || item.state === "CancelPending" || !item.brokerOrderId) {
            const label = isWorking(item.state) ? "Working - no inline action" : "Terminal - no action";
            append(cell, "span", label, "note orders-terminal");
            return;
        }

        const form = cancelTemplate.cloneNode(true);
        addHidden(form, "brokerOrderId", item.brokerOrderId);
        const button = form.querySelector("button");
        if (button) {
            button.dataset.confirm = `Cancel the working ${item.symbol} order? The broker may still fill it before the cancel is acknowledged.`;
        }
        cell.append(form);

        const replace = append(cell, "a", "Replace", "btn btn-secondary btn-sm");
        const url = new URL("/OrderTicket", window.location.origin);
        url.searchParams.set("ticker", item.symbol);
        url.searchParams.set("side", String(item.side || "buy").toLowerCase());
        if (item.limitPrice != null) url.searchParams.set("limitPrice", item.limitPrice);
        const environment = new URLSearchParams(window.location.search).get("env");
        if (environment) url.searchParams.set("env", environment);
        replace.href = `${url.pathname}${url.search}`;
        replace.title = "Replace opens a fresh reviewed ticket; it does not amend this order in place.";
    }

    function updateRow(row, item) {
        const time = row.querySelector("[data-order-time]");
        const local = row.querySelector("[data-order-local-time]");
        const updated = new Date(item.updatedAtUtc);
        if (time && !Number.isNaN(updated.valueOf())) {
            time.dateTime = item.updatedAtUtc;
            time.textContent = `${updated.toISOString().slice(5, 19).replace("T", " ")} UTC`;
            if (local) local.textContent = `${updated.toLocaleString()} local`;
        }
        const state = row.querySelector("[data-order-state]");
        if (state) {
            state.textContent = String(item.state || "Unknown").replace(/([a-z])([A-Z])/g, "$1 $2");
            state.className = `order-state state-${String(item.state || "unknown").toLowerCase()}`;
        }
        const filled = row.querySelector("[data-order-filled]");
        if (filled) {
            const filledQuantity = Number(item.filledQuantity || 0);
            filled.textContent = quantity(filledQuantity);
            filled.className = "num";
            if (filledQuantity <= 0) filled.classList.add("unknown");
            else if (filledQuantity < Number(item.requestedQuantity || 0)) filled.classList.add("accent-note");
        }
        const orderPrice = row.querySelector("[data-order-price]");
        if (orderPrice) orderPrice.textContent = price(item);
        const actions = row.querySelector(".order-actions");
        if (actions) updateActions(actions, item);
    }

    function createRow(item) {
        const row = document.createElement("tr");
        row.dataset.clientOrderId = item.clientOrderId;
        const updated = append(row, "td");
        append(updated, "time", "").dataset.orderTime = "";
        append(updated, "span", "", "cell-sub").dataset.orderLocalTime = "";
        const symbol = append(row, "td");
        append(symbol, "strong", item.symbol);
        append(symbol, "span", String(item.side || "").toUpperCase(), "cell-sub");
        append(append(row, "td"), "span", "").dataset.orderState = "";
        append(row, "td", quantity(item.requestedQuantity), "num");
        append(row, "td", "", "num").dataset.orderFilled = "";
        append(row, "td", `${item.orderType} / ${item.timeInForce}`);
        append(row, "td", "").dataset.orderPrice = "";
        append(row, "td", item.strategyId || "-");
        append(row, "td", item.clientOrderId, "mono order-identity");
        append(row, "td", "", "order-actions");
        updateRow(row, item);
        return row;
    }

    function sortItems(items) {
        const params = new URLSearchParams(window.location.search);
        const key = params.get("sort");
        const direction = params.get("dir") === "desc" ? -1 : 1;
        if (!key) return items;
        const stateRank = new Map(workingStates.concat(["Filled", "Rejected", "Canceled", "Expired"])
            .map((state, index) => [state, index]));
        const value = item => key === "updated" ? new Date(item.updatedAtUtc).valueOf() :
            key === "symbol" ? item.symbol : key === "status" ? (stateRank.get(item.state) ?? 99) :
            key === "requested" ? Number(item.requestedQuantity || 0) : Number(item.filledQuantity || 0);
        return items.slice().sort((left, right) => {
            const a = value(left);
            const b = value(right);
            const compared = typeof a === "string" ? a.localeCompare(b) : a - b;
            return compared !== 0 ? compared * direction :
                new Date(right.updatedAtUtc).valueOf() - new Date(left.updatedAtUtc).valueOf();
        });
    }

    function removeEmptyState() {
        body.querySelector("[data-orders-empty]")?.remove();
        body.querySelector(".orders-empty-action")?.remove();
    }

    function ensureTable() {
        let tableBody = body.querySelector("[data-orders-body]");
        if (tableBody) return tableBody;
        removeEmptyState();
        const region = document.createElement("div");
        region.className = "table-scroll";
        region.setAttribute("role", "region");
        region.setAttribute("aria-label", "Order lifecycle table");
        region.tabIndex = 0;
        const table = append(region, "table", null, "grid-table orders-table");
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
        tableBody.dataset.ordersBody = "";
        body.insertBefore(region, body.querySelector(".orders-footnote"));
        return tableBody;
    }

    function renderEmptyState() {
        body.querySelector(".table-scroll")?.remove();
        if (body.querySelector("[data-orders-empty]")) return;
        const messages = {
            working: "No order is working right now.", filled: "No order has filled yet.",
            rejected: "No order has been rejected.", closed: "No order has been cancelled or has expired.",
            all: "The order journal is empty."
        };
        const empty = document.createElement("p");
        empty.className = "empty-state";
        empty.dataset.ordersEmpty = "";
        empty.textContent = messages[filter] || messages.all;
        body.insertBefore(empty, body.querySelector(".orders-footnote"));
    }

    function reconcile(items) {
        const visible = sortItems(items.filter(item => matchesFilter(item.state)));
        const active = document.activeElement;
        const windowScroll = [window.scrollX, window.scrollY];
        const existingRegion = body.querySelector(".table-scroll");
        const regionScroll = existingRegion ? [existingRegion.scrollLeft, existingRegion.scrollTop] : null;
        if (visible.length === 0) {
            renderEmptyState();
        } else {
            const tableBody = ensureTable();
            const rows = new Map([...tableBody.querySelectorAll("[data-client-order-id]")]
                .map(row => [row.dataset.clientOrderId, row]));
            const incoming = new Set(visible.map(item => item.clientOrderId));
            for (const [key, row] of rows) if (!incoming.has(key)) row.remove();
            for (const item of visible) {
                const row = rows.get(item.clientOrderId) ?? createRow(item);
                updateRow(row, item);
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

    function updateSummary(items) {
        const values = summary.querySelectorAll(".metric-value");
        if (values.length < 5) return;
        const counts = [
            items.filter(item => isWorking(item.state)).length,
            items.filter(item => item.state === "Filled").length,
            items.filter(item => item.state === "Rejected").length,
            items.filter(item => ["Canceled", "Expired"].includes(item.state)).length
        ];
        counts.forEach((count, index) => { values[index].textContent = count; });
        values[4].textContent = counts.reduce((total, count) => total + count, 0);
    }

    async function poll() {
        if (loading) return;
        loading = true;
        try {
            const response = await fetch("/api/v1/orders?limit=500", { headers: { Accept: "application/json" } });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const items = await response.json();
            updateSummary(items);
            reconcile(items);
            connection("live", `Order list checked at ${new Date().toLocaleTimeString()}.`);
        } catch (error) {
            connection("stale", `Order updates disconnected: ${error.message}. Persisted values remain visible and may be stale.`);
        } finally {
            loading = false;
        }
    }

    summary.addEventListener("tradingflow:refresh", poll);
    void poll();
    window.setInterval(poll, 10000);
})();
