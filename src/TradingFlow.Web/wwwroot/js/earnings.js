/* The earnings monitor.

   Everything rendered here comes from EarningsCalendarResponse. The three
   attention lanes are derivations over fields the API already returns plus the
   operator's open positions; nothing on this screen invents a value.

   The monitor runs server-side whether or not the page is open, so Refresh is a
   read and never a trigger. */
(() => {
    const refreshButton = document.getElementById("earnings-refresh");
    const status = document.getElementById("earnings-status");
    const updated = document.getElementById("earnings-updated");
    const nextDay = document.getElementById("earnings-next-day");
    const countLabel = document.getElementById("earnings-count");
    const feed = document.getElementById("earnings-feed");
    const newsFeed = document.getElementById("earnings-news-feed");
    const newsCount = document.getElementById("earnings-news-count");
    const newsCaption = document.getElementById("earnings-news-caption");
    const newsScopeClear = document.getElementById("earnings-news-scope-clear");
    const emptyTemplate = document.getElementById("earnings-empty-template");
    const lanesRoot = document.getElementById("earnings-lanes");
    const sessionFilters = document.getElementById("earnings-session-filters");
    const capFilters = document.getElementById("earnings-cap-filters");
    const assessmentFilters = document.getElementById("earnings-assessment-filters");
    const fromInput = document.getElementById("earnings-from");
    const toInput = document.getElementById("earnings-to");
    const rangeClear = document.getElementById("earnings-range-clear");
    const dateScope = document.getElementById("earnings-date-scope");
    const onlyHeldToggle = document.getElementById("earnings-only-held");
    const hasNewsToggle = document.getElementById("earnings-has-news");
    const minSurpriseInput = document.getElementById("earnings-min-surprise");
    const sortInput = document.getElementById("earnings-sort");

    const held = new Set((lanesRoot?.dataset.heldTickers || "")
        .split(",").map(value => value.trim().toUpperCase()).filter(Boolean));

    const serverFilters = { session: "all", marketCap: "all", dateScope: "today" };
    // The rest are applied here because the payload is already fully
    // materialised for the chosen date range.
    const clientFilters = { assessment: "all", onlyHeld: false, hasNews: false, minSurprise: null, sort: "time" };
    let activeLane = null;
    let newsScopeTicker = null;
    let latestPayload = null;
    let loading = false;

    /* --- formatting ------------------------------------------------------- */

    function text(tag, value, className) {
        const element = document.createElement(tag);
        element.textContent = value;
        if (className) element.className = className;
        return element;
    }

    function number(value, digits = 2) {
        if (value === null || value === undefined) return "—";
        return Number(value).toLocaleString(undefined, { minimumFractionDigits: digits, maximumFractionDigits: digits });
    }

    function price(value) {
        return value === null || value === undefined ? "—" : `$${number(value)}`;
    }

    // Direction is glyph, sign and colour together. U+2212 keeps figures aligned
    // in tabular numerals.
    function signedPercent(value) {
        if (value === null || value === undefined) return "—";
        const amount = Number(value);
        const sign = amount > 0 ? "+" : amount < 0 ? "−" : "";
        return `${sign}${number(Math.abs(amount))}%`;
    }

    function signedClass(value) {
        if (value === null || value === undefined) return "value-signed flat";
        const amount = Number(value);
        return amount > 0 ? "value-signed up" : amount < 0 ? "value-signed down" : "value-signed flat";
    }

    function marketCap(value) {
        if (value === null || value === undefined) return "cap unknown";
        const millions = Number(value);
        if (millions >= 1_000_000) return `$${number(millions / 1_000_000)}T cap`;
        return millions >= 1000 ? `$${number(millions / 1000, 1)}B cap` : `$${number(millions, 0)}M cap`;
    }

    function localDateTime(value, includeSeconds = false) {
        if (!value) return "—";
        return new Date(value).toLocaleString(undefined, {
            weekday: "short", month: "short", day: "numeric",
            hour: "2-digit", minute: "2-digit",
            ...(includeSeconds ? { second: "2-digit" } : {})
        });
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

    /* --- lanes ------------------------------------------------------------
       Recomputed on every render and on the one-second tick, because two of the
       three depend on wall clock. The tick never re-fetches. */

    function minutesUntil(value) {
        if (!value) return null;
        return (new Date(value).getTime() - Date.now()) / 60000;
    }

    function isBreakoutLane(item) {
        return String(item.epsOutcome || "").toLowerCase() === "beat" &&
            String(item.breakoutAssessment || "").toLowerCase().includes("confirm") &&
            !String(item.breakoutAssessment || "").toLowerCase().includes("not");
    }

    function isImminentLane(item) {
        if (item.epsActual !== null && item.epsActual !== undefined) return false;
        const minutes = minutesUntil(item.scheduledAtUtc);
        return minutes !== null && minutes > 0 && minutes <= 30;
    }

    function isHeldLane(item) {
        return held.has(String(item.ticker || "").toUpperCase());
    }

    const laneTests = { breakout: isBreakoutLane, imminent: isImminentLane, held: isHeldLane };

    function renderLanes(items) {
        for (const button of lanesRoot.querySelectorAll("[data-lane]")) {
            const lane = button.dataset.lane;
            const matches = items.filter(laneTests[lane]);
            button.querySelector("[data-lane-count]").textContent = String(matches.length);
            button.querySelector("[data-lane-tickers]").textContent =
                matches.length ? matches.map(item => item.ticker).join(" · ") : "None";
            button.classList.toggle("is-active", activeLane === lane);
            button.classList.toggle("is-live", matches.length > 0);
            button.setAttribute("aria-pressed", String(activeLane === lane));
        }
    }

    /* --- event card -------------------------------------------------------- */

    // The left rule states the server's own assessment. Awaiting and
    // insufficient-data are their own state, not a muted negative.
    function ruleClass(item) {
        switch (String(item.resultAssessment || "").toLowerCase()) {
            case "positive": return "is-positive";
            case "negative": return "is-negative";
            case "mixed": return "is-mixed";
            default: return "is-awaiting";
        }
    }

    function cellStrip(cells) {
        const strip = document.createElement("div");
        strip.className = "cell-strip earnings-strip";
        for (const [label, value, className] of cells) {
            const cell = document.createElement("div");
            cell.append(text("span", label, "cell-label"));
            cell.append(text("span", value, `cell-value ${className || ""}`.trim()));
            strip.append(cell);
        }
        return strip;
    }

    function badge(label, className) {
        return text("span", label, `tag ${className}`);
    }

    function renderEvent(item) {
        const card = document.createElement("article");
        card.className = `earnings-event ${ruleClass(item)}`;

        const header = document.createElement("header");
        header.className = "earnings-event-head";

        const identity = document.createElement("div");
        identity.className = "earnings-identity";
        const line = document.createElement("div");
        line.className = "earnings-identity-line";
        line.append(text("h3", item.ticker));
        line.append(text("span", item.companyName, "earnings-company"));
        line.append(text("span", marketCap(item.marketCapMillions), "earnings-cap"));
        identity.append(line);
        identity.append(text("div", [
            item.releaseWindowLabel,
            item.scheduledNewYorkText,
            item.scheduledUtcText,
            item.isScheduleEstimate ? "Provider-estimated slot" : "Confirmed slot"
        ].join("   ·   "), "earnings-meta"));

        const badges = document.createElement("div");
        badges.className = "earnings-badges";
        if (isHeldLane(item)) badges.append(badge("HELD", "tag-accent"));
        const minutes = minutesUntil(item.scheduledAtUtc);
        if (isImminentLane(item)) badges.append(badge(`IN ${Math.max(1, Math.round(minutes))} MIN`, "tag-accent"));
        badges.append(badge(item.releaseWindowLabel, "tag-neutral"));
        badges.append(badge(item.epsOutcomeLabel || "AWAITING",
            String(item.epsOutcome || "").toLowerCase() === "beat" ? "tag-accent" : "tag-neutral"));
        if (item.breakoutAssessment) {
            badges.append(badge(
                item.breakoutAssessment.replace(/([a-z])([A-Z])/g, "$1 $2").toUpperCase(),
                isBreakoutLane(item) ? "tag-accent" : "tag-outline"));
        }
        header.append(identity, badges);

        // Unreported actuals read "awaiting", never 0 and never a bare dash: the
        // two say different things and only one of them is true here.
        const awaiting = value => (value === null || value === undefined ? "awaiting" : null);
        const results = cellStrip([
            ["EPS est", number(item.epsEstimate)],
            ["EPS actual", awaiting(item.epsActual) || number(item.epsActual), awaiting(item.epsActual) ? "unknown" : null],
            ["Surprise", signedPercent(item.epsSurprisePercent), signedClass(item.epsSurprisePercent)],
            ["Rev est", number(item.revenueEstimateMillions, 0)],
            ["Rev actual", awaiting(item.revenueActualMillions) || number(item.revenueActualMillions, 0), awaiting(item.revenueActualMillions) ? "unknown" : null],
            ["Rev surprise", signedPercent(item.revenueSurprisePercent), signedClass(item.revenueSurprisePercent)]
        ]);

        const reaction = cellStrip([
            ["Pre-release high", price(item.preReleaseReferenceHigh)],
            ["Latest close", price(item.latestClose)],
            ["Event move", signedPercent(item.eventReturnPercent), signedClass(item.eventReturnPercent)],
            ["Slot RVOL", item.slotRelativeVolume == null ? "—" : `${number(item.slotRelativeVolume)}x`],
            ["EMA 10 / 20", `${number(item.ema10)} / ${number(item.ema20)}`],
            ["MACD hist", number(item.macdHistogram, 4)]
        ]);
        reaction.classList.add("is-butted");

        // The server's own explanation of its assessment. Rendered verbatim.
        const analysis = text("p", item.analysisReason, "earnings-analysis");

        card.append(header, results, reaction, analysis);

        if (isHeldLane(item)) {
            card.append(text("p",
                `You hold ${item.ticker}. Confirm the protection on this position predates the print.`,
                "callout earnings-held-note"));
        }

        card.append(renderProvenance(item));
        card.append(renderActions(item));
        return card;
    }

    function factList(pairs) {
        const list = document.createElement("dl");
        list.className = "fact-list";
        for (const [term, value] of pairs) {
            list.append(text("dt", term));
            list.append(text("dd", value));
        }
        return list;
    }

    function renderProvenance(item) {
        const details = document.createElement("details");
        details.className = "earnings-provenance";
        details.append(text("summary", "Provenance and previous quarter"));

        const body = document.createElement("div");
        body.className = "earnings-provenance-body";
        body.append(factList([
            ["Provider", `${item.provider} · reaction from Alpaca SIP completed bars`],
            ["Received", localDateTime(item.providerReceivedAtUtc, true)],
            ["Result first seen", item.resultFirstSeenAtUtc ? localDateTime(item.resultFirstSeenAtUtc, true) : "Not yet observable"],
            ["Latest completed bar", localDateTime(item.latestCompletedBarAtUtc, true)],
            ["Bar session", item.latestMarketSession || "session unknown"]
        ]));

        const previous = item.previousEarnings;
        body.append(previous
            ? factList([
                ["Prior quarter", String(previous.reportDateExchange)],
                ["Prior EPS", `${number(previous.epsActual)} vs ${number(previous.epsEstimate)} est`],
                ["Prior surprise", signedPercent(previous.epsSurprisePercent)],
                ["Prior 1-day move", signedPercent(previous.oneDayPriceReactionPercent)],
                ["Outcome then", previous.epsOutcomeLabel]
            ])
            : text("p", "No previous quarter is on file for this symbol.", "note"));

        details.append(body);
        return details;
    }

    function renderActions(item) {
        const actions = document.createElement("div");
        actions.className = "earnings-actions";

        const inspect = text("a", "Inspect on desk", "btn btn-secondary btn-md");
        inspect.href = `/TradeDesk?ticker=${encodeURIComponent(item.ticker)}`;
        actions.append(inspect);

        const evidence = text("button", "Show evidence", "btn btn-secondary btn-md");
        evidence.type = "button";
        evidence.addEventListener("click", () => {
            newsScopeTicker = item.ticker.toUpperCase();
            render();
        });
        actions.append(evidence);

        // No order control. This surface is advisory and cannot route.
        return actions;
    }

    /* --- timeline ---------------------------------------------------------- */

    function dayTitle(item, payload) {
        if (item.dayGroup === "today") {
            return `Today · ${new Date(item.scheduledAtUtc).toLocaleDateString(undefined, { weekday: "short", day: "2-digit", month: "short", year: "numeric" })}`;
        }
        if (item.dayGroup === "nextBusinessDay") {
            return `Next reporting day · ${payload.nextBusinessDateLabel}`;
        }
        return new Date(item.scheduledAtUtc).toLocaleDateString(undefined, { weekday: "long", month: "long", day: "numeric" });
    }

    function renderTimeline(items, payload, totalForGroup) {
        feed.replaceChildren();
        if (!items.length) {
            feed.append(emptyTemplate.content.cloneNode(true));
            return;
        }

        const groups = new Map();
        for (const item of items) {
            const key = item.dayGroup || new Date(item.scheduledAtUtc).toDateString();
            if (!groups.has(key)) groups.set(key, []);
            groups.get(key).push(item);
        }

        for (const [key, groupItems] of groups) {
            const section = document.createElement("section");
            section.className = "earnings-day";
            const header = document.createElement("header");
            header.className = "earnings-day-head";
            header.append(text("h2", dayTitle(groupItems[0], payload)));
            const reported = groupItems.filter(item => item.epsActual !== null && item.epsActual !== undefined).length;
            header.append(text("span",
                `${groupItems.length} of ${totalForGroup.get(key) ?? groupItems.length} events · ${reported} reported`,
                "earnings-day-summary"));
            section.append(header);
            for (const item of groupItems) section.append(renderEvent(item));
            feed.append(section);
        }
    }

    /* --- evidence rail ------------------------------------------------------ */

    function renderNews(items, visibleItems) {
        let scoped = items;
        if (newsScopeTicker) {
            scoped = items.filter(item => String(item.tickers || "").toUpperCase().includes(newsScopeTicker));
        } else if (visibleItems) {
            const visibleTickers = new Set(visibleItems.map(i => i.ticker.toUpperCase()));
            scoped = items.filter(item => {
                if (!item.tickers) return false;
                const newsTickers = String(item.tickers).toUpperCase().split(',').map(t => t.trim());
                return newsTickers.some(t => visibleTickers.has(t));
            });
        }

        newsFeed.replaceChildren();
        newsCount.textContent = String(scoped.length);
        newsScopeClear.hidden = !newsScopeTicker;
        if (!scoped.length) {
            newsFeed.append(emptyTemplate.content.cloneNode(true));
            return;
        }

        for (const item of scoped) {
            const article = document.createElement("article");
            const score = Number(item.sentimentScore);
            article.className = `news-item earnings-news-item ${score > 0.25 ? "is-positive" : score < -0.25 ? "is-negative" : ""}`.trim();

            const meta = document.createElement("div");
            meta.className = "news-meta";
            meta.append(text("strong", item.tickers || "Market"));
            meta.append(text("time", localDateTime(item.publishedAtUtc, true)));

            const headline = safeLink(item.url, item.headline, "news-headline") || text("span", item.headline, "news-headline");
            article.append(meta, headline);

            // WhyItMatters is the reason this rail is worth the space. Always rendered.
            if (item.whyItMatters) article.append(text("p", item.whyItMatters, "news-why"));

            const footer = document.createElement("div");
            footer.className = "news-meta";
            footer.append(text("span", `${item.provider}${item.category ? ` · ${item.category}` : ""}`));
            footer.append(text("span", `sentiment ${score >= 0 ? "+" : "−"}${number(Math.abs(score))} · ${item.sentimentLabel}`));
            article.append(footer);
            newsFeed.append(article);
        }
    }

    /* --- filtering --------------------------------------------------------- */

    function applyClientFilters(items) {
        let result = items.filter(item => {
            if (activeLane && !laneTests[activeLane](item)) return false;
            if (clientFilters.assessment !== "all") {
                const outcome = String(item.epsOutcome || "").toLowerCase();
                const matches = clientFilters.assessment === "pending"
                    ? outcome === "" || outcome === "pending" || outcome === "awaiting"
                    : outcome.includes(clientFilters.assessment);
                if (!matches) return false;
            }
            if (clientFilters.onlyHeld && !isHeldLane(item)) return false;
            if (clientFilters.hasNews && !item.newsHeadline) return false;
            if (clientFilters.minSurprise !== null) {
                const surprise = item.epsSurprisePercent;
                if (surprise === null || surprise === undefined) return false;
                if (Math.abs(Number(surprise)) < clientFilters.minSurprise) return false;
            }
            return true;
        });

        // Descending for magnitude-style sorts; ascending for the schedule, which
        // reads as a timeline.
        const numeric = key => item => {
            const value = item[key];
            return value === null || value === undefined ? Number.NEGATIVE_INFINITY : Number(value);
        };
        const bySurprise = item => {
            const value = item.epsSurprisePercent;
            return value === null || value === undefined ? Number.NEGATIVE_INFINITY : Math.abs(Number(value));
        };
        switch (clientFilters.sort) {
            case "cap": result = [...result].sort((a, b) => numeric("marketCapMillions")(b) - numeric("marketCapMillions")(a)); break;
            case "surprise": result = [...result].sort((a, b) => bySurprise(b) - bySurprise(a)); break;
            case "move": result = [...result].sort((a, b) => numeric("eventReturnPercent")(b) - numeric("eventReturnPercent")(a)); break;
            case "rvol": result = [...result].sort((a, b) => numeric("slotRelativeVolume")(b) - numeric("slotRelativeVolume")(a)); break;
            default: result = [...result].sort((a, b) => new Date(a.scheduledAtUtc) - new Date(b.scheduledAtUtc));
        }
        return result;
    }

    function render() {
        if (!latestPayload) return;
        const all = latestPayload.items || [];
        const filtered = applyClientFilters(all);

        // Client-side fade transition
        const bodyContainer = document.querySelector(".earnings-body");
        if (bodyContainer) bodyContainer.style.opacity = "0.3";
        const spinner = document.getElementById("earnings-spinner");
        if (spinner) spinner.style.display = "flex";

        setTimeout(() => {
            const totals = new Map();
            for (const item of all) {
                const key = item.dayGroup || new Date(item.scheduledAtUtc).toDateString();
                totals.set(key, (totals.get(key) ?? 0) + 1);
            }

            renderLanes(all);
            renderTimeline(filtered, latestPayload, totals);
            renderNews(latestPayload.news || [], filtered);
            countLabel.textContent = `${filtered.length} of ${all.length} events`;
            
            if (bodyContainer) bodyContainer.style.opacity = "1";
            if (spinner && !loading) spinner.style.display = "none";
        }, 150);
    }

    function markStrip(container, attribute, value) {
        for (const button of container.querySelectorAll(`button[${attribute}]`)) {
            const selected = button.getAttribute(attribute) === value;
            if (selected) {
                button.setAttribute("aria-current", "page");
            } else {
                button.removeAttribute("aria-current");
            }
        }
    }

    function updateCounts(container, options) {
        const byValue = new Map((options || []).map(option => [option.value, option]));
        for (const button of container.querySelectorAll("button[data-filter-value]")) {
            const option = byValue.get(button.dataset.filterValue);
            if (!option) continue;
            button.replaceChildren(
                document.createTextNode(option.label),
                text("span", String(option.count), "count"));
        }
    }

    /* --- loading ------------------------------------------------------------ */

    async function readError(response, fallback) {
        try {
            return (await response.text()) || fallback;
        } catch {
            return fallback;
        }
    }

    async function loadCalendar(forceRefresh = false) {
        if (loading) return;
        loading = true;
        const bodyContainer = document.querySelector(".earnings-body");
        if (bodyContainer) bodyContainer.classList.add("is-loading");
        const spinner = document.getElementById("earnings-spinner");
        if (spinner) spinner.style.display = "flex";
        refreshButton.disabled = true;
        status.classList.remove("accent-note");
        try {
            if (forceRefresh) {
                const refreshResponse = await fetch("/api/earnings/refresh", { method: "POST" });
                if (!refreshResponse.ok) throw new Error(await readError(refreshResponse, `Refresh failed (${refreshResponse.status}).`));
            }

            const query = new URLSearchParams({
                session: serverFilters.session,
                marketCap: serverFilters.marketCap,
                scope: serverFilters.dateScope
            });
            const from = fromInput?.value;
            const to = toInput?.value;
            const useRange = Boolean(from && to && from <= to);
            if (useRange) {
                query.set("from", from);
                query.set("to", to);
            }
            const endpoint = useRange ? "calendar" : "today-next-business-day";
            const response = await fetch(`/api/earnings/${endpoint}?${query}`, {
                headers: { Accept: "application/json" },
                cache: "no-store"
            });
            if (!response.ok) throw new Error(await readError(response, `Calendar request failed (${response.status}).`));

            latestPayload = await response.json();
            serverFilters.session = latestPayload.filters?.session || serverFilters.session;
            serverFilters.marketCap = latestPayload.filters?.marketCap || serverFilters.marketCap;
            updateCounts(sessionFilters, latestPayload.filters?.sessions);
            markStrip(sessionFilters, "data-filter-value", serverFilters.session);
            updateCounts(capFilters, latestPayload.filters?.marketCaps);
            markStrip(capFilters, "data-filter-value", serverFilters.marketCap);

            const seconds = latestPayload.monitoringIntervalSeconds;
            const cadence = seconds >= 60 && seconds % 60 === 0 ? `${seconds / 60} min` : `${seconds} s`;
            status.textContent = latestPayload.monitoringActive ? `Running · every ${cadence}` : "Offline";
            if (!latestPayload.monitoringActive) status.classList.add("accent-note");
            updated.textContent = latestPayload.lastAnalysisUtc ? localDateTime(latestPayload.lastAnalysisUtc, true) : "never";
            newsCaption.textContent = `${latestPayload.newsWindowLabel}. Times display in your device timezone; stored timestamps remain UTC.`;
            document.getElementById("earnings-news-window").textContent = latestPayload.newsWindowLabel;
            nextDay.textContent = latestPayload.nextBusinessDateLabel;

            render();
        } catch (error) {
            status.textContent = error instanceof Error ? error.message : "Unable to load earnings data.";
            status.classList.add("accent-note");
        } finally {
            loading = false;
            const bodyContainer = document.querySelector(".earnings-body");
            if (bodyContainer) bodyContainer.classList.remove("is-loading");
            refreshButton.disabled = false;
            const spinner = document.getElementById("earnings-spinner");
            if (spinner) spinner.style.display = "none";
        }
    }

    /* --- wiring ------------------------------------------------------------- */

    refreshButton.addEventListener("click", () => loadCalendar(true));

    lanesRoot.addEventListener("click", event => {
        const button = event.target.closest("[data-lane]");
        if (!button) return;
        // A lane intersects with the other filters rather than replacing them.
        activeLane = activeLane === button.dataset.lane ? null : button.dataset.lane;
        render();
    });

    sessionFilters.addEventListener("click", event => {
        const button = event.target.closest("button[data-filter-value]");
        if (!button || serverFilters.session === button.dataset.filterValue) return;
        serverFilters.session = button.dataset.filterValue;
        markStrip(sessionFilters, "data-filter-value", serverFilters.session);
        loadCalendar(false);
    });

    capFilters?.addEventListener("click", event => {
        const button = event.target.closest("button[data-filter-value]");
        if (!button || serverFilters.marketCap === button.dataset.filterValue) return;
        serverFilters.marketCap = button.dataset.filterValue;
        markStrip(capFilters, "data-filter-value", serverFilters.marketCap);
        loadCalendar(false);
    });

    assessmentFilters.addEventListener("click", event => {
        const button = event.target.closest("button[data-assessment-value]");
        if (!button) return;
        clientFilters.assessment = button.dataset.assessmentValue;
        markStrip(assessmentFilters, "data-assessment-value", clientFilters.assessment);
        render();
    });

    dateScope.addEventListener("click", event => {
        const button = event.target.closest("button[data-scope-value]");
        if (!button) return;
        serverFilters.dateScope = button.dataset.scopeValue;
        if (fromInput) fromInput.value = "";
        if (toInput) toInput.value = "";
        markStrip(dateScope, "data-scope-value", serverFilters.dateScope);
        loadCalendar(false);
    });

    function bindToggle(button, key) {
        button?.addEventListener("click", () => {
            clientFilters[key] = !clientFilters[key];
            button.setAttribute("aria-pressed", String(clientFilters[key]));
            render();
        });
    }
    bindToggle(onlyHeldToggle, "onlyHeld");
    bindToggle(hasNewsToggle, "hasNews");

    minSurpriseInput?.addEventListener("change", () => {
        const value = Number(minSurpriseInput.value);
        clientFilters.minSurprise = minSurpriseInput.value === "" || Number.isNaN(value) ? null : value;
        render();
    });
    sortInput?.addEventListener("change", () => {
        clientFilters.sort = sortInput.value;
        render();
    });
    const rangeApply = document.getElementById("earnings-range-apply");
    rangeApply?.addEventListener("click", () => {
        if (fromInput?.value && toInput?.value) {
            markStrip(dateScope, "data-scope-value", ""); // Clear preset tab highlighting
            loadCalendar(false);
        }
    });
    rangeClear?.addEventListener("click", () => {
        if (fromInput) fromInput.value = "";
        if (toInput) toInput.value = "";
        serverFilters.dateScope = "today";
        markStrip(dateScope, "data-scope-value", "today");
        loadCalendar(false);
    });
    newsScopeClear?.addEventListener("click", () => {
        newsScopeTicker = null;
        render();
    });

    /* The countdown badge and the imminent lane both depend on wall clock, so
       they are recomputed on a one-second tick rather than re-fetched. Only
       those two things are touched: a full re-render every second would close an
       open provenance disclosure and take focus out of the rail. */
    function tick() {
        if (!latestPayload) return;
        renderLanes(latestPayload.items || []);
        for (const badgeNode of feed.querySelectorAll("[data-countdown-for]")) {
            const minutes = minutesUntil(badgeNode.dataset.countdownFor);
            badgeNode.textContent = minutes !== null && minutes > 0
                ? `IN ${Math.max(1, Math.round(minutes))} MIN`
                : "REPORTING NOW";
        }
    }

    loadCalendar();
    window.setInterval(tick, 1000);
    window.setInterval(() => loadCalendar(false), 10000);
})();
