document.querySelectorAll("[data-confirm]").forEach(button => {
    button.addEventListener("click", event => {
        if (!window.confirm(button.dataset.confirm)) {
            event.preventDefault();
        }
    });
});

// Bulk selection. The action bar stays hidden until something is selected so an
// empty destructive control is never sitting on screen.
(() => {
    const form = document.querySelector("[data-bulk-form]");
    if (!form) return;

    const bar = form.querySelector("[data-bulk-bar]");
    const count = form.querySelector("[data-bulk-count]");
    const names = form.querySelector("[data-bulk-names]");
    const toggleAll = form.querySelector("[data-bulk-toggle-all]");
    const clear = form.querySelector("[data-bulk-clear]");
    const items = () => [...form.querySelectorAll("[data-bulk-item]")];

    const sync = () => {
        const selected = items().filter(item => item.checked);
        if (count) count.textContent = String(selected.length);
        if (names) {
            const tickers = selected.map(item => item.value);
            names.textContent = tickers.length > 6 ? "· " + tickers.slice(0, 6).join(", ") + " and " + (tickers.length - 6) + " more" : tickers.length ? "· " + tickers.join(", ") : "";
        }
        if (bar) bar.hidden = selected.length === 0;
        items().forEach(item => item.closest("tr")?.classList.toggle("is-selected", item.checked));
        if (toggleAll) {
            toggleAll.checked = selected.length > 0 && selected.length === items().length;
            toggleAll.indeterminate = selected.length > 0 && selected.length < items().length;
        }
    };

    items().forEach(item => item.addEventListener("change", sync));
    toggleAll?.addEventListener("change", () => {
        items().forEach(item => { item.checked = toggleAll.checked; });
        sync();
    });
    clear?.addEventListener("click", () => {
        items().forEach(item => { item.checked = false; });
        sync();
    });
    sync();
})();

// Names and notes save when the field is left, through the one shared edit form and without reloading the page.
(() => {
    const form = document.getElementById("editMembershipForm");
    const status = document.querySelector("[data-inline-status]");
    if (!form) return;
    const fields = [...document.querySelectorAll("[data-inline-name], [data-inline-notes]")];
    fields.forEach(field => { field.dataset.saved = field.value; });
    const say = text => { if (status) status.textContent = text; };

    async function save(field) {
        if (field.value === field.dataset.saved) return;
        const row = field.closest("tr");
        const ticker = row.dataset.ticker;
        form.elements.Ticker.value = ticker;
        form.elements.DisplayName.value = row.querySelector("[data-inline-name]").value;
        form.elements.Notes.value = row.querySelector("[data-inline-notes]").value;
        say("Saving " + ticker + "…");
        try {
            const response = await fetch(form.action, { method: "POST", body: new FormData(form), credentials: "same-origin" });
            if (!response.ok) throw new Error("The server answered " + response.status + ".");
            row.querySelectorAll("[data-inline-name], [data-inline-notes]").forEach(input => { input.dataset.saved = input.value; });
            say("Saved " + ticker + ".");
        } catch (error) {
            say("Could not save " + ticker + ": " + error.message + " Your text is still in the field; leave it again to retry.");
        }
    }

    fields.forEach(field => {
        field.addEventListener("change", () => save(field));
        field.addEventListener("keydown", event => {
            if (event.key === "Enter") { event.preventDefault(); field.blur(); }
            if (event.key === "Escape") { field.value = field.dataset.saved; field.blur(); }
        });
    });
})();
