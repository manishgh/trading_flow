(() => {
    const refreshButton = document.getElementById("earnings-refresh");
    const status = document.getElementById("earnings-status");
    const updated = document.getElementById("earnings-updated");
    const feed = document.getElementById("earnings-feed");
    const newsFeed = document.getElementById("earnings-news-feed");
    const newsCount = document.getElementById("earnings-news-count");
    const newsWindow = document.getElementById("earnings-news-window");
    const emptyTemplate = document.getElementById("earnings-empty-template");
    const sessionFilters = document.getElementById("earnings-session-filters");
    const capFilters = document.getElementById("earnings-cap-filters");
    const activeFilters = { session: "all", marketCap: "all" };
    let loading = false;

    function text(tag, value, className) {
        const element = document.createElement(tag);
        element.textContent = value;
        if (className) element.className = className;
        return element;
    }

    function formatNumber(value, digits = 2) {
        if (value === null || value === undefined) return "-";
        return Number(value).toLocaleString(undefined, {
            minimumFractionDigits: digits,
            maximumFractionDigits: digits
        });
    }

    function formatPrice(value) {
        return value === null || value === undefined ? "-" : `$${formatNumber(value)}`;
    }

    function formatMarketCap(value) {
        if (value === null || value === undefined) return "Cap unavailable";
        const millions = Number(value);
        return millions >= 1000
            ? `$${formatNumber(millions / 1000, millions >= 100000 ? 0 : 1)}B cap`
            : `$${formatNumber(millions, 0)}M cap`;
    }

    function formatPercent(value) {
        if (value === null || value === undefined) return "-";
        const number = Number(value);
        return `${number > 0 ? "+" : ""}${formatNumber(number)}%`;
    }

    function localDateTime(value, includeSeconds = false) {
        if (!value) return "-";
        return new Date(value).toLocaleString(undefined, {
            weekday: "short",
            month: "short",
            day: "numeric",
            hour: "2-digit",
            minute: "2-digit",
            ...(includeSeconds ? { second: "2-digit" } : {})
        });
    }

    function localDateKey(value) {
        const date = new Date(value);
        return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
    }

    function safeLink(url, label, className) {
        if (!url) return null;
        try {
            const parsed = new URL(url);
            if (parsed.protocol !== "https:" && parsed.protocol !== "http:") return null;
            const link = text("a", label, className);
            link.href = parsed.toString();
            link.target = "_blank";
            link.rel = "noopener noreferrer";
            return link;
        } catch {
            return null;
        }
    }

    function assessmentClass(item) {
        if (item.breakoutAssessment === "PossibleBreakout") return "positive";
        if (item.resultAssessment === "Negative") return "negative";
        if (item.resultAssessment === "Mixed" || item.breakoutAssessment === "BreakoutNotConfirmed") return "warning";
        return "neutral";
    }

    function metric(label, value, valueClass) {
        const container = document.createElement("div");
        container.className = "earnings-metric";
        container.append(text("span", label, "earnings-metric-label"));
        container.append(text("strong", value, valueClass));
        return container;
    }

    function outcomeClass(outcome) {
        if (outcome === "Beat") return "good";
        if (outcome === "Miss") return "bad";
        return "";
    }

    function resultComparison(title, result, emptyText) {
        const row = document.createElement("div");
        row.className = "earnings-result-row";
        row.append(text("strong", title, "earnings-result-title"));
        if (!result) {
            row.append(text("span", emptyText, "muted earnings-result-empty"));
            return row;
        }

        const values = document.createElement("div");
        values.className = "earnings-result-values";
        values.append(
            text("span", `Estimate ${result.epsEstimate == null ? "-" : formatNumber(result.epsEstimate)}`),
            text("span", `Actual ${result.epsActual == null ? "-" : formatNumber(result.epsActual)}`),
            text("span", `${result.epsOutcomeLabel}${result.epsSurprisePercent == null ? "" : ` ${formatPercent(result.epsSurprisePercent)}`}`, `earnings-outcome ${outcomeClass(result.epsOutcome)}`)
        );
        row.append(values);
        return row;
    }

    function renderEvent(item) {
        const card = document.createElement("article");
        card.className = `earnings-event ${assessmentClass(item)}`;

        const heading = document.createElement("header");
        heading.className = "earnings-event-header";
        const identity = document.createElement("div");
        identity.className = "earnings-event-identity";
        identity.append(text("h3", item.ticker));
        identity.append(text("span", item.companyName, "muted"));
        identity.append(text("span", formatMarketCap(item.marketCapMillions), "muted earnings-market-cap"));
        const badges = document.createElement("div");
        badges.className = "earnings-event-badges";
        badges.append(text("span", item.releaseWindowLabel, `status release-${item.releaseWindow.toLowerCase()}`));
        badges.append(text("span", item.assessmentLabel, `status ${assessmentClass(item)}`));
        heading.append(identity, badges);

        const schedule = document.createElement("div");
        schedule.className = "earnings-event-schedule";
        schedule.append(text("strong", localDateTime(item.scheduledAtUtc)));
        schedule.append(text("span", `${item.scheduledNewYorkText} | ${item.scheduledUtcText}`, "muted"));
        if (item.isScheduleEstimate) schedule.append(text("span", "Release time is estimated", "warn"));

        const moveClass = item.eventReturnPercent > 0 ? "good" : item.eventReturnPercent < 0 ? "bad" : "";
        const metrics = document.createElement("div");
        metrics.className = "earnings-event-metrics";
        metrics.append(
            metric("Pre-release", formatPrice(item.preReleaseReferenceClose)),
            metric("Latest", formatPrice(item.latestClose)),
            metric("Move", formatPercent(item.eventReturnPercent), moveClass),
            metric("Slot RVOL", item.slotRelativeVolume == null ? "-" : `${formatNumber(item.slotRelativeVolume)}x`)
        );

        const results = document.createElement("div");
        results.className = "earnings-result-comparison";
        results.append(
            resultComparison("This report", item, "Result pending"),
            resultComparison(
                item.previousEarnings ? `Previous | ${item.previousEarnings.reportDateExchange}` : "Previous report",
                item.previousEarnings,
                "Historical result is loading or unavailable")
        );

        const surprises = document.createElement("div");
        surprises.className = "earnings-surprises";
        surprises.append(
            text("span", `Revenue ${formatPercent(item.revenueSurprisePercent)}`),
            text("span", item.latestMarketSession || "Price pending", "muted")
        );

        const footer = document.createElement("footer");
        footer.className = "earnings-event-footer";
        const details = document.createElement("details");
        details.className = "earnings-event-details";
        details.append(text("summary", "View evidence"));
        const body = document.createElement("div");
        body.className = "earnings-event-detail-body";
        body.append(text("p", item.analysisReason));
        body.append(text("p", `EMA10/20 ${formatNumber(item.ema10)}/${formatNumber(item.ema20)} | MACD histogram ${formatNumber(item.macdHistogram, 4)}`, "muted"));
        if (item.latestCompletedBarAtUtc) {
            body.append(text("p", `Latest completed 5m bar: ${localDateTime(item.latestCompletedBarAtUtc, true)} (${item.latestMarketSession || "session unknown"})`, "muted"));
        }
        if (item.newsHeadline) {
            body.append(safeLink(item.newsUrl, item.newsHeadline, "news-inline-link") || text("p", item.newsHeadline));
            if (item.resultNewsPublishedAtUtc) {
                body.append(text("p", `Provider publication: ${localDateTime(item.resultNewsPublishedAtUtc, true)} | ${item.newsProvider || "unknown"}`, "muted"));
            }
        }
        const source = safeLink(item.sourceUrl, "Open source record", "news-inline-link");
        if (source) body.append(source);
        details.append(body);
        footer.append(details);

        if (item.breakoutAssessment === "PossibleBreakout" && item.latestClose) {
            const action = text("a", "Review paper order", "button buy compact-action");
            action.href = `/OrderTicket?ticker=${encodeURIComponent(item.ticker)}&limitPrice=${encodeURIComponent(item.latestClose)}`;
            footer.append(action);
        }

        card.append(heading, schedule, metrics, results, surprises, footer);
        return card;
    }

    function dayLabel(item, payload) {
        if (item.dayGroup === "today") return "Today";
        if (item.dayGroup === "nextBusinessDay") return `Next reporting day | ${payload.nextBusinessDateLabel}`;
        return new Date(item.scheduledAtUtc).toLocaleDateString(undefined, {
            weekday: "long",
            month: "long",
            day: "numeric"
        });
    }

    function renderEvents(items, payload) {
        feed.replaceChildren();
        if (!items.length) {
            feed.append(emptyTemplate.content.cloneNode(true));
            return;
        }

        const groups = new Map();
        items.forEach(item => {
            const key = localDateKey(item.scheduledAtUtc);
            if (!groups.has(key)) groups.set(key, []);
            groups.get(key).push(item);
        });

        groups.forEach(groupItems => {
            const section = document.createElement("section");
            section.className = "earnings-day-group";
            const header = document.createElement("header");
            header.className = "earnings-day-header";
            header.append(text("h2", dayLabel(groupItems[0], payload)));
            header.append(text("span", `${groupItems.length} event${groupItems.length === 1 ? "" : "s"}`, "muted"));
            const cards = document.createElement("div");
            cards.className = "earnings-event-list";
            groupItems.forEach(item => cards.append(renderEvent(item)));
            section.append(header, cards);
            feed.append(section);
        });
    }

    function renderNews(items) {
        newsFeed.replaceChildren();
        newsCount.textContent = String(items.length);
        if (!items.length) {
            newsFeed.append(emptyTemplate.content.cloneNode(true));
            return;
        }

        items.forEach(item => {
            const article = document.createElement("article");
            const sentiment = item.sentimentLabel === "Bullish" ? "positive" : item.sentimentLabel === "Bearish" ? "negative" : "neutral";
            article.className = `earnings-news-item ${sentiment}`;
            const meta = document.createElement("div");
            meta.className = "earnings-news-meta";
            meta.append(text("strong", item.tickers || "Market"));
            meta.append(text("time", localDateTime(item.publishedAtUtc, true)));
            const headline = safeLink(item.url, item.headline, "earnings-news-headline") || text("strong", item.headline);
            article.append(meta, headline);
            if (item.summary) article.append(text("p", item.summary));
            const footer = document.createElement("div");
            footer.className = "earnings-news-footer";
            footer.append(text("span", `${item.category} | ${item.sentimentLabel} ${formatNumber(item.sentimentScore)}`));
            footer.append(text("span", item.source || item.provider));
            article.append(footer);
            newsFeed.append(article);
        });
    }

    function updateFilterControls(container, options, activeValue) {
        const byValue = new Map((options || []).map(option => [option.value, option]));
        container.querySelectorAll("button[data-filter-value]").forEach(button => {
            const value = button.dataset.filterValue;
            const option = byValue.get(value);
            if (option) button.textContent = `${option.label} (${option.count})`;
            const selected = value === activeValue;
            button.classList.toggle("active", selected);
            button.setAttribute("aria-pressed", String(selected));
        });
    }

    function bindFilterGroup(container, key) {
        container.addEventListener("click", event => {
            const button = event.target.closest("button[data-filter-value]");
            if (!button || activeFilters[key] === button.dataset.filterValue) return;
            activeFilters[key] = button.dataset.filterValue;
            loadCalendar(false);
        });
    }

    async function readError(response, fallback) {
        try {
            const message = await response.text();
            return message || fallback;
        } catch {
            return fallback;
        }
    }

    async function loadCalendar(forceRefresh = false) {
        if (loading) return;
        loading = true;
        refreshButton.disabled = true;
        status.className = "";
        status.textContent = forceRefresh ? "Refreshing provider data and analysis..." : "Loading earnings calendar...";
        try {
            if (forceRefresh) {
                const refreshResponse = await fetch("/api/earnings/refresh", { method: "POST" });
                if (!refreshResponse.ok) throw new Error(await readError(refreshResponse, `Refresh failed (${refreshResponse.status}).`));
            }

            const query = new URLSearchParams({
                session: activeFilters.session,
                marketCap: activeFilters.marketCap
            });
            const response = await fetch(`/api/earnings/today-next-business-day?${query}`, {
                headers: { Accept: "application/json" },
                cache: "no-store"
            });
            if (!response.ok) throw new Error(await readError(response, `Calendar request failed (${response.status}).`));
            const payload = await response.json();
            activeFilters.session = payload.filters?.session || activeFilters.session;
            activeFilters.marketCap = payload.filters?.marketCap || activeFilters.marketCap;
            updateFilterControls(sessionFilters, payload.filters?.sessions, activeFilters.session);
            updateFilterControls(capFilters, payload.filters?.marketCaps, activeFilters.marketCap);
            renderEvents(payload.items || [], payload);
            renderNews(payload.news || []);
            newsWindow.textContent = `${payload.newsWindowLabel}. Times display in your device timezone; stored timestamps remain UTC.`;
            const cadence = payload.monitoringIntervalSeconds >= 60 && payload.monitoringIntervalSeconds % 60 === 0
                ? `${payload.monitoringIntervalSeconds / 60} min`
                : `${payload.monitoringIntervalSeconds} sec`;
            status.textContent = payload.monitoringActive
                ? `${payload.items.length} events | monitoring every ${cadence}`
                : `${payload.items.length} events | monitor offline`;
            updated.textContent = `Updated ${localDateTime(payload.generatedAtUtc, true)}`;
        } catch (error) {
            status.textContent = error instanceof Error ? error.message : "Unable to load earnings data.";
            status.className = "bad";
        } finally {
            loading = false;
            refreshButton.disabled = false;
        }
    }

    refreshButton.addEventListener("click", () => loadCalendar(true));
    bindFilterGroup(sessionFilters, "session");
    bindFilterGroup(capFilters, "marketCap");
    loadCalendar();
    window.setInterval(() => loadCalendar(false), 60000);
})();
