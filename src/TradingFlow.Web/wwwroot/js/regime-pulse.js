/* Global market regime strip and Ask/Jump button.
   Presentation only: it renders GET /api/v1/workspace/regime-pulse (computed by the
   shared C# regime evaluators) and never recalculates a gate in the browser.
   Direction is carried by glyphs and words as well as colour. */
(() => {
    const strip = document.querySelector("[data-regime-pulse]");
    const ask = document.getElementById("OpenCommandPalette");
    if (ask) {
        ask.addEventListener("click", () => window.TradingFlowPalette && window.TradingFlowPalette.open());
    }
    if (!strip || typeof window.tradingFlowFetch !== "function") return;

    const REFRESH_MS = 60000;
    let controller = null;
    window.addEventListener("pagehide", () => controller && controller.abort());

    // Builds one chip: label, value text and a tone (good / bad / warn / muted).
    function chip(label, value, tone, title) {
        const el = document.createElement("span");
        el.className = "regime-chip";
        el.dataset.tone = tone;
        if (title) el.title = title;
        const l = document.createElement("span");
        l.className = "regime-chip__label";
        l.textContent = label;
        const v = document.createElement("strong");
        v.className = "regime-chip__value";
        v.textContent = value;
        el.append(l, v);
        return el;
    }

    // Describes one benchmark with explicit close price, 50-day context, and gate state so colour is never the only cue.
    function benchmarkChip(b) {
        if (!b || !b.available) return chip(b ? b.symbol : "?", "— unavailable", "muted", (b && b.summaryLabel) || "No benchmark bars were available.");
        const close = b.lastClose != null ? Number(b.lastClose).toFixed(2) : "—";
        const label = b.symbol + " " + close;
        const title = [b.summaryLabel, b.lastSessionDate ? "as of " + b.lastSessionDate : ""].filter(Boolean).join(" · ");
        if (b.longGateOn) return chip(label, "▲ rising 50-day · longs on", "good", title);
        if (b.shortGateOn) return chip(label, "▼ below 50-day · longs off · shorts on", "bad", title);
        return chip(label, "◐ 50-day flat · longs off", "warn", title);
    }

    // Shows the VIX close and what it does to long and short risk size.
    function vixChip(v) {
        if (!v || !v.available) return chip("VIX", "— pending", "muted", v ? v.longLabel : "VIX unavailable");
        const tone = v.longRiskScale >= 1 ? "good" : v.longRiskScale > 0 ? "warn" : "bad";
        const glyph = v.longRiskScale >= 1 ? "✓" : v.longRiskScale > 0 ? "◐" : "✕";
        return chip("VIX " + Number(v.close).toFixed(2), glyph + " " + v.longLabel + " · " + v.shortLabel, tone,
            "Previous session close " + v.sessionDate + " from FRED VIXCLS.");
    }

    // Renders the server-evaluated regime chips and observation timestamp using text nodes only.
    function render(p) {
        strip.textContent = "";
        strip.append(benchmarkChip(p.spy), benchmarkChip(p.qqq), vixChip(p.vix));
        const sectors = p.topSectors && p.topSectors.length ? p.topSectors.join(" · ") : "— not published";
        strip.append(chip("Sectors", sectors, p.topSectors && p.topSectors.length ? "good" : "muted",
            p.topSectorsAsOfDate ? "Top sectors ranked as of " + p.topSectorsAsOfDate : "No sector screen published."));
        const stamp = document.createElement("span");
        stamp.className = "regime-pulse-strip__stamp";
        stamp.textContent = "Checked " + new Date(p.observedAtUtc).toLocaleTimeString();
        strip.append(stamp);
    }

    // Fetches the cached server pulse while aborting any superseded in-flight request.
    async function refresh() {
        if (document.hidden) return;
        if (controller) controller.abort();
        controller = new AbortController();
        try {
            const response = await window.tradingFlowFetch("/api/v1/workspace/regime-pulse", {
                credentials: "same-origin", cache: "no-store", signal: controller.signal
            });
            if (!response.ok) throw new Error("HTTP " + response.status);
            render(await response.json());
        } catch (error) {
            if (error && error.name === "AbortError") return;
            strip.textContent = "";
            strip.append(chip("Market regime", "unavailable — retrying", "muted", String(error && error.message)));
        }
    }

    refresh();
    window.setInterval(refresh, REFRESH_MS);
    document.addEventListener("visibilitychange", () => { if (!document.hidden) refresh(); });
})();
