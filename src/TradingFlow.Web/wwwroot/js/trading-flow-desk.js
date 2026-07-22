import { createNamedEventStream, formatAge, isSafeArticleUrl, normalizeTicker } from "./trading-flow-stream.js";

const root = document.querySelector("[data-desk-root]");
const wishlistId = root?.dataset.wishlistId;
const selectedTicker = normalizeTicker(root?.dataset.selectedTicker);
const connectionNode = document.getElementById("DeskQuoteConnection");
const freshnessNode = document.getElementById("DeskQuoteFreshness");
const announcementNode = document.getElementById("DeskConnectionAnnouncement");
const toastNode = document.getElementById("SignalToast");
const rowMap = buildRowMap();
let latestQuoteTimestamp = null;
let lastConnectionAnnouncement = "";
let lastSignalId = null;

function buildRowMap() {
    const map = new Map();
    document.querySelectorAll("[data-quote-row]").forEach(row => {
        const ticker = normalizeTicker(row.dataset.quoteRow);
        const rows = map.get(ticker) || [];
        rows.push(row);
        map.set(ticker, rows);
    });
    return map;
}

function setText(node, value) {
    if (node && node.textContent !== String(value ?? "")) {
        node.textContent = String(value ?? "");
    }
}

function setConnectionState(state, detail) {
    if (connectionNode) {
        connectionNode.dataset.state = state;
        connectionNode.className = state === "connected"
            ? "status-ok"
            : state === "reconnecting" || state === "partial"
                ? "status-warn"
                : "status-bad";
        setText(connectionNode, state);
        connectionNode.title = detail;
    }
    if (detail && detail !== lastConnectionAnnouncement) {
        setText(announcementNode, detail);
        lastConnectionAnnouncement = detail;
    }
}

function patchField(container, selector, value) {
    container.querySelectorAll(selector).forEach(node => setText(node, value || "--"));
}

function applyQuotes(quotes) {
    for (const quote of Array.isArray(quotes) ? quotes : []) {
        const ticker = normalizeTicker(quote.ticker);
        const rows = rowMap.get(ticker) || [];
        rows.forEach(row => {
            patchField(row, '[data-quote-field="bid"]', quote.bidText);
            patchField(row, '[data-quote-field="ask"]', quote.askText);
            patchField(row, '[data-quote-field="mid"]', quote.midText);
            patchField(row, "[data-quote-age]", formatAge(quote.timestamp));
        });
        if (ticker === selectedTicker) {
            patchField(document, '[data-selected-quote="bid"]', quote.bidText);
            patchField(document, '[data-selected-quote="ask"]', quote.askText);
            patchField(document, '[data-selected-quote="mid"]', quote.midText);
        }

        const parsed = Date.parse(quote.timestamp || "");
        if (Number.isFinite(parsed) && (latestQuoteTimestamp === null || parsed > latestQuoteTimestamp)) {
            latestQuoteTimestamp = parsed;
        }
    }
    refreshFreshness();
}

function applyActivity(payload) {
    const signals = Array.isArray(payload?.signals) ? payload.signals : [];
    const news = Array.isArray(payload?.news) ? payload.news : [];
    const signalByTicker = new Map();
    for (const signal of signals) {
        const ticker = normalizeTicker(signal.ticker);
        if (!signalByTicker.has(ticker)) {
            signalByTicker.set(ticker, signal);
        }
    }

    for (const [ticker, rows] of rowMap.entries()) {
        const signal = signalByTicker.get(ticker);
        rows.forEach(row => {
            row.querySelectorAll("[data-signal-badge]").forEach(node => {
                setText(node, signal ? "Eligible" : "Watching");
                node.classList.toggle("eligible", Boolean(signal));
                node.classList.toggle("watching", !signal);
            });
            if (signal) {
                patchField(row, "[data-signal-status]", signal.reason);
            }
        });
    }

    const selectedSignal = signalByTicker.get(selectedTicker);
    if (selectedSignal) {
        patchSelectedSignal(selectedSignal);
        notifySignal(selectedSignal);
    }

    const selectedNews = news.find(item => splitTickers(item.ticker).includes(selectedTicker));
    if (selectedNews) {
        patchSelectedNews(selectedNews);
    }
    patchWishlistNews(news);
}

