/* Theme selection.
   Loaded synchronously in <head> so the stored scheme is applied before first
   paint. Without that, a dark-scheme operator gets a white flash on every
   navigation, which on a trading surface is both jarring and slow to read. */
(() => {
    const STORAGE_KEY = "tradingflow.theme";
    const SCHEMES = ["system", "dark", "light", "cvd"];

    function stored() {
        try {
            const value = window.localStorage.getItem(STORAGE_KEY);
            return SCHEMES.includes(value) ? value : "system";
        } catch {
            return "system";
        }
    }

    function apply(scheme) {
        if (scheme === "system") {
            document.documentElement.removeAttribute("data-theme");
        } else {
            document.documentElement.setAttribute("data-theme", scheme);
        }
    }

    apply(stored());

    window.TradingFlowTheme = {
        get: stored,
        set(scheme) {
            if (!SCHEMES.includes(scheme)) return;
            try {
                window.localStorage.setItem(STORAGE_KEY, scheme);
            } catch {
                /* Private-mode or storage-disabled: apply for this document only. */
            }
            apply(scheme);

            // Keep the control honest when the theme is changed by anything other
            // than the control itself.
            const select = document.getElementById("ThemeSelect");
            if (select && select.value !== scheme) {
                select.value = scheme;
            }
        }
    };

    document.addEventListener("DOMContentLoaded", () => {
        const select = document.getElementById("ThemeSelect");
        if (!select) return;
        select.value = stored();
        select.addEventListener("change", () => window.TradingFlowTheme.set(select.value));
    });
})();
