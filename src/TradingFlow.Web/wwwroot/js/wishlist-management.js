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