function patchSelectedSignal(signal) {
    setText(document.querySelector("[data-selected-signal-title]"), signal.signalType || "Signal");
    setText(document.querySelector("[data-selected-signal-type]"), signal.signalType || "Signal");
    setText(document.querySelector("[data-selected-signal-summary]"), signal.reason || "No reason supplied.");
    setText(document.querySelector("[data-selected-signal-reason]"), signal.reason || "No reason supplied.");
    setText(document.querySelector("[data-selected-signal-time]"), signal.detectedAtText || signal.detectedAt || "Timestamp unavailable");
    document.querySelectorAll("[data-selected-signal-badge]").forEach(node => {
        setText(node, "Eligible");
        node.classList.add("eligible");
        node.classList.remove("watching");
    });
}

function patchSelectedNews(item) {
    setText(document.querySelector("[data-selected-news-title]"), item.headline || "Untitled article");
    setText(document.querySelector("[data-selected-news-summary]"), item.summary || "No summary supplied.");
    setText(document.querySelector("[data-selected-news-source]"), [item.provider, item.source, item.timestampText].filter(Boolean).join(" · "));
    const link = document.querySelector("[data-selected-news-link]");
    if (!link) {
        return;
    }
    const safe = isSafeArticleUrl(item.url);
    link.hidden = !safe;
    if (safe) {
        link.href = item.url;
        link.target = "_blank";
        link.rel = "noopener noreferrer";
    } else {
        link.removeAttribute("href");
    }
}

function patchWishlistNews(items) {
    const container = document.querySelector("[data-wishlist-news-list]");
    if (!container) {
        return;
    }

    const groupedItems = new Map();
    for (const item of items) {
        const key = newsKey(item);
        if (!key) {
            continue;
        }
        const existingItem = groupedItems.get(key);
        if (!existingItem) {
            groupedItems.set(key, { ...item });
            continue;
        }
        existingItem.ticker = Array.from(new Set([
            ...splitTickers(existingItem.ticker),
            ...splitTickers(item.ticker)
        ])).sort().join(", ");
        if (Date.parse(item.timestamp || "") > Date.parse(existingItem.timestamp || "")) {
            existingItem.timestamp = item.timestamp;
            existingItem.timestampText = item.timestampText;
        }
    }
    const uniqueItems = Array.from(groupedItems.values());
    uniqueItems.sort((left, right) => Date.parse(right.timestamp || "") - Date.parse(left.timestamp || ""));

    const existing = new Map(Array.from(container.querySelectorAll("[data-news-item]"))
        .map(node => [node.dataset.newsKey, node]));
    for (const item of uniqueItems.slice(0, 20)) {
        const key = newsKey(item);
        let article = existing.get(key);
        if (!article) {
            article = createNewsArticle(key);
            container.appendChild(article);
        }
        patchNewsArticle(article, item);
    }

    const desiredKeys = new Set(uniqueItems.slice(0, 20).map(newsKey));
    container.querySelectorAll("[data-news-item]").forEach(node => {
        if (!desiredKeys.has(node.dataset.newsKey)) {
            node.remove();
        }
    });

    Array.from(container.querySelectorAll("[data-news-item]"))
        .sort((left, right) => Date.parse(right.dataset.newsTimestamp || "") - Date.parse(left.dataset.newsTimestamp || ""))
        .forEach(node => container.appendChild(node));

    const count = container.querySelectorAll("[data-news-item]").length;
    setText(document.querySelector("[data-wishlist-news-count]"), count);
    const empty = document.querySelector("[data-wishlist-news-empty]");
    if (empty) {
        empty.hidden = count > 0;
    }
}

