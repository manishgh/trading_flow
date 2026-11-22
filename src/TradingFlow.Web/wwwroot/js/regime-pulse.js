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

    // Describes one benchmark in a few words so the whole strip fits one line. The words name the standard
    // 50-day rule this strip checks; a strategy with its own regime settings may differ, which the tooltip says.
    function benchmarkChip(b, checkedAt) {
        if (!b || !b.available) return chip(b ? b.symbol : "?", "— unavailable", "muted", (b && b.summaryLabel) || "No benchmark bars were available.");
        const close = b.lastClose != null ? Number(b.lastClose).toFixed(2) : "—";
        const label = b.symbol + " " + close;
        const detail = b.longGateOn ? "Close above a 50-day average that is rising: the standard rule allows new longs, not shorts."
            : b.shortGateOn ? "Close below a 50-day average that is falling: the standard rule allows new shorts, not longs."
            : "Close and 50-day average point different ways (for example above the average while it falls): the standard rule allows neither.";
        const title = [detail, "Each strategy applies its own regime settings.",
            b.lastSessionDate ? "Session " + b.lastSessionDate : "", checkedAt].filter(Boolean).join(" · ");
        if (b.longGateOn) return chip(label, "▲ 50-day: longs", "good", title);
        if (b.shortGateOn) return chip(label, "▼ 50-day: shorts", "bad", title);
        return chip(label, "◐ 50-day: neither", "warn", title);
    }

    // Describes one risk multiplier in two or three words: "full", "off" or "0.5×".
    function scaleWord(scale) {
        return scale >= 1 ? "full" : scale > 0 ? Number(scale).toFixed(2).replace(/0+$/, "").replace(/\.$/, "") + "×" : "off";
    }

    // Shows the previous session's VIX close and the size it allows for new longs and shorts in a few words; the
    // tooltip spells out the server's labels, the session and the observation time.
    function vixChip(v, checkedAt) {
        if (v && !v.available && v.latestPublishedClose != null) {
            // FRED has not posted the previous session's close yet: show the newest close it has, with its date.
            return chip("VIX " + Number(v.latestPublishedClose).toFixed(2), v.latestPublishedDate + " · newer close pending", "warn",
                ["FRED has not published the " + v.sessionDate + " close yet; the latest published close is " +
                    Number(v.latestPublishedClose).toFixed(2) + " on " + v.latestPublishedDate + ".",
                 "Strategies that size by VIX wait for the " + v.sessionDate + " close before new entries.", checkedAt].join(" · "));
        }
        if (!v || !v.available) return chip("VIX", "— unavailable", "muted", [v ? v.longLabel : "VIX unavailable", checkedAt].join(" · "));
        const tone = v.longRiskScale >= 1 ? "good" : v.longRiskScale > 0 ? "warn" : "bad";
        const glyph = v.longRiskScale >= 1 ? "✓" : v.longRiskScale > 0 ? "◐" : "✕";
        return chip("VIX " + Number(v.close).toFixed(2), glyph + " longs " + scaleWord(v.longRiskScale) + " · shorts " + scaleWord(v.shortRiskScale), tone,
            ["Size for new trades: " + v.longLabel + " · " + v.shortLabel, "Previous session close " + v.sessionDate + " from FRED VIXCLS", checkedAt].join(" · "));
    }

    // Lists the top six sector funds by symbol; the tooltip names each sector with its 12-1 month return and the session.
    function sectorChip(p, checkedAt) {
        const leaders = Array.isArray(p.topSectorLeaders) ? p.topSectorLeaders : [];
        if (!leaders.length) return chip("Top sectors", "— unavailable", "muted", [p.topSectorsUnavailableReason || "No sector ranking.", checkedAt].join(" · "));
        const lines = leaders.map(l => l.rank + ". " + l.symbol + " " + l.name + " " + (l.return12Minus1 >= 0 ? "+" : "−") +
            Math.abs(l.return12Minus1 * 100).toFixed(1) + "%");
        const title = "Sector funds ranked by 12-1 month return (price change from 12 months ago to 1 month ago) as of " +
            p.topSectorsAsOfDate + ":\n" + lines.join("\n") + "\n" + checkedAt + ". Context only; strategies apply their own rules.";
        return chip("Top sectors", leaders.map(l => l.symbol).join(" · "), "good", title);
    }

    // Renders the server-evaluated regime chips using text nodes only; the observation time goes into each
    // chip's tooltip rather than taking strip width.
    function render(p) {
        const checkedAt = "Checked " + new Date(p.observedAtUtc).toLocaleTimeString();
        strip.textContent = "";
        strip.append(benchmarkChip(p.spy, checkedAt), benchmarkChip(p.qqq, checkedAt), vixChip(p.vix, checkedAt), sectorChip(p, checkedAt));
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
