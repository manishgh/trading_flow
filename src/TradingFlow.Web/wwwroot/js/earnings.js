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
    let loadSequence = 0, inFlight = null;
    const advanced = document.getElementById('earnings-advanced');
    const initial = new URLSearchParams(location.search);
    for (const key of ['session','marketCap']) if (initial.has(key)) serverFilters[key] = initial.get(key);
    if (initial.has('scope')) serverFilters.dateScope = initial.get('scope');
    for (const key of ['assessment','sort']) if (initial.has(key)) clientFilters[key] = initial.get(key);
    for (const key of ['onlyHeld','hasNews']) clientFilters[key] = initial.get(key) === 'true';
    if (initial.has('minSurprise')) clientFilters.minSurprise = Number(initial.get('minSurprise'));
    fromInput.value = initial.get('from') || ''; toInput.value = initial.get('to') || '';
    if (minSurpriseInput) minSurpriseInput.value = initial.get('minSurprise') || '';
    if (sortInput) sortInput.value = clientFilters.sort;
    const searchInput = document.getElementById('earnings-search');
    const sectorInput = document.getElementById('earnings-sector');
    // Sector choices arrive with each response; keep the requested one until its option exists.
    let requestedSector = initial.get('sector') || '';
    for (const input of advanced.elements) if (input.name && initial.has(input.name)) {
        if (input.type === 'checkbox') input.checked = initial.get(input.name) === 'true'; else input.value = initial.get(input.name);
    }
    onlyHeldToggle?.setAttribute('aria-pressed', String(clientFilters.onlyHeld));
    hasNewsToggle?.setAttribute('aria-pressed', String(clientFilters.hasNews));
    markStrip(assessmentFilters, 'data-assessment-value', clientFilters.assessment);

    function queryState() {
        const query = new URLSearchParams({ session: serverFilters.session, marketCap: serverFilters.marketCap, scope: serverFilters.dateScope });
        for (const [key,value] of Object.entries(clientFilters)) if (value !== null) query.set(key, String(value));
        for (const [key,value] of new FormData(advanced)) if (value !== '') query.set(key, value);
        if (requestedSector) query.set('sector', requestedSector);
        if (fromInput.value) query.set('from', fromInput.value);
        if (toInput.value) query.set('to', toInput.value);
        const visibleQuery = new URLSearchParams(query);
        for (const [key,value] of Object.entries({session:'all',marketCap:'all',scope:'today',assessment:'all',onlyHeld:'false',hasNews:'false',sort:'time',view:'timeline'}))
            if (visibleQuery.get(key) === value) visibleQuery.delete(key);
        const url = new URL(location.href); url.search = visibleQuery.toString(); history.replaceState(null, '', url);
        return query;
    }
    function exchangeDate(value) {
        const parts = new Intl.DateTimeFormat('en-US', { timeZone:'America/New_York', year:'numeric', month:'2-digit', day:'2-digit' }).formatToParts(new Date(value));
        const part = name => parts.find(x => x.type === name).value;
        return `${part('year')}-${part('month')}-${part('day')}`;
    }
    function renderCalendar(items) {
        feed.classList.add('earnings-week');
        const start = fromInput.value || exchangeDate(latestPayload.generatedAtUtc);
        const finish = toInput.value || start;
        const grouped = new Map();
        for (const item of items) {
            const key = exchangeDate(item.scheduledAtUtc);
            if (!grouped.has(key)) grouped.set(key, []);
            grouped.get(key).push(item);
        }
        // Include boundary dates after timezone conversion, even when the API used an operator date window.
        const keys = new Set(grouped.keys());
        for (let day = new Date(`${start}T12:00:00Z`), end = new Date(`${finish}T12:00:00Z`); day <= end; day.setUTCDate(day.getUTCDate()+1)) keys.add(day.toISOString().slice(0,10));
        const used = [];
        syncChildren(feed, [...keys].sort().map(date => {
            const day = daySection('w:' + date, `${date} · New York`, ''); used.push('w:' + date);
            const matches = grouped.get(date) || [];
            day.none ??= text('p', 'No matching events', 'muted');
            syncChildren(day.section, [day.header, ...(matches.length ? matches.map(eventCard) : [day.none])]);
            return day.section;
        }));
        return used;
    }

    /* --- in-place updates --------------------------------------------------
       Every refresh, filter change and ten-second poll patches the existing
       list: unchanged cards stay in the page, so open disclosures, focus,
       selection and scroll position survive, and the list never goes dim. */

    function syncChildren(parent, nodes) {
        nodes.forEach((node, index) => { if (parent.childNodes[index] !== node) parent.insertBefore(node, parent.childNodes[index] ?? null); });
        while (parent.childNodes.length > nodes.length) parent.lastChild.remove();
    }

    const emptyNotes = new Map();
    function emptyNote(container) {
        if (!emptyNotes.has(container)) emptyNotes.set(container, emptyTemplate.content.firstElementChild.cloneNode(true));
        return emptyNotes.get(container);
    }

    const daySections = new Map();
    function daySection(key, title, summary) {
        let day = daySections.get(key);
        if (!day) {
            const section = text('section', undefined, 'earnings-day');
            const header = text('header', undefined, 'earnings-day-head');
            day = { section, header, heading: text('h2'), note: text('span', undefined, 'earnings-day-summary') };
            header.append(day.heading, day.note); section.append(header);
            daySections.set(key, day);
        }
        if (day.heading.textContent !== title) day.heading.textContent = title;
        if (day.note.textContent !== summary) day.note.textContent = summary;
        day.note.hidden = !summary;
        return day;
    }

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

    const eventCache = new Map();
    function eventCard(item) {
        const signature = JSON.stringify(item), old = eventCache.get(item.id);
        if (old?.signature === signature) return old.card;
        const card = renderEvent(item);
        if (old) [...card.querySelectorAll('details')].forEach((detail,index) => { detail.open = old.card.querySelectorAll('details')[index]?.open || false; });
        eventCache.set(item.id, { signature, card });
        return card;
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
        if (isImminentLane(item)) {
            const countdown = badge(`IN ${Math.max(1, Math.round(minutes))} MIN`, "tag-accent");
            countdown.dataset.countdownFor = item.scheduledAtUtc;
            badges.append(countdown);
        }
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
        inspect.href = `/Desk?search=${encodeURIComponent(item.ticker)}`;
        actions.append(inspect);
        if (document.getElementById('groupActionDialog')) {
            const add = text('button', 'Add to group', 'btn btn-secondary btn-sm');
            add.type = 'button'; add.dataset.groupAction = 'add'; add.dataset.tickers = item.ticker;
            actions.append(add);
            const order = text('a', 'Review trade setup', 'btn btn-secondary btn-sm');
            order.href = `/Desk?search=${encodeURIComponent(item.ticker)}&order=buy`;
            actions.append(order);
        }

        const evidence = text("button", "Show evidence", "btn btn-secondary btn-md");
        evidence.type = "button";
        evidence.addEventListener("click", () => {
            newsScopeTicker = item.ticker.toUpperCase();
            render();
        });
        actions.append(evidence);

        // The review link navigates to a separate authenticated ticket; this calendar cannot route.
        return actions;
    }

    /* --- timeline ---------------------------------------------------------- */

    // Today's group also holds the previous session's after-close reports, which trade today,
    // so its heading takes today's New York date from the response rather than from a card.
    function dayKey(item) {
        return item.dayGroup === 'today' || item.dayGroup === 'nextBusinessDay' ? item.dayGroup : exchangeDate(item.scheduledAtUtc);
    }

    function exchangeDateLabel(date, options) {
        return new Date(`${date}T12:00:00Z`).toLocaleDateString(undefined, { timeZone: 'UTC', ...options });
    }

    function dayTitle(key, payload) {
        if (key === "today") return `Today · ${exchangeDateLabel(exchangeDate(payload.generatedAtUtc), { weekday: "short", day: "2-digit", month: "short", year: "numeric" })}`;
        if (key === "nextBusinessDay") return `Next reporting day · ${payload.nextBusinessDateLabel}`;
        return exchangeDateLabel(key, { weekday: "long", month: "long", day: "numeric" });
    }

    function renderTimeline(items, payload, totalForGroup) {
        feed.classList.remove('earnings-week');
        if (!items.length) { syncChildren(feed, [emptyNote(feed)]); return []; }

        const groups = new Map();
        for (const item of items) {
            const key = dayKey(item);
            if (!groups.has(key)) groups.set(key, []);
            groups.get(key).push(item);
        }

        const today = exchangeDate(payload.generatedAtUtc), used = [];
        syncChildren(feed, [...groups].map(([key, groupItems]) => {
            const reported = groupItems.filter(item => item.epsActual !== null && item.epsActual !== undefined).length;
            const carried = key === 'today' ? groupItems.filter(item => exchangeDate(item.scheduledAtUtc) < today).length : 0;
            const summary = `${groupItems.length} of ${totalForGroup.get(key) ?? groupItems.length} events · ${reported} reported`
                + (carried ? ` · ${carried} reported after the previous close` : '');
            const day = daySection('t:' + key, dayTitle(key, payload), summary); used.push('t:' + key);
            syncChildren(day.section, [day.header, ...groupItems.map(eventCard)]);
            return day.section;
        }));
        return used;
    }

    /* --- evidence rail ------------------------------------------------------ */

    function renderNews(items, visibleItems) {
        let scoped = items;
        if (newsScopeTicker) {
            scoped = items.filter(item => window.TradingFlowNews.symbols(item.tickers).includes(newsScopeTicker.toUpperCase()));
        } else if (visibleItems) {
            const visibleTickers = new Set(visibleItems.map(i => i.ticker.toUpperCase()));
            scoped = items.filter(item => {
                if (!item.tickers) return false;
                const newsTickers = String(item.tickers).toUpperCase().split(',').map(t => t.trim());
                return newsTickers.some(t => visibleTickers.has(t));
            });
        }

        newsCount.textContent = String(scoped.length);
        newsScopeClear.hidden = !newsScopeTicker;
        if (!scoped.length) { syncChildren(newsFeed, [emptyNote(newsFeed)]); return; }

        // A story that arrives after the page first loaded flashes and says NEW for a minute; changing filters never re-flashes old ones.
        const firstLoad = seenNews === null, wanted = new Set();
        seenNews ??= new Set();
        const articles = scoped.map(item => {
            const key = newsKey(item), signature = JSON.stringify(item);
            wanted.add(key);
            let entry = newsCards.get(key);
            if (entry?.signature !== signature) {
                const article = window.TradingFlowNews.create(item);
                article.classList.add('earnings-news-item');
                if (!firstLoad && !seenNews.has(key)) {
                    const marker = text('span', 'NEW', 'earnings-news-new');
                    article.classList.add('earnings-news-flash');
                    article.prepend(marker);
                    setTimeout(() => { marker.remove(); article.classList.remove('earnings-news-flash'); }, 60000);
                }
                entry = { signature, article };
                newsCards.set(key, entry);
            }
            return entry.article;
        });
        for (const item of items) seenNews.add(newsKey(item));
        for (const key of newsCards.keys()) if (!wanted.has(key)) newsCards.delete(key);
        syncChildren(newsFeed, articles);
    }
    const newsCards = new Map();
    let seenNews = null;
    function newsKey(item) { return (item.headline || '') + '|' + item.publishedAtUtc; }

    /* --- filtering --------------------------------------------------------- */

    function applyClientFilters(items) {
        let result = items.filter(item => !activeLane || laneTests[activeLane](item));

        return result;
    }

    function render() {
        if (!latestPayload) return;
        const all = latestPayload.items || [];
        const filtered = applyClientFilters(all);
        const focused = feed.contains(document.activeElement) ? document.activeElement : null;

        const totals = new Map();
        for (const item of all) totals.set(dayKey(item), (totals.get(dayKey(item)) ?? 0) + 1);

        renderLanes(all);
        const used = new Set(advanced.elements.view.value === 'week' ? renderCalendar(filtered) : renderTimeline(filtered, latestPayload, totals));
        for (const key of daySections.keys()) if (!used.has(key)) daySections.delete(key);
        if (focused?.isConnected && document.activeElement !== focused) focused.focus({ preventScroll: true });
        const visible = new Set(filtered.map(item => item.id));
        for (const id of eventCache.keys()) if (!visible.has(id)) eventCache.delete(id);
        renderNews(latestPayload.news || [], filtered);
        countLabel.textContent = `${filtered.length} of ${latestPayload.totalMatching ?? all.length} matching events${latestPayload.truncated ? " · first 2000 shown" : ""}`;
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

    function renderSectorOptions(options) {
        if (!sectorInput || !options) return;
        const selected = requestedSector || sectorInput.value;
        const choices = options.filter(option => option.value !== 'all');
        if (selected && !choices.some(option => option.value === selected)) choices.push({ value: selected, label: selected, count: 0 });
        sectorInput.replaceChildren(new Option(`All sectors (${options.find(option => option.value === 'all')?.count ?? 0})`, ''),
            ...choices.map(option => new Option(`${option.label} (${option.count})`, option.value)));
        sectorInput.value = selected;
        requestedSector = '';
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

    // The ten-second poll is silent and never interrupts a load the operator started. An operator
    // change cancels the request it replaces, so the newest choice always wins, and the list stays
    // usable throughout: only the small "Updating" marker shows while a request is out.
    async function loadCalendar(forceRefresh = false, { background = false } = {}) {
        if (background && inFlight) return;
        inFlight?.abort();
        const controller = inFlight = new AbortController(), sequence = ++loadSequence;
        const spinner = document.getElementById("earnings-spinner");
        if (!background && spinner) spinner.hidden = false;
        try {
            if (forceRefresh) {
                const refreshResponse = await window.tradingFlowFetch("/api/earnings/refresh", { method: "POST", signal: controller.signal });
                if (!refreshResponse.ok) throw new Error(await readError(refreshResponse, `Refresh failed (${refreshResponse.status}).`));
            }

            const query = queryState();
            const from = fromInput?.value, to = toInput?.value;
            if ((from || to) && (!from || !to || from > to || (new Date(to) - new Date(from)) / 86400000 > 34)) throw new Error('Choose both dates, in order, covering at most 35 days.');
            const useRange = Boolean(from && to);
            const endpoint = useRange ? "calendar" : "today-next-business-day";
            const response = await window.tradingFlowFetch(`/api/earnings/${endpoint}?${query}`, {
                headers: { Accept: "application/json" },
                cache: "no-store",
                signal: controller.signal
            });
            if (!response.ok) throw new Error(await readError(response, `Calendar request failed (${response.status}).`));
            const payload = await response.json();
            if (sequence !== loadSequence) return;

            latestPayload = payload;
            updateCounts(sessionFilters, latestPayload.filters?.sessions);
            markStrip(sessionFilters, "data-filter-value", serverFilters.session);
            updateCounts(capFilters, latestPayload.filters?.marketCaps);
            renderSectorOptions(latestPayload.filters?.sectors);
            markStrip(capFilters, "data-filter-value", serverFilters.marketCap);

            const seconds = latestPayload.monitoringIntervalSeconds;
            const cadence = seconds >= 60 && seconds % 60 === 0 ? `${seconds / 60} min` : `${seconds} s`;
            status.textContent = latestPayload.monitoringActive ? `Running · every ${cadence}` : "Offline";
            status.classList.toggle("accent-note", !latestPayload.monitoringActive);
            updated.textContent = latestPayload.lastAnalysisUtc ? localDateTime(latestPayload.lastAnalysisUtc, true) : "never";
            newsCaption.textContent = `${latestPayload.newsWindowLabel}. Times display in your device timezone; stored timestamps remain UTC.`;
            document.getElementById("earnings-news-window").textContent = latestPayload.newsWindowLabel;
            nextDay.textContent = latestPayload.nextBusinessDateLabel;

            render();
        } catch (error) {
            if (controller.signal.aborted || sequence !== loadSequence) return;
            status.textContent = error instanceof Error ? error.message : "Unable to load earnings data.";
            status.classList.add("accent-note");
        } finally {
            if (sequence === loadSequence) {
                inFlight = null;
                if (spinner) spinner.hidden = true;
            }
        }
    }

    /* --- wiring ------------------------------------------------------------- */

    refreshButton.addEventListener("click", () => loadCalendar(false));

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
        loadCalendar(false);
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
            loadCalendar(false);
        });
    }
    bindToggle(onlyHeldToggle, "onlyHeld");
    bindToggle(hasNewsToggle, "hasNews");

    minSurpriseInput?.addEventListener("change", () => {
        const value = Number(minSurpriseInput.value);
        clientFilters.minSurprise = minSurpriseInput.value === "" || Number.isNaN(value) ? null : value;
        loadCalendar(false);
    });
    sortInput?.addEventListener("change", () => {
        clientFilters.sort = sortInput.value;
        loadCalendar(false);
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

    advanced.addEventListener('submit', event => { event.preventDefault(); loadCalendar(false); });
    sectorInput?.addEventListener('change', () => loadCalendar(false));
    let searchTimer = null;
    searchInput?.addEventListener('input', () => { clearTimeout(searchTimer); searchTimer = setTimeout(() => loadCalendar(false), 300); });
    document.getElementById('earnings-this-week').addEventListener('click', () => {
        const now = new Date(`${exchangeDate(Date.now())}T12:00:00Z`);
        now.setUTCDate(now.getUTCDate() - (now.getUTCDay()+6)%7);
        fromInput.value = now.toISOString().slice(0,10); now.setUTCDate(now.getUTCDate()+6);
        toInput.value = now.toISOString().slice(0,10);
        markStrip(dateScope, 'data-scope-value', ''); loadCalendar(false);
    });
    const groupPicker = document.getElementById('earnings-group');
    if (groupPicker) window.tradingFlowFetch('/api/v1/wishlists/mutable', { cache:'no-store' }).then(async response => {
        if (!response.ok) return;
        for (const group of await response.json()) {
            const option = text('option', group.name); option.value = group.id; groupPicker.append(option);
        }
        groupPicker.value = initial.get('groupId') || '';
        if (initial.has('groupId')) loadCalendar(false);
    });
    loadCalendar();
    window.setInterval(tick, 1000);
    window.setInterval(() => loadCalendar(false, { background: true }), 10000);
})();
