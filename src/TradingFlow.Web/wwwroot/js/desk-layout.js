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

    /* The ticket's derived figures, size presets and token expiry live in
       desk-ticket.js, shared with the Orders screen. */
})();
