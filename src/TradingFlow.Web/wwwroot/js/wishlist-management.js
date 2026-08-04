const combo = document.getElementById("WishlistCombo");
const hiddenId = document.getElementById("WishlistId");
const options = Array.from(document.querySelectorAll("#WishlistOptions option"));

function synchronizeWishlistId() {
    const typed = String(combo?.value || "").trim().toLowerCase();
    const match = options.find(option => String(option.value || "").trim().toLowerCase() === typed);
    if (hiddenId) {
        hiddenId.value = match?.dataset.id || "";
    }
}

combo?.addEventListener("input", synchronizeWishlistId);
document.getElementById("wishlistOpenForm")?.addEventListener("submit", synchronizeWishlistId);
document.getElementById("CreateWishlistButton")?.addEventListener("click", () => {
    const name = String(combo?.value || "").trim();
    if (!name) {
        combo?.focus();
        return;
    }
    const field = document.getElementById("CreateWishlistName");
    if (field) {
        field.value = name;
    }
    document.getElementById("createWishlistForm")?.requestSubmit();
});

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
    const toggleAll = form.querySelector("[data-bulk-toggle-all]");
    const clear = form.querySelector("[data-bulk-clear]");
    const items = () => [...form.querySelectorAll("[data-bulk-item]")];

    const sync = () => {
        const selected = items().filter(item => item.checked);
        if (count) count.textContent = String(selected.length);
        if (bar) bar.hidden = selected.length === 0;
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
