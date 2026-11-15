(() => {
    'use strict';

    const prices = new Intl.NumberFormat(undefined, { style: 'currency', currency: 'USD', maximumFractionDigits: 4 });
    const numbers = new Intl.NumberFormat();
    const percents = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2, signDisplay: 'exceptZero' });
    const clockFormat = {
        year: 'numeric', month: 'short', day: 'numeric',
        hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23'
    };
    const clock = new Intl.DateTimeFormat(undefined, { ...clockFormat, timeZone: 'America/New_York' });
    const berlinClock = new Intl.DateTimeFormat(undefined, { ...clockFormat, timeZone: 'Europe/Berlin' });
    const statusLabels = {
        streaming: 'Streaming completed candles', delayed: 'Delayed candles', repairing: 'Repairing data',
        history_only: 'History only', closed: 'Market closed', unavailable: 'Candles unavailable'
    };
    const dailyContexts = new Set(['swing', 'week', 'month', 'year']);
    const horizonLabels = {
        swing: 'Daily pivots · 64 sessions', week: 'Daily pivots · 5 sessions', month: 'Daily pivots · 21 sessions', year: 'Daily pivots · 252 sessions'
    };
    const historyPageLimit = 3;

    // Express the same instant in New York and Berlin, with each zone's historical daylight saving rules.
    function localClocks(time) {
        return clock.format(time) + ' NY · ' + berlinClock.format(time) + ' Berlin';
    }

    // Show the displayed clocks and the exact original UTC instant for source evidence.
    function timestamp(value) {
        const time = Date.parse(value);
        return Number.isFinite(time) ? localClocks(time) + ' · ' + new Date(time).toISOString() : 'Unavailable';
    }

    // Put only text and explicit classes in elements that carry server evidence.
    function element(tag, content, className) {
        const node = document.createElement(tag);
        if (content != null) node.textContent = content;
        if (className) node.className = className;
        return node;
    }

    // Make ordered label/value pairs reusable for readings and their evidence.
    function fields(list, entries, grouped = false) {
        list.replaceChildren(...entries.flatMap(([label, value]) => {
            const term = element('dt', label);
            const detail = element('dd', value);
            if (!grouped) return [term, detail];
            const wrapper = element('div');
            wrapper.append(term, detail);
            return [wrapper];
        }));
    }

    // Limit external event links to ordinary articles reachable over HTTP or HTTPS.
    function articleUrl(value) {
        try {
            const url = new URL(value);
            return ['http:', 'https:'].includes(url.protocol) ? url.href : null;
        } catch {
            return null;
        }
    }

    // Prefer the latest source for each identifier while preserving older distinct evidence.
    function byId(items, identify) {
        return [...new Map(items.map(item => [identify(item), item])).values()];
    }

    // Preserve authoritative current-page removals when joining earlier completed candles.
    function joinedBars(current, older) {
        const decorate = bar => ({ ...bar, startMs: Date.parse(bar.startUtc), endMs: Date.parse(bar.endUtc) });
        const latest = current.bars.map(decorate);
        const first = latest[0]?.startMs;
        return byId([
            ...older.flatMap(page => page.bars.map(decorate)).filter(bar => first == null || bar.startMs < first),
            ...latest
        ].filter(bar => bar.completionState === 'completed' && bar.endMs > bar.startMs), bar => bar.startMs)
            .sort((a, b) => a.startMs - b.startMs);
    }

    // Summarize the age of the completed bar without confusing it with provider health.
    function readAge(value) {
        const minutes = Math.max(0, Math.floor((Date.now() - Date.parse(value)) / 60_000));
        if (!Number.isFinite(minutes)) return 'None';
        if (minutes >= 1440) return Math.floor(minutes / 1440) + 'd old';
        if (minutes >= 60) return Math.floor(minutes / 60) + 'h ' + minutes % 60 + 'm old';
        return minutes < 1 ? 'Less than 1m old' : minutes + 'm old';
    }

    // Return distances for levels observable by the selected candle's close.
    function distancesFor(bar, levels, hiddenLevels, reconstructed, latest) {
        return levels.flatMap(level => {
            if (hiddenLevels.has(level.key)) return [];
            const points = level.points || [];
            if (!points.length) {
                return latest && level.distancePct != null
                    ? [level.label + ' · Latest comparison ' + percents.format(level.distancePct) + '%'] : [];
            }
            const point = points.find(item => Date.parse(item.startUtc) === bar.startMs);
            const historical = point && window.TradingFlowStockChartPlot.reconstructedAtClose(point, bar, reconstructed);
            const available = point && Date.parse(point.availableAtUtc) <= bar.endMs;
            const value = (historical || available) && point.value;
            if (!(value > 0)) return [];
            const text = level.label + ' ' + percents.format(100 * (bar.close / value - 1)) + '%';
            return [historical ? 'Reconstructed for this close · ' + text + ' · Available ' + timestamp(point.availableAtUtc) : text];
        });
    }

    // Mount one read-only chart instance with scoped controls, data and disposal.
    function mount(root) {
        // Find the component's elements inside this instance so other hosts remain independent.
        const get = name => root.querySelector('[data-chart-' + name + ']');
        const transport = window.TradingFlowStockChartTransport;
        const plotting = window.TradingFlowStockChartPlot;
        const paths = { history: root.dataset.chartHistory, hub: root.dataset.chartHub };
        const controller = new AbortController();
        const layerFields = [...root.querySelectorAll('[data-chart-layer]')];
        const layers = Object.fromEntries(layerFields.map(input => [input.dataset.chartLayer, input.checked]));
        const hiddenLevels = new Set();
        const hiddenZones = new Set();
        let plot, selected, active, snapshot, key;
        let stop = () => Promise.resolve();
        let currentBars = [], currentLevels = [], olderPages = [];
        let paging = false;
        let historyExpired = false;
        let streamStatus = '';
        let reconstructed = false;

        // Add handlers whose lifetime is no longer than this chart host.
        function on(node, event, handler) {
            node.addEventListener(event, handler, { signal: controller.signal });
        }

        // Keep a status transition accessible while preserving the last price data on screen.
        function setStatus(state, text) {
            root.dataset.state = state;
            if (get('status').textContent !== text) get('status').textContent = text;
        }

        // Surface live transport state alongside the source's independently reported status.
        function refreshStatus() {
            const state = snapshot?.status || 'unavailable';
            const label = statusLabels[state] || 'Chart status unavailable';
            if (streamStatus) {
                setStatus(get('retry').hidden ? 'reconnecting' : 'unavailable',
                    streamStatus + (snapshot ? ' Showing last available candles · ' + label + '.' : ''));
            } else if (snapshot) {
                setStatus(state, label + (snapshot.reason ? ' · ' + snapshot.reason : ''));
            }
        }

        // Summarize feed, basis and last completed age directly above the chart.
        function refreshSource() {
            const evidence = snapshot?.evidence;
            if (!evidence) return;
            const evidenceText = [evidence.provider, evidence.feed?.toUpperCase(), evidence.adjustmentPolicy].filter(Boolean);
            evidenceText.push(evidence.lastCompletedUtc ? 'Last completed · ' + readAge(evidence.lastCompletedUtc) : 'No completed candle');
            if (reconstructed) {
                evidenceText.push('Reconstructed from retrieved history');
                if (currentLevels.some(level => level.points?.length)) {
                    evidenceText.push('Calculations shown at source candle close. Original availability may be later.');
                }
            }
            const showSessions = key.sessionView === 'extended' && layers.sessions;
            const text = evidenceText.join(' · ');
            const source = get('source');
            const summary = showSessions ? text + ' · Shading: Premarket / Postmarket' : text;
            if (source.textContent === summary) return;
            source.replaceChildren(text);
            if (showSessions) source.append(' · Shading: ',
                element('span', 'Premarket', 'stock-chart-early'), ' / ',
                element('span', 'Postmarket', 'stock-chart-late'));
        }

        // Present completed prices and provenance in a readout shared by the keyboard and crosshair.
        function inspect(start, announce = false) {
            const index = currentBars.findIndex(bar => bar.startMs === start);
            if (index === -1) return;
            const bar = currentBars[index];
            selected = start;
            const values = [
                ['Open', prices.format(bar.open)], ['High', prices.format(bar.high)], ['Low', prices.format(bar.low)],
                ['Close', prices.format(bar.close)], ['Volume', numbers.format(bar.volume)]
            ];
            get('candle-time').textContent = localClocks(bar.startMs) + ' · ' + bar.segment;
            get('candle-evidence').textContent = [
                timestamp(bar.startUtc) + ' → ' + timestamp(bar.endUtc),
                bar.sourceKind?.replaceAll('_', ' '), bar.knownAtUtc ? 'Original receipt ' + timestamp(bar.knownAtUtc) : 'Original receipt unavailable',
                bar.retrievedAtUtc ? 'Retrieved ' + timestamp(bar.retrievedAtUtc) : '', 'Available ' + timestamp(bar.availableAtUtc)
            ].filter(Boolean).join(' · ');
            const distances = distancesFor(bar, currentLevels, hiddenLevels, reconstructed, index === currentBars.length - 1);
            get('distances').hidden = distances.length === 0;
            get('distances').textContent = distances.join(' · ');
            fields(get('values'), values, true);
            get('prev').disabled = index === 0;
            get('next').disabled = index === currentBars.length - 1;
            get('readout').hidden = false;
            if (announce) get('inspection').textContent = get('candle-time').textContent + '. ' + values.map(value => value.join(' ')).join(', ');
        }

        // Move the keyboard inspection to a real completed bar and preserve zoom while bringing it into view.
        function inspectIndex(index) {
            const bar = currentBars[Math.max(0, Math.min(index, currentBars.length - 1))];
            if (!bar) return;
            plot.reveal(currentBars.indexOf(bar));
            inspect(bar.startMs, true);
        }

        // Show every level's underlying source date, availability and latest signed distance.
        function renderLevels(levels) {
            const focus = document.activeElement?.dataset.chartLevel;
            const entries = levels.map(level => {
                const row = element('li');
                const toggle = element('input');
                toggle.type = 'checkbox';
                toggle.checked = !hiddenLevels.has(level.key);
                toggle.dataset.chartLevel = level.key;
                const title = element('label');
                title.append(toggle, element('strong', level.label + ' · ' + prices.format(level.value)));
                const when = 'Available ' + timestamp(level.effectiveAtUtc);
                const distance = level.distancePct == null ? null : 'Latest close ' + percents.format(level.distancePct) + '%';
                const currentOnly = !level.points?.length;
                row.append(title, element('small', [level.sourceTimeframe, level.sourceDate, when, distance,
                    currentOnly ? 'Current level · shown at the right edge' : ''].filter(Boolean).join(' · ')));
                return row;
            });
            get('levels').replaceChildren(...(entries.length ? entries : [element('li', 'No verified price levels for this view.')]));
            if (focus) [...get('levels').querySelectorAll('input')].find(input => input.dataset.chartLevel === focus)?.focus({ preventScroll: true });
        }

        // Name each support/resistance band consistently in the compact key and the detailed evidence.
        function zoneTitle(zone) {
            const label = zone.type === 'support' ? 'Support' : 'Resistance';
            return label + ' · ' + prices.format(zone.lower) + ' – ' + prices.format(zone.upper);
        }

        // Label every enabled band beside the plot, including bands outside the visible price range.
        function renderZoneKey(zones) {
            const keyList = get('zone-key');
            const visible = zones.filter(zone => layers[zone.type] && !hiddenZones.has(zone.id));
            keyList.replaceChildren(...visible.map(zone => {
                const item = element('li');
                item.dataset.zoneType = zone.type;
                const evidence = [zone.currentOnly ? 'Current zone' : 'Historical zone', zone.sourceTimeframe + ' pivots'];
                if (zone.isPriceInBand) evidence.push('Latest close in band');
                item.append(element('strong', zoneTitle(zone)), element('small', evidence.join(' · ')));
                return item;
            }));
            keyList.hidden = visible.length === 0;
        }

        // Show the boundaries and effective clock behind each eligible support or resistance band.
        function renderZones(zones) {
            const focused = get('zones').contains(document.activeElement) ? document.activeElement.dataset.chartZone : null;
            const entries = zones.map(zone => {
                const row = element('li');
                row.dataset.zoneType = zone.type;
                const label = element('label');
                const toggle = element('input');
                toggle.type = 'checkbox';
                toggle.dataset.chartZone = zone.id;
                toggle.checked = !hiddenZones.has(zone.id);
                label.append(toggle, element('strong', zoneTitle(zone)));
                row.append(label,
                    element('small', [
                        zone.sourceTimeframe, zone.pivotCount + ' confirmed pivots', zone.sourceDates?.join(', '),
                        'First pivot ' + timestamp(zone.firstPivotUtc), 'Confirmed ' + timestamp(zone.latestConfirmationUtc),
                        (zone.currentOnly ? 'Inputs available ' : 'Effective ') + timestamp(zone.effectiveAtUtc),
                        zone.currentOnly ? 'Current reconstruction · shown at the right edge' : '',
                        zone.untilUtc ? 'Through ' + timestamp(zone.untilUtc) : '',
                        zone.isPriceInBand ? 'Latest close in band' : ''
                    ].filter(Boolean).join(' · ')));
                return row;
            });
            get('zones').replaceChildren(...(entries.length ? entries : [element('li', 'No confirmed zones for this view.')]));
            if (focused) [...get('zones').querySelectorAll('input')].find(input => input.dataset.chartZone === focused)?.focus({ preventScroll: true });
        }

        // Keep stored event timestamps and estimates visible even when no candle contains the event.
        function renderEvents(events) {
            const entries = events.map(event => {
                const row = element('li');
                const kind = event.kind === 'earnings' ? 'Earnings' : 'News';
                const matched = plotting.candleIndex(currentBars, Date.parse(event.eventAtUtc)) !== -1;
                row.append(element('strong', kind + (event.isEstimate ? ' estimate · ' : ' · ') + event.title),
                    element('small', [timestamp(event.eventAtUtc), event.source, matched ? '' : 'Outside loaded candle intervals'].filter(Boolean).join(' · ')));
                if (event.knownAtUtc) row.append(element('small', 'Recorded ' + timestamp(event.knownAtUtc)));
                if (event.detail) row.append(element('p', event.detail));
                const url = articleUrl(event.url);
                if (url) {
                    const link = element('a', 'Open source');
                    link.href = url;
                    link.target = '_blank';
                    link.rel = 'noopener noreferrer';
                    row.append(link);
                }
                return row;
            });
            get('events').replaceChildren(...(entries.length ? entries : [element('li', 'No stored news or earnings for this view.')]));
            get('event-count').textContent = events.length ? '(' + events.length + ')' : '';
        }

        // Show coverage assumptions and independent source clocks without inferring freshness from the WebSocket.
        function renderEvidence() {
            const evidence = snapshot.evidence;
            fields(get('evidence'), [
                ['Provider / feed', [evidence.provider, evidence.feed].filter(Boolean).join(' / ')],
                ['Price basis', evidence.adjustmentPolicy],
                ['Coverage', evidence.coverage],
                ['Coverage through', timestamp(evidence.coverageThroughUtc)],
                ['Last completed candle', timestamp(evidence.lastCompletedUtc)],
                ['Last source observation', timestamp(evidence.lastSourceObservedUtc)],
                ['Chart snapshot', timestamp(snapshot.asOfUtc)],
                ['Receipt basis', evidence.reconstructed ? 'Reconstructed from retrieved history' : 'See each candle’s original receipt'],
                ...((evidence.basisBreaks || []).map(time => ['Price-basis break', timestamp(time)])),
                ...((evidence.notes || []).map(note => ['Source note', note]))
            ]);
        }

        // Keep page navigation buttons aligned with the last accepted cursor and active request.
        function refreshPager() {
            const cursor = (olderPages.at(-1) || snapshot)?.historyCursor;
            get('older').disabled = !cursor || paging || historyExpired || olderPages.length >= historyPageLimit;
            get('history-reload').hidden = !historyExpired;
            get('latest').disabled = currentBars.length === 0;
        }

        // Update the visible baseline, combining only compatible earlier evidence.
        function paint(retainPosition) {
            currentBars = joinedBars(snapshot, olderPages);
            const recent = [...olderPages].reverse().concat(snapshot);
            reconstructed = recent.some(page => page.evidence.reconstructed);
            // An earlier page's reconstructed band is not a retained historical version.
            const zones = snapshot.zones;
            for (const id of hiddenZones) if (!zones.some(zone => zone.id === id)) hiddenZones.delete(id);
            currentLevels = snapshot.levels.map(level => ({
                ...level,
                points: byId(recent.flatMap(page => page.levels.find(item => item.key === level.key)?.points || []),
                    point => point.startUtc).sort((a, b) => Date.parse(a.startUtc) - Date.parse(b.startUtc))
            }));
            const events = byId(recent.flatMap(page => page.markers), event => event.id)
                .sort((a, b) => Date.parse(b.eventAtUtc) - Date.parse(a.eventAtUtc));
            plot.setData({ bars: currentBars, zones, levels: currentLevels, events, interval: key.interval,
                basisBreaks: recent.flatMap(page => page.evidence.basisBreaks || []),
                reconstructed }, retainPosition);
            const hasBars = currentBars.length > 0;
            get('empty').hidden = hasBars;
            get('empty').textContent = 'No completed candles for the selected view.';
            get('readout').hidden = !hasBars;
            if (hasBars) {
                const prior = currentBars.find(bar => bar.startMs === selected);
                inspect((prior || currentBars.at(-1)).startMs);
            }
            renderZoneKey(zones);
            renderZones(zones);
            renderLevels(currentLevels);
            renderEvents(events);
            renderEvidence();
            refreshSource();
            refreshPager();
        }

        // Clear historical pages if a provider, feed or adjustment policy changes during a read.
        function sameBasis(one, two) {
            return one.provider === two.provider && one.feed === two.feed && one.adjustmentPolicy === two.adjustmentPolicy;
        }

        // Clear prices and evidence when the active symbol or chart settings change.
        function resetSelection() {
            key = null;
            snapshot = null;
            selected = null;
            currentBars = [];
            currentLevels = [];
            olderPages = [];
            paging = false;
            historyExpired = false;
            streamStatus = '';
            get('history-reload').disabled = false;
            reconstructed = false;
            get('retry').hidden = true;
            get('readout').hidden = true;
            get('empty').hidden = false;
            get('empty').textContent = 'Loading completed candles…';
            renderZoneKey([]);
            for (const name of ['source', 'history-status', 'event-count', 'inspection', 'zones', 'levels', 'events', 'evidence']) get(name).replaceChildren();
            refreshPager();
        }

        // Use the active controls to start one observation, keeping the current view until a requested reload succeeds.
        function startSelection(reset = true, reloadEarlier = false) {
            const closed = stop();
            const request = new AbortController();
            active = request;
            if (reset) resetSelection();
            else {
                paging = false;
                if (reloadEarlier) {
                    get('history-reload').disabled = true;
                    get('history-status').textContent = 'Reloading earlier history links…';
                }
                streamStatus = 'Reconnecting chart stream…';
                get('retry').hidden = true;
                refreshStatus();
                refreshPager();
            }
            if (!root.dataset.symbol) {
                stop = () => closed;
                get('empty').textContent = 'Select a stock to open its chart.';
                setStatus('unavailable', 'No stock selected');
                if (plot) plot.setData({ bars: [], zones: [], levels: [], events: [], basisBreaks: [] });
                return;
            }

            const interval = get('interval').value;
            const daily = interval === '1d';
            const contextControl = get('context');
            const sessionControl = get('session');
            sessionControl.disabled = daily;
            sessionControl.options[0].textContent = daily ? 'Provider daily' : 'Regular';
            if (daily) sessionControl.value = 'regular';
            for (const option of contextControl.options) {
                if (option.value === 'auto') continue;
                option.disabled = daily !== dailyContexts.has(option.value);
            }
            if (contextControl.selectedOptions[0]?.disabled) contextControl.value = daily ? 'swing' : 'intraday';
            key = { symbol: root.dataset.symbol, interval, sessionView: sessionControl.value, context: contextControl.value };
            updateFullLink();
            get('context-label').textContent = daily
                ? (horizonLabels[key.context] || 'Daily pivots · Swing context') + ' · Provider daily aggregation'
                : 'Intraday zones use 15-minute candles';
            if (!plotting || !window.LightweightCharts || !transport) {
                stop = () => closed;
                get('empty').textContent = 'Chart assets could not load. Reload this page to retry.';
                setStatus('unavailable', 'Chart unavailable');
                return;
            }
            if (!plot) plot = plotting.createPlot(get('plot'), start => {
                if (start !== selected) inspect(start);
            });
            if (reset) {
                plot.setData({ bars: [], zones: [], levels: [], events: [], basisBreaks: [], interval });
                plot.setLayers(layers, hiddenLevels, hiddenZones);
                setStatus('loading', 'Loading completed candles…');
            }
            stop = transport.watch(paths, key, request, {
                // Apply a fresh baseline only while its exact selection is mounted.
                snapshot(received, live) {
                    if (active !== request || request.signal.aborted) return;
                    if (reloadEarlier && received.bars.length) {
                        reloadEarlier = false;
                        historyExpired = false;
                        olderPages = [];
                        get('history-reload').disabled = false;
                        get('history-status').textContent = received.historyCursor ? 'Earlier history links refreshed.' : 'No earlier history is available.';
                    } else if (reloadEarlier) {
                        get('history-reload').disabled = false;
                        get('history-status').textContent = 'Could not reload earlier history. Try again shortly.';
                        streamStatus = 'Chart refresh unavailable.';
                        get('retry').hidden = false;
                        refreshStatus();
                        return;
                    }
                    if (snapshot && !sameBasis(snapshot.evidence, received.evidence)) olderPages = [];
                    const followNewest = currentBars.at(-1)?.startMs === selected;
                    snapshot = received;
                    if (followNewest) selected = null;
                    if (live) {
                        streamStatus = '';
                        get('retry').hidden = true;
                    }
                    refreshStatus();
                    paint(false);
                },
                // Display connection failures without erasing valid candles or implying a live quote.
                status(text, retrying) {
                    if (active !== request || request.signal.aborted) return;
                    streamStatus = text;
                    get('retry').hidden = retrying;
                    if (reloadEarlier && !retrying) {
                        get('history-reload').disabled = false;
                        get('history-status').textContent = 'Could not reload earlier history. Try again shortly.';
                    }
                    if (!snapshot) get('empty').textContent = text;
                    refreshStatus();
                }
            }, closed);
        }

        // Add earlier history to the left of the candle set without replacing the current zoom or inspection.
        async function loadEarlier() {
            const cursor = (olderPages.at(-1) || snapshot)?.historyCursor;
            if (!cursor || paging || historyExpired || olderPages.length >= historyPageLimit) return;
            const request = active;
            paging = true;
            refreshPager();
            get('history-status').textContent = 'Loading earlier candles…';
            try {
                const page = await transport.history(paths.history, key, cursor, request.signal);
                if (active !== request || request.signal.aborted) return;
                if (!sameBasis(snapshot.evidence, page.evidence)) {
                    get('history-status').textContent = 'Earlier candles use a different price basis.';
                    return;
                }
                olderPages.push(page);
                const prior = currentBars.length;
                paint(true);
                const count = currentBars.length - prior;
                get('history-status').textContent = count > 0 ? count + ' earlier candles loaded' : 'No earlier candles in this page';
                if (olderPages.length >= historyPageLimit && page.historyCursor) {
                    get('history-status').textContent += ' · Earlier history limit reached';
                }
            } catch (error) {
                if (active === request && !request.signal.aborted) {
                    historyExpired = error.status === 400;
                    get('history-status').textContent = historyExpired
                        ? 'This history link expired. Reload earlier history to continue.' : error.message;
                }
            } finally {
                if (active === request) {
                    paging = false;
                    refreshPager();
                }
            }
        }

        // Replace only the symbol, preserving the operator's interval and layer settings.
        function setSymbol(symbol) {
            const normalized = (symbol || '').trim().toUpperCase().replaceAll('.', '-');
            if (root.dataset.symbol === normalized && active) return;
            root.dataset.symbol = normalized;
            hiddenZones.clear();
            startSelection();
        }

        // Carry the current interval and context when enlarging the embedded Desk chart.
        function updateFullLink() {
            const full = get('full');
            if (!full) return;
            const url = new URL(full.href);
            url.searchParams.set('symbol', key.symbol);
            url.searchParams.set('interval', key.interval);
            url.searchParams.set('sessionView', key.sessionView);
            url.searchParams.set('chartContext', key.context);
            full.href = url.href;
            full.setAttribute('aria-label', 'Open full chart for ' + key.symbol);
        }

        const controls = { interval: 'interval', session: 'sessionView', context: 'chartContext' };
        if (root.closest('.stock-chart-page')) {
            const query = new URLSearchParams(location.search);
            for (const [picker, field] of Object.entries(controls)) {
                const select = get(picker);
                const value = query.get(field);
                if ([...select.options].some(option => option.value === value)) select.value = value;
            }
        }
        for (const picker of Object.keys(controls)) on(get(picker), 'change', () => startSelection());
        on(get('retry'), 'click', () => startSelection(!snapshot));
        on(get('older'), 'click', loadEarlier);
        on(get('history-reload'), 'click', () => startSelection(false, true));
        on(get('latest'), 'click', () => { plot.latest(); inspect(currentBars.at(-1).startMs, true); });
        on(get('prev'), 'click', () => inspectIndex(currentBars.findIndex(bar => bar.startMs === selected) - 1));
        on(get('next'), 'click', () => inspectIndex(currentBars.findIndex(bar => bar.startMs === selected) + 1));
        on(get('plot'), 'keydown', event => {
            // Keep native Tab and Escape navigation while giving arrow controls to the chart.
            if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key) || !currentBars.length) return;
            event.preventDefault();
            const index = currentBars.findIndex(bar => bar.startMs === selected);
            const next = { ArrowLeft: index - 1, ArrowRight: index + 1, Home: 0, End: currentBars.length - 1 };
            inspectIndex(next[event.key]);
        });
        on(get('levels'), 'change', event => {
            // Persist individual price-level choices across incoming baselines.
            const id = event.target.dataset.chartLevel;
            if (!id) return;
            if (event.target.checked) hiddenLevels.delete(id);
            else hiddenLevels.add(id);
            plot.setLayers(layers, hiddenLevels, hiddenZones);
            inspect(selected);
        });
        on(get('zones'), 'change', event => {
            // Keep a hidden band hidden across accepted snapshots of the active series.
            const id = event.target.dataset.chartZone;
            if (!id) return;
            if (event.target.checked) hiddenZones.delete(id);
            else hiddenZones.add(id);
            plot.setLayers(layers, hiddenLevels, hiddenZones);
            renderZoneKey(snapshot?.zones || []);
        });
        for (const checkbox of layerFields) on(checkbox, 'change', () => {
            // Toggle a layer without querying the source again.
            layers[checkbox.dataset.chartLayer] = checkbox.checked;
            plot?.setLayers(layers, hiddenLevels, hiddenZones);
            renderZoneKey(snapshot?.zones || []);
            refreshSource();
        });
        const sourceAge = setInterval(refreshSource, 15_000);
        if (root.dataset.symbol) startSelection();
        return {
            setSymbol,
            // Cancel network work and release canvases while the page or selected-stock host is absent.
            dispose() {
                controller.abort();
                clearInterval(sourceAge);
                const result = stop();
                active = null;
                plot?.dispose();
                return result;
            }
        };
    }

    window.TradingFlowStockChart = { mount };
    const pageChart = document.querySelector('.stock-chart-page [data-stock-chart][data-symbol]');
    if (pageChart) {
        let page = mount(pageChart);
        // Recreate the chart when browser back/forward cache restores the document.
        window.addEventListener('pageshow', event => { if (event.persisted) page = mount(pageChart); });
        // Close live connections when leaving the standalone stock page.
        window.addEventListener('pagehide', () => { void page.dispose(); });
    }
})();
