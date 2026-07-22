export function normalizeTicker(value) {
    return String(value || "").trim().toUpperCase();
}

export function formatAge(timestamp, now = Date.now()) {
    const parsed = Date.parse(timestamp || "");
    if (!Number.isFinite(parsed)) {
        return "No quote timestamp";
    }

    const ageSeconds = Math.max(0, Math.floor((now - parsed) / 1000));
    return ageSeconds < 60 ? `${ageSeconds}s old` : `${Math.floor(ageSeconds / 60)}m old`;
}

export function isSafeArticleUrl(value) {
    try {
        const url = new URL(value);
        return url.protocol === "https:" || url.protocol === "http:";
    } catch {
        return false;
    }
}

export function createNamedEventStream(url, eventName, handlers) {
    const source = new EventSource(url);
    handlers.onState?.("connecting", "Connecting to the live server feed.");

    source.addEventListener("open", () => handlers.onState?.("connected", "Live server feed connected."));
    source.addEventListener(eventName, event => {
        try {
            handlers.onMessage?.(JSON.parse(event.data));
            handlers.onState?.("connected", "Live server feed connected.");
        } catch (error) {
            handlers.onState?.("partial", "A live update could not be decoded; existing values were preserved.");
            handlers.onDecodeError?.(error);
        }
    });
    source.addEventListener("error", () => {
        const state = source.readyState === EventSource.CLOSED ? "disconnected" : "reconnecting";
        const detail = state === "disconnected"
            ? "Live server feed disconnected. Values remain visible but may be stale."
            : "Live server feed interrupted; reconnecting automatically.";
        handlers.onState?.(state, detail);
    });

    return source;
}