function createNewsArticle(key) {
    const article = document.createElement("article");
    article.dataset.newsItem = "";
    article.dataset.newsKey = key;

    const meta = document.createElement("div");
    meta.className = "news-timeline-meta";
    const tickers = document.createElement("strong");
    tickers.dataset.newsTickers = "";
    const time = document.createElement("time");
    time.dataset.newsTime = "";
    meta.append(tickers, time);

    const headline = document.createElement("strong");
    headline.dataset.newsHeadline = "";
    const summary = document.createElement("p");
    summary.dataset.newsSummary = "";
    const source = document.createElement("span");
    source.dataset.newsSource = "";
    const link = document.createElement("a");
    link.dataset.newsLink = "";
    link.textContent = "Open article";
    article.append(meta, headline, summary, source, link);
    return article;
}

function patchNewsArticle(article, item) {
    article.dataset.newsKey = newsKey(item);
    article.dataset.newsTimestamp = item.timestamp || "";
    article.dataset.newsUrl = isSafeArticleUrl(item.url) ? item.url : "";
    setText(article.querySelector("[data-news-tickers]"), item.ticker || "MARKET");
    setText(article.querySelector("[data-news-time]"), item.timestampText || item.timestamp || "Timestamp unavailable");
    setText(article.querySelector("[data-news-headline]"), item.headline || "Untitled article");
    setText(article.querySelector("[data-news-summary]"), item.summary || "");
    setText(article.querySelector("[data-news-source]"), [item.provider, item.source].filter(Boolean).join(" · "));

    const link = article.querySelector("[data-news-link]");
    const safe = isSafeArticleUrl(item.url);
    link.hidden = !safe;
    if (safe) {
        link.href = item.url;
        link.target = "_blank";
        link.rel = "noopener noreferrer";
    } else {
        link.removeAttribute("href");
    }
}

function newsKey(item) {
    const safeUrl = isSafeArticleUrl(item?.url) ? item.url.trim() : "";
    return safeUrl || `${item?.headline || ""}|${item?.timestamp || ""}`;
}

function splitTickers(value) {
    return String(value || "").split(",").map(normalizeTicker).filter(Boolean);
}

function notifySignal(signal) {
    if (!signal?.id || signal.id === lastSignalId) {
        return;
    }
    lastSignalId = signal.id;
    const message = `${signal.ticker}: ${signal.signalType}. ${signal.reason}`;
    setText(toastNode, message);
    toastNode?.classList.add("show");
    window.setTimeout(() => toastNode?.classList.remove("show"), 6000);
    if (Notification.permission === "granted" && document.hidden) {
        new Notification(`${signal.ticker} actionable signal`, { body: `${signal.signalType}: ${signal.reason}` });
    }
}

function refreshFreshness() {
    if (latestQuoteTimestamp === null) {
        return;
    }
    const ageMilliseconds = Math.max(0, Date.now() - latestQuoteTimestamp);
    const ageText = formatAge(new Date(latestQuoteTimestamp).toISOString());
    setText(freshnessNode, `${ageText} · live server stream`);
    if (ageMilliseconds > 120000) {
        setConnectionState("stale", `Quote stream is connected, but the newest quote is ${ageText}.`);
    }
}

document.getElementById("EnableDeskAlertsButton")?.addEventListener("click", async () => {
    if (!("Notification" in window)) {
        setText(toastNode, "This browser does not support notifications.");
        toastNode?.classList.add("show");
        return;
    }
    const permission = await Notification.requestPermission();
    setText(toastNode, permission === "granted" ? "Trade Desk alerts enabled." : "Browser alerts remain disabled.");
    toastNode?.classList.add("show");
    window.setTimeout(() => toastNode?.classList.remove("show"), 4000);
});

if (wishlistId) {
    createNamedEventStream(`/api/wishlists/${wishlistId}/quotes/stream`, "quotes", {
        onMessage: applyQuotes,
        onState: setConnectionState,
        onDecodeError: error => console.warn("Quote update rejected.", error)
    });
    createNamedEventStream(`/api/wishlists/${wishlistId}/activity/stream`, "activity", {
        onMessage: applyActivity,
        onState: (state, detail) => {
            if (state !== "connected") {
                setConnectionState(state, detail);
            }
        },
        onDecodeError: error => console.warn("Activity update rejected.", error)
    });
    window.setInterval(refreshFreshness, 5000);
}

window.TradingFlowDesk = { applyActivity, applyQuotes, setConnectionState };
