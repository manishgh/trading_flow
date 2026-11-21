(() => {
    'use strict';

    const prices = new Intl.NumberFormat(undefined, { style: 'currency', currency: 'USD', maximumFractionDigits: 4 });
    const zonePrices = new Intl.NumberFormat(undefined, { style: 'currency', currency: 'USD', minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const numbers = new Intl.NumberFormat();
    const percents = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2, signDisplay: 'exceptZero' });
    const clockFormat = {
        year: 'numeric', month: 'short', day: 'numeric',
        hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23'
    };
    const clock = new Intl.DateTimeFormat(undefined, { ...clockFormat, timeZone: 'America/New_York' });
    const berlinClock = new Intl.DateTimeFormat(undefined, { ...clockFormat, timeZone: 'Europe/Berlin' });
    const sessionDate = new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'short', day: 'numeric', timeZone: 'UTC' });
    const statusLabels = {
        streaming: 'Streaming completed candles', delayed: 'Delayed candles', repairing: 'Repairing data',
        history_only: 'History only', closed: 'Market closed', unavailable: 'Candles unavailable'
    };
    const formingLabels = {
        forming: 'Forming · SIP', not_streaming: 'Not streaming', coverage_unverified: 'SIP minute coverage is unverified',
        suppressed: 'SIP preview withheld while trade evidence is unresolved or exceeds its limit',
        waiting_for_trade: 'Waiting for eligible SIP trades', closed: 'Outside the selected exchange session',
        unsupported: 'Forming candles are available on 1m, 5m and 15m charts'
    };
    const horizonLabels = {
        swing: '64-session lookback', week: 'week lookback', month: 'month lookback', year: 'year lookback'
    };
    const defaults = { interval: '5m', session: 'regular', structure: 'auto', lookback: 'swing' };
    const historyPageLimit = 3;

    // Express the same instant in New York and Berlin, with each zone's historical daylight saving rules.
    function localClocks(time, fraction = '') {
        return clock.format(time) + fraction + ' NY · ' + berlinClock.format(time) + fraction + ' Berlin';
    }

    // Show the displayed clocks and the exact original UTC instant for source evidence.
    function timestamp(value, exact = false) {
        const time = Date.parse(value);
        if (!Number.isFinite(time)) return 'Unavailable';
        const fraction = exact ? String(value).match(/\.\d+(?=Z$|[+-]\d{2}:\d{2}$)/i)?.[0] : '';
        const utc = new Date(time).toISOString();
        return localClocks(time, fraction) + ' · ' + (fraction ? utc.replace(/\.\d+Z$/, fraction + 'Z') : utc);
    }

    // Format DateOnly in UTC so a provider's literal exchange date never moves across a local midnight.
    function candleCaption(bar, interval) {
        if (interval === '1w') return bar.sessionDate + ' – ' + bar.weekly.lastSessionDate + ' · Weekly (1w)';
        if (interval !== '1d') return localClocks(bar.startMs) + ' · ' + bar.segment;
        const time = Date.parse(bar.sessionDate);
        return (Number.isFinite(time) ? sessionDate.format(time) : 'Session date unavailable') + ' · Daily';
    }

    // Put only text and explicit classes in elements that carry server evidence.
    function element(tag, content, className) {
        const node = document.createElement(tag);
        if (content != null) node.textContent = content;
        if (className) node.className = className;
        return node;
    }

    // Update only changed evidence fields so the full revision stays selectable across snapshot updates.
    function fields(list, entries, grouped = false) {
        for (const [index, [label, value]] of entries.entries()) {
            let row = grouped ? list.children[index] : list;
            if (!row) {
                row = element('div');
                list.append(row);
            }
            const offset = grouped ? 0 : 2 * index;
            let term = row.children[offset];
            let detail = row.children[offset + 1];
            if (!term) {
                term = element('dt');
                detail = element('dd');
                row.append(term, detail);
            }
            if (term.textContent !== label) term.textContent = label;
            const text = String(value ?? '');
            if (detail.textContent !== text) detail.textContent = text;
        }
        for (const child of [...list.children].slice(grouped ? entries.length : 2 * entries.length)) child.remove();
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
        const owned = window.TradingFlowStockChartIndicators.pageRanges(current, older)
            .flatMap(({ page, before }) => page.bars.map(decorate).filter(bar => bar.startMs < before));
        return byId(owned.filter(bar => bar.completionState === 'completed' && bar.endMs > bar.startMs), bar => bar.startMs)
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
                    ? [[level.label, 'Latest comparison ' + percents.format(level.distancePct) + '%']] : [];
            }
            const point = points.find(item => Date.parse(item.startUtc) === bar.startMs);
            const historical = point && window.TradingFlowStockChartPlot.reconstructedAtClose(point, bar, reconstructed);
            const available = point && Date.parse(point.availableAtUtc) <= bar.endMs;
            const value = (historical || available) && point.value;
            if (!(value > 0)) return [];
            const distance = percents.format(100 * (bar.close / value - 1)) + '%';
            return [[level.label, [distance, historical ? 'Reconstructed for this close' : '',
                'Available ' + timestamp(point.availableAtUtc, true)].filter(Boolean).join(' · ')]];
        });
    }

    // Mount one read-only chart instance with scoped controls, data, optional host defaults/zone callback, and disposal.
    function mount(root, { privateContext, acceptanceCorrelation, onAcceptanceEnded, summaryHost, defaultInterval, defaultStudies, onZones, compactDesk, controlsHost, quickIntervalHost, onSeriesInfo } = {}) {
        const acceptance = Boolean(acceptanceCorrelation);
        let acceptanceStarted = false;
        const summaryNodes = new Map();
        const relocatedNodes = [];
        if (summaryHost) {
            const head = root.querySelector('.stock-chart-head');
            for (const name of ['status', 'completed-note', 'full']) summaryNodes.set(name, head.querySelector('[data-chart-' + name + ']'));
            summaryHost.append(...head.childNodes);
            head.hidden = true;
        }
        // Relocates a chart sub-element into a compact Desk container and records its original slot for clean disposal.
        function relocateNode(node, destination, prepend = false) {
            if (!node || !destination) return;
            relocatedNodes.push({ node, parent: node.parentNode, nextSibling: node.nextSibling });
            if (prepend) destination.prepend(node);
            else destination.append(node);
        }
        // Moves Desk readout into the chart stage overlay and tucks secondary controls/navigation inside Studies & settings while keeping Retry chart visible.
        function applyCompactDeskLayout() {
            if (!compactDesk) return;
            root.dataset.compactDesk = 'true';
            const stage = root.querySelector('.stock-chart-stage');
            const readout = root.querySelector('[data-chart-readout]');
            relocateNode(readout, stage);
            const more = root.querySelector('.stock-chart-more');
            const moreBody = more?.querySelector('.stock-chart-more-body');
            if (moreBody) {
                const navRow = element('div', null, 'stock-chart-more-nav');
                for (const selector of ['[data-chart-older]', '[data-chart-latest]', '[data-chart-history-reload]', '[data-chart-details-open]', '[data-chart-history-status]']) {
                    relocateNode(root.querySelector(selector), navRow);
                }
                const controlsRow = element('div', null, 'stock-chart-more-controls');
                relocateNode(root.querySelector('.stock-chart-controls'), controlsRow);
                const presetLabel = root.querySelector('.stock-chart-study-toolbar > label');
                relocateNode(presetLabel, controlsRow);
                moreBody.prepend(controlsRow, navRow);
            }
            if (more && controlsHost) {
                relocateNode(more, controlsHost);
            }
        }
        applyCompactDeskLayout();
        // Find the component's elements inside this instance (and any Desk host slots) so other hosts remain independent.
        const get = name => summaryNodes.get(name)
            || root.querySelector('[data-chart-' + name + ']')
            || controlsHost?.querySelector('[data-chart-' + name + ']');
        const transport = window.TradingFlowStockChartTransport;
        const plotting = window.TradingFlowStockChartPlot;
        const indicatorLibrary = window.TradingFlowStockChartIndicators;
        const paths = { history: root.dataset.chartHistory, hub: root.dataset.chartHub };
        const controller = new AbortController();
        const details = get('details');
        const layerFields = [...root.querySelectorAll('[data-chart-layer]')];
        const layers = Object.fromEntries(layerFields.map(input => [input.dataset.chartLayer, input.checked]));
        const hiddenLevels = new Set();
        const hiddenZones = new Set();
        let plot, selected, active, snapshot, key;
        let stop = () => Promise.resolve();
        let currentBars = [], currentLevels = [], olderPages = [];
        let paging = false;
        let historyExpired = false;
        let earlierStudiesChanged = false;
        let streamStatus = '';
        let reconstructed = false;
        let shownPane = null;
        let pointerMoved = false;
        let preservedSession;
        let forming;
        let drawnForming;
        let pendingTradePlan = null;
        let privateReady = false;
        let privateLayer = privateContext?.symbol === root.dataset.symbol
            ? window.TradingFlowPrivateChart?.mount(root, privateContext, events => plot?.setPrivateEvents(events), timestamp)
            : null;

        // Add handlers whose lifetime is no longer than this chart host.
        function on(node, event, handler) {
            node.addEventListener(event, handler, { signal: controller.signal });
        }

        // Forwards compact series status, completed-candle note, and source summary to the Desk info tooltip.
        function emitSeriesInfo() {
            if (!onSeriesInfo) return;
            const parts = [
                get('status')?.textContent,
                get('completed-note')?.textContent,
                get('source')?.textContent
            ].filter(Boolean);
            onSeriesInfo(parts.join(' · '));
        }

        // Keep a status transition accessible while preserving the last price data on screen.
        function setStatus(state, text) {
            root.dataset.state = state;
            if (summaryHost) summaryHost.dataset.state = state;
            if (get('status').textContent !== text) get('status').textContent = text;
            emitSeriesInfo();
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
            const weekly = key.interval === '1w';
            const evidenceText = weekly ? ['SIP daily · adjusted']
                : [evidence.provider, evidence.feed?.toUpperCase(), evidence.adjustmentPolicy].filter(Boolean);
            evidenceText.push(evidence.lastCompletedUtc ? 'Last completed · ' + readAge(evidence.lastCompletedUtc) : 'No completed candle');
            if (evidence.coverage === 'partial') evidenceText.push(weekly ? 'Weekly coverage gaps' : 'Partial candle coverage');
            if (evidence.weekly?.pending) evidenceText.push('Current week pending');
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
            emitSeriesInfo();
        }

        // Present preview evidence independently and never inspect it as a completed candle.
        function renderForming(preview, fallback = 'Waiting for SIP candle preview…') {
            const enabled = layers.forming;
            const supported = ['1m', '5m', '15m'].includes(get('interval').value);
            const start = Date.parse(preview?.startUtc);
            const end = Date.parse(preview?.endUtc);
            const latest = currentBars.at(-1);
            if (preview?.status === 'forming' && latest && start < latest.endMs) preview = null;
            forming = enabled && supported ? preview : null;
            const candidate = forming?.status === 'forming' ? forming : null;
            if (drawnForming !== candidate) {
                drawnForming = candidate;
                plot?.setForming(candidate);
            }
            const status = get('forming-status');
            status.hidden = !enabled;
            const label = supported ? formingLabels[forming?.status] || fallback : formingLabels.unsupported;
            if (enabled && status.textContent !== label) status.textContent = label;
            const completedNote = candidate ? 'Readout shows completed candles' : 'Completed candles only';
            if (get('completed-note').textContent !== completedNote) get('completed-note').textContent = completedNote;
            emitSeriesInfo();
            get('forming-section').hidden = !enabled;
            fields(get('forming-evidence'), !enabled ? [] : [
                ['Status', label],
                ...(forming ? [['Feed', 'SIP'],
                    ...(Number.isFinite(start) && Number.isFinite(end) ? [
                        ['Bucket opens', timestamp(forming.startUtc)], ['Bucket ends', timestamp(forming.endUtc)],
                        ['Exchange segment', forming.segment]
                    ] : []),
                    ['Last source event', timestamp(forming.lastEventAtUtc, true)],
                    ['Last source observation', timestamp(forming.lastObservedAtUtc, true)]] : []),
                ...(candidate ? [['Open', prices.format(candidate.open)], ['High', prices.format(candidate.high)],
                    ['Low', prices.format(candidate.low)], ['Close', prices.format(candidate.close)],
                    ['Eligible volume', candidate.eligibleVolume == null ? 'Unavailable' : numbers.format(candidate.eligibleVolume)]] : [])
            ]);
            if (snapshot && !currentBars.length) get('empty').hidden = Boolean(candidate);
        }

        // A private window follows displayed completed candles and the last authorized baseline clock.
        function updatePrivateWindow(bounds) {
            if (!privateLayer || !privateReady || key?.symbol !== privateContext.symbol) return;
            if (key.interval === '1w') { privateLayer.setWindow(null, 'weekly'); return; }
            const asOfMs = Date.parse(snapshot?.asOfUtc);
            const toMs = Math.min(bounds?.toMs, asOfMs);
            privateLayer.setWindow(bounds?.fromMs < toMs ? { fromMs: bounds.fromMs, toMs, asOfMs } : null);
        }

        // Present compact prices and keep the matching selected study values in the chart details.
        function inspect(start, announce = false) {
            const index = currentBars.findIndex(bar => bar.startMs === start);
            if (index === -1) return;
            const bar = currentBars[index];
            selected = start;
            const fullValues = [
                ['Open', prices.format(bar.open)], ['High', prices.format(bar.high)], ['Low', prices.format(bar.low)],
                ['Close', prices.format(bar.close)], ['Volume', numbers.format(bar.volume)]
            ];
            const values = compactDesk ? [
                ['O', prices.format(bar.open)], ['H', prices.format(bar.high)], ['L', prices.format(bar.low)],
                ['C', prices.format(bar.close)], ['Vol', numbers.format(bar.volume)]
            ] : fullValues;
            const title = candleCaption(bar, key.interval);
            const time = get('candle-time');
            if (time.textContent !== title) time.textContent = title;
            fields(get('values'), values, true);
            const indicators = studies.readouts(start).map(([label, value, available, anchor, revision, reconstructedStudy, observed, historical]) =>
                ({ label, value, available, anchor, revision, reconstructedStudy, observed, historical }));
            const studyReadout = get('study-values');
            studyReadout.hidden = !indicators.length;
            fields(studyReadout, indicators.map(({ label, value }) => [label, value]), true);
            const history = get('study-history');
            const notes = [
                indicators.some(item => item.reconstructedStudy) ? 'Study history includes reconstructed values' : '',
                indicators.some(item => item.historical) ? 'Separate earlier study segment' : ''
            ].filter(Boolean).join(' · ');
            if (history.textContent !== notes) history.textContent = notes;
            history.hidden = !notes;
            renderSelectedEvidence(bar, indicators, index === currentBars.length - 1);
            get('prev').disabled = index === 0;
            get('next').disabled = index === currentBars.length - 1;
            get('readout').hidden = false;
            if (announce) get('inspection').textContent = title + '. ' + fullValues
                .map(([label, value]) => label + ' ' + value).join(', ');
        }

        // Keep the same selected candle and matched studies in the chart details with their independent clocks.
        function renderSelectedEvidence(bar, indicators, latest) {
            get('selected-candle').hidden = !bar;
            fields(get('candle-evidence'), bar ? [
                ['Start', timestamp(bar.startUtc, true)], ['End', timestamp(bar.endUtc, true)],
                ['Source', bar.weekly ? 'SIP daily · adjusted' : bar.sourceKind?.replaceAll('_', ' ') || 'Unavailable'],
                [bar.weekly ? 'Latest original daily receipt' : 'Original receipt',
                    bar.knownAtUtc ? timestamp(bar.knownAtUtc, true) : 'Original receipt unavailable'],
                ...(bar.retrievedAtUtc ? [['Retrieved', timestamp(bar.retrievedAtUtc, true)]] : []),
                ['Available', timestamp(bar.availableAtUtc, true)],
                ...((bar.weekly?.constituents || []).map(input => ['Daily (1d) · ' + input.sessionDate,
                    [input.sourceKind.replaceAll('_', ' '), input.knownAtUtc ? 'Original receipt ' + timestamp(input.knownAtUtc, true)
                        : input.retrievedAtUtc ? 'Reconstructed · retrieved ' + timestamp(input.retrievedAtUtc, true)
                            : 'Original receipt unavailable'].join(' · ')]))
            ] : []);
            const studyEvidence = indicators.flatMap(({ label, available, anchor, revision, reconstructedStudy, observed, historical }) =>
                available ? [[label, [
                    ['Available', timestamp(available, true)],
                    ['Calculation start', timestamp(anchor, true)],
                    ['Observed snapshot', timestamp(observed, true)],
                    ['Source revision', revision || 'Unavailable'],
                    ...(reconstructedStudy ? [['Series / page', 'Study history includes reconstructed values']] : []),
                    ...(historical ? [['Calculation segment', 'Separate earlier study segment']] : [])
                ]]] : []);
            renderIndicatorEvidence(studyEvidence);
            const comparisons = bar ? distancesFor(bar, currentLevels, hiddenLevels, reconstructed, latest) : [];
            get('selected-comparisons').hidden = !comparisons.length;
            fields(get('distances'), comparisons);
        }

        // Keep each study's evidence nodes across snapshots of the same source interval and study.
        function renderIndicatorEvidence(studies) {
            get('selected-indicators').hidden = !studies.length;
            const list = get('indicator-evidence');
            const rows = new Map([...list.children].map(row => [row.firstElementChild.textContent, row]));
            for (const [index, [label, evidence]] of studies.entries()) {
                let row = rows.get(label);
                if (!row) {
                    row = element('li');
                    row.append(element('strong', label), element('dl', null, 'stock-chart-evidence'));
                }
                const position = list.children[index];
                if (position !== row) list.insertBefore(row, position ?? null);
                fields(row.lastElementChild, evidence);
            }
            for (const row of [...list.children].slice(studies.length)) row.remove();
        }

        // Clear stale evidence when no candle is left in the accepted joined view.
        function clearInspection() {
            selected = null;
            get('readout').hidden = true;
            get('candle-time').textContent = '';
            get('inspection').textContent = '';
            fields(get('values'), [], true);
            fields(get('study-values'), [], true);
            get('study-values').hidden = true;
            get('study-history').replaceChildren();
            get('study-history').hidden = true;
            renderSelectedEvidence(null, [], false);
        }

        // Move the keyboard inspection to a real completed bar and preserve zoom while bringing it into view.
        function inspectIndex(index) {
            const bar = currentBars[Math.max(0, Math.min(index, currentBars.length - 1))];
            if (!bar) return;
            pointerMoved = false;
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

        // Name each support/resistance band consistently in the detailed evidence modal.
        function zoneTitle(zone) {
            const label = zone.type === 'support' ? 'Support' : 'Resistance';
            return label + ' · ' + prices.format(zone.lower) + ' – ' + prices.format(zone.upper);
        }

        // Format a compact 2-decimal support/resistance label with minimum words for the chart zone key.
        function compactZoneTitle(zone) {
            const label = zone.type === 'support' ? 'Support' : 'Resistance';
            return label + ' ' + zonePrices.format(zone.lower) + '–' + zonePrices.format(zone.upper);
        }

        // Label visible bands concisely with 2-decimal bounds, keeping pivot/timestamp evidence in tooltips and notifying hosts.
        function renderZoneKey(zones) {
            const keyList = get('zone-key');
            const visible = (zones || []).filter(zone => layers[zone.type] && !hiddenZones.has(zone.id));
            keyList.replaceChildren(...visible.map(zone => {
                const item = element('li');
                item.dataset.zoneType = zone.type;
                const evidence = [zone.currentOnly ? 'Current zone' : 'Historical zone', zone.sourceTimeframe + ' pivots'];
                if (zone.isPriceInBand) evidence.push('Latest close in band');
                if (zone.effectiveAtUtc) evidence.push('Inputs available ' + timestamp(zone.effectiveAtUtc));
                item.title = evidence.join(' · ');
                item.append(element('strong', compactZoneTitle(zone)));
                return item;
            }));
            keyList.hidden = visible.length === 0;
            onZones?.(visible, root.dataset.symbol || null);
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

        // Keep event evidence visible and preserve the focused source link across chart snapshots.
        function renderEvents(events) {
            const list = get('events');
            const focusedEvent = list.contains(document.activeElement) ? document.activeElement.dataset.chartEvent : null;
            const weekly = key.interval === '1w';
            const entries = events.map(event => {
                const row = element('li');
                const kind = event.kind === 'earnings' ? 'Earnings' : 'News';
                const matched = plotting.eventCandleIndex(currentBars, event, key.interval) !== -1;
                row.append(element('strong', kind + (event.isEstimate ? ' estimate · ' : ' · ') + event.title),
                    element('small', [timestamp(event.eventAtUtc, weekly), event.source,
                        matched ? '' : weekly ? 'Outside loaded weeks' : 'Outside loaded candle intervals'].filter(Boolean).join(' · ')));
                if (event.knownAtUtc) row.append(element('small', 'Recorded ' + timestamp(event.knownAtUtc, weekly)));
                if (event.detail) row.append(element('p', event.detail));
                const url = articleUrl(event.url);
                if (url) {
                    const link = element('a', 'Open source');
                    link.dataset.chartEvent = event.id;
                    link.href = url;
                    link.target = '_blank';
                    link.rel = 'noopener noreferrer';
                    row.append(link);
                }
                return row;
            });
            list.replaceChildren(...(entries.length ? entries : [element('li', 'No stored news or earnings for this view.')]));
            if (focusedEvent) {
                const link = [...list.querySelectorAll('a')].find(item => item.dataset.chartEvent === focusedEvent);
                (link || get('details-close')).focus({ preventScroll: true });
            }
            get('event-count').textContent = events.length ? '(' + events.length + ')' : '';
        }

        // Show coverage assumptions and independent source clocks without inferring freshness from the WebSocket.
        function renderEvidence() {
            const evidence = snapshot.evidence;
            const weeks = [snapshot, ...olderPages].map(page => page.evidence.weekly).filter(Boolean);
            fields(get('evidence'), [
                ['Provider / feed', [evidence.provider, evidence.feed].filter(Boolean).join(' / ')],
                ['Price basis', evidence.adjustmentPolicy],
                ['Coverage', evidence.coverage],
                ['Coverage through', timestamp(evidence.coverageThroughUtc)],
                ['Last completed candle', timestamp(evidence.lastCompletedUtc)],
                ['Last source observation', timestamp(evidence.lastSourceObservedUtc)],
                ['Chart snapshot', timestamp(snapshot.asOfUtc)],
                ['Receipt basis', evidence.reconstructed ? 'Reconstructed from retrieved history' : 'See each candle’s original receipt'],
                ...(evidence.weekly ? [['Daily / calendar revision', evidence.weekly.sourceRevision]] : []),
                ...weeks.flatMap(week => week.gaps.map(gap => ['Week of ' + gap.weekOf, gap.reason])),
                ...((evidence.basisBreaks || []).map(time => ['Price-basis break', timestamp(time)])),
                ...((evidence.notes || []).map(note => ['Source note', note]))
            ]);
        }

        // Keep page navigation buttons aligned with the last accepted cursor and active request.
        function refreshPager() {
            const cursor = (olderPages.at(-1) || snapshot)?.historyCursor;
            const profilePending = snapshot?.seriesKey.indicators !== key?.indicators;
            get('older').disabled = !cursor || paging || historyExpired || profilePending || olderPages.length >= historyPageLimit;
            get('history-reload').hidden = !historyExpired && !earlierStudiesChanged;
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
            const selectedStudies = indicatorLibrary.joinedStudies(snapshot, olderPages.slice().reverse(), studies.tokens());
            earlierStudiesChanged = selectedStudies.some(study => study.earlierUnavailable);
            studies.update(selectedStudies);
            pointerMoved = false;
            plot.setData({ bars: currentBars, zones, levels: currentLevels, events, interval: key.interval,
                basisBreaks: recent.flatMap(page => page.evidence.basisBreaks || []),
                reconstructed, studies: selectedStudies, shownPane }, retainPosition);
            updatePrivateWindow(plot.visibleBounds());
            const hasBars = currentBars.length > 0;
            get('empty').hidden = hasBars;
            get('empty').textContent = key.interval === '1w'
                ? 'No completed weeks in the loaded history.' : 'No completed candles for the selected view.';
            get('readout').hidden = !hasBars;
            if (hasBars) {
                const prior = currentBars.find(bar => bar.startMs === selected);
                inspect((prior || currentBars.at(-1)).startMs);
            } else clearInspection();
            renderZoneKey(zones);
            renderZones(zones);
            renderLevels(currentLevels);
            renderEvents(events);
            renderEvidence();
            renderForming(forming);
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
            earlierStudiesChanged = false;
            streamStatus = '';
            get('history-reload').disabled = false;
            reconstructed = false;
            studies.update([]);
            get('retry').hidden = true;
            clearInspection();
            get('empty').hidden = false;
            get('empty').textContent = 'Loading completed candles…';
            renderZoneKey([]);
            for (const name of ['source', 'history-status', 'event-count', 'zones', 'levels', 'events']) get(name).replaceChildren();
            fields(get('evidence'), []);
            refreshPager();
        }

        // Apply interval-specific controls while retaining the intraday session and structural preferences.
        function configureInterval(interval) {
            const weekly = interval === '1w';
            const daily = interval === '1d';
            const session = get('session');
            if (weekly) {
                preservedSession ??= session.value;
                session.value = 'regular';
            } else if (preservedSession !== undefined) {
                session.value = preservedSession;
                preservedSession = undefined;
            }
            session.disabled = acceptance || daily || weekly;
            session.options[0].textContent = daily || weekly ? 'Provider daily' : 'Regular';
            if (daily) session.value = 'regular';
            const structure = get('structure');
            structure.disabled = acceptance || weekly;
            structure.querySelector('[value="intraday"]').disabled = daily;
            if (daily && structure.value === 'intraday') structure.value = 'auto';
            get('preset').disabled = acceptance || weekly;
            get('library').inert = acceptance || weekly;
            get('weekly-availability').hidden = !weekly;
            get('zones').hidden = weekly;
            get('levels').hidden = weekly;
            for (const input of layerFields) {
                if (['sessions', 'support', 'resistance'].includes(input.dataset.chartLayer)) input.disabled = weekly;
                if (input.dataset.chartLayer === 'forming') input.disabled = acceptance || daily || weekly;
            }
            const showDaily = !weekly && (daily || structure.value === 'daily');
            get('lookback-control').hidden = !showDaily;
            const context = weekly ? 'weekly' : showDaily ? get('lookback').value : structure.value;
            get('context-label').textContent = weekly ? '' : showDaily
                ? '1d structure · ' + horizonLabels[context] + ' · Current confirmed zones'
                : '15m structure · Current confirmed zones';
            get('context-label').hidden = weekly;
            if (quickIntervalHost) {
                for (const btn of quickIntervalHost.querySelectorAll('[data-quick-interval]')) {
                    btn.setAttribute('aria-pressed', String(btn.dataset.quickInterval === interval));
                }
            }
            return context;
        }

        // Use the active controls to start one observation, keeping the current view until a requested reload succeeds.
        function startSelection(reset = true, reloadEarlier = false) {
            if (acceptance && acceptanceStarted) {
                void stop();
                onAcceptanceEnded?.('The acceptance chart selection changed. The observation has ended.');
                return;
            }
            if (acceptance) acceptanceStarted = true;
            const closed = stop();
            const request = new AbortController();
            privateReady = false;
            privateLayer?.reset();
            const interval = get('interval').value;
            const profile = interval === '1w' ? '' : studies.tokens().sort().join(',');
            let profileChanged = !reset && snapshot?.seriesKey.indicators !== profile;
            active = request;
            if (reset) resetSelection();
            else {
                paging = false;
                if (reloadEarlier) {
                    get('history-reload').disabled = true;
                    get('history-status').textContent = 'Reloading earlier history links…';
                }
                streamStatus = profileChanged ? 'Updating indicators…' : 'Reconnecting chart stream…';
                get('retry').hidden = true;
                refreshStatus();
            }
            const context = configureInterval(interval);
            renderForming(null);
            if (!root.dataset.symbol) {
                stop = () => closed;
                get('empty').textContent = 'Select a stock to open its chart.';
                setStatus('unavailable', 'No stock selected');
                if (plot) plot.setData({ bars: [], zones: [], levels: [], events: [], basisBreaks: [] });
                return;
            }

            key = { symbol: root.dataset.symbol, interval, sessionView: get('session').value,
                context, indicators: profile };
            refreshPager();
            updateFullLink();
            if (!plotting || !window.LightweightCharts || !transport) {
                stop = () => closed;
                get('empty').textContent = 'Chart assets could not load. Reload this page to retry.';
                setStatus('unavailable', 'Chart unavailable');
                if (acceptance) onAcceptanceEnded?.('Chart assets could not load.');
                return;
            }
            if (!plot) {
                plot = plotting.createPlot(get('plot'), start => {
                    // Layout and programmatic crosshair updates must not replace a keyboard or prior selection.
                    if (!pointerMoved) return;
                    pointerMoved = false;
                    if (start !== selected) inspect(start);
                }, privateLayer ? updatePrivateWindow : undefined);
                if (pendingTradePlan) plot.setTradePlanLevels(pendingTradePlan);
            }
            if (reset) {
                plot.setData({ bars: [], zones: [], levels: [], events: [], basisBreaks: [], interval });
                plot.setLayers(layers, hiddenLevels, hiddenZones);
                setStatus('loading', 'Loading completed candles…');
            }
            const watchOptions = { formingPreview: Boolean(layers.forming && ['1m', '5m', '15m'].includes(interval)) };
            if (acceptance) {
                watchOptions.acceptanceCorrelation = acceptanceCorrelation;
                acceptanceCorrelation = null;
            }
            stop = transport.watch(paths, key, watchOptions, request, {
                // Apply a fresh baseline only while its exact selection is mounted.
                snapshot(received, live) {
                    if (active !== request || request.signal.aborted) return;
                    if (reloadEarlier && (received.bars.length || received.evidence.weekly?.evaluatedFromUtc)) {
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
                    if (profileChanged || (snapshot && !sameBasis(snapshot.evidence, received.evidence))) {
                        if (olderPages.length && profileChanged) get('history-status').textContent = 'Load earlier candles again for the selected indicators.';
                        olderPages = [];
                        historyExpired = false;
                        profileChanged = false;
                    }
                    const followNewest = currentBars.at(-1)?.startMs === selected;
                    snapshot = received;
                    privateReady = true;
                    if (followNewest) selected = null;
                    if (live) {
                        streamStatus = '';
                        get('retry').hidden = true;
                    }
                    refreshStatus();
                    paint(false);
                },
                // Preview frames never trigger a baseline paint or replace the selected completed candle.
                preview(value) {
                    if (active === request && !request.signal.aborted) renderForming(value);
                },
                // Display connection failures without erasing valid candles or implying a live quote.
                status(text, retrying) {
                    if (active !== request || request.signal.aborted) return;
                    privateReady = false;
                    privateLayer?.reset();
                    renderForming(null, formingLabels.not_streaming);
                    streamStatus = text;
                    get('retry').hidden = retrying;
                    if (reloadEarlier && !retrying) {
                        get('history-reload').disabled = false;
                        get('history-status').textContent = 'Could not reload earlier history. Try again shortly.';
                    }
                    if (!snapshot) get('empty').textContent = text;
                    refreshStatus();
                    if (acceptance && !retrying) onAcceptanceEnded?.(text);
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
                const unit = key.interval === '1w' ? 'weeks' : 'candles';
                get('history-status').textContent = count > 0 ? count + ' earlier ' + unit + ' loaded' : 'No earlier ' + unit + ' in this page';
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

        // Forwards advisory server Entry/Stop/Target reference levels to the active plot (or queues them until plot creation) and clears on null.
        function setTradePlanLevels(plan) {
            pendingTradePlan = plan || null;
            plot?.setTradePlanLevels(pendingTradePlan);
        }

        // Replace only the symbol, preserving the operator's interval and layer settings.
        function setSymbol(symbol) {
            const normalized = (symbol || '').trim().toUpperCase().replaceAll('.', '-');
            if (root.dataset.symbol === normalized && active) return;
            details.close();
            setTradePlanLevels(null);
            if (privateLayer && normalized !== privateContext.symbol) {
                privateLayer.dispose();
                privateLayer = null;
                plot?.setPrivateEvents([]);
            }
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
            if (key.interval === '1w') url.searchParams.delete('indicators');
            else url.searchParams.set('indicators', key.indicators);
            url.searchParams.set('preset', get('preset').value);
            full.href = url.href;
            full.setAttribute('aria-label', 'Open full chart for ' + key.symbol);
        }

        // Store only the active browser display preferences without changing the workspace or stock.
        function savePreferences() {
            if (acceptance) return;
            indicatorLibrary.remember({
                ...Object.fromEntries(Object.keys(defaults).map(name => [name, get(name).value])),
                session: preservedSession ?? get('session').value,
                preset: get('preset').value, indicators: studies.tokens(), layers
            });
        }

        // Restore supported selections, allowing an explicit full chart URL to override browser defaults.
        function restorePreferences(settings, query = new URLSearchParams()) {
            const context = query.get('chartContext');
            preservedSession = undefined;
            const interval = query.get('interval');
            const params = { interval, session: interval === '1w' ? null : query.get('sessionView') };
            if (Object.hasOwn(horizonLabels, context)) {
                params.structure = 'daily';
                params.lookback = context;
            } else if (context === 'intraday' || context === 'auto') params.structure = context;
            const effectiveDefaults = defaultInterval ? { ...defaults, interval: defaultInterval } : defaults;
            for (const [name, fallback] of Object.entries(effectiveDefaults)) {
                const select = get(name);
                const value = params[name] ?? settings[name];
                select.value = [...select.options].some(option => option.value === value) ? value : fallback;
            }
            for (const checkbox of layerFields) {
                const name = checkbox.dataset.chartLayer;
                checkbox.checked = typeof settings.layers?.[name] === 'boolean' ? settings.layers[name] : checkbox.defaultChecked;
                layers[name] = checkbox.checked;
            }
            const indicators = query.has('indicators')
                ? query.get('indicators').split(',')
                : Array.isArray(settings.indicators)
                    ? settings.indicators
                    : (Array.isArray(defaultStudies) ? defaultStudies : undefined);
            studies.restore(indicators, query.get('preset') ?? settings.preset);
        }

        const studies = indicatorLibrary.mount(get, on, element, timestamp, preset => {
            const intervalChanged = key?.interval !== get('interval').value;
            if (preset && preset !== 'custom') get('settings-announcement').textContent =
                (preset === 'swing' ? 'Swing' : 'Intraday') + ' preset selected. Candle interval ' + get('interval').selectedOptions[0].textContent + '.';
            if (intervalChanged || key?.indicators !== studies.tokens().sort().join(',')) {
                plot?.clearStudies();
                inspect(selected);
                startSelection(!snapshot || intervalChanged);
            }
            savePreferences();
        }, pane => {
            shownPane = pane;
            plot?.setShownPane(pane);
        });

        if (acceptance) {
            root.dataset.symbol = 'AAPL';
            restorePreferences({ interval: '1m', session: 'regular', indicators: [], preset: 'custom', layers: { forming: true } });
            // A diagnostic observes one fixed watch. Normal chart preferences remain untouched.
            for (const node of root.querySelectorAll('.stock-chart-controls select, .stock-chart-study-toolbar button, .stock-chart-study-toolbar select, [data-chart-layer="forming"], [data-chart-reset], [data-chart-retry]')) node.disabled = true;
        } else restorePreferences(indicatorLibrary.preferences(), new URLSearchParams(root.closest('.stock-chart-page') ? location.search : ''));
        for (const picker of Object.keys(defaults)) on(get(picker), 'change', () => {
            if (picker === 'interval') studies.intervalChanged();
            startSelection();
            savePreferences();
        });
        if (quickIntervalHost) {
            // Syncs compact Desk 1d/1w/5m quick buttons with the canonical interval select and saved preferences.
            on(quickIntervalHost, 'click', event => {
                const button = event.target.closest('[data-quick-interval]');
                if (!button || acceptance) return;
                const next = button.dataset.quickInterval;
                const select = get('interval');
                if (!select || select.value === next) return;
                select.value = next;
                studies.intervalChanged();
                startSelection();
                savePreferences();
            });
        }
        on(get('reset'), 'click', () => {
            if (acceptance) return;
            indicatorLibrary.remember(null);
            restorePreferences({});
            get('study-feedback').textContent = '';
            get('settings-announcement').textContent = 'Chart settings reset.';
            if (root.closest('.stock-chart-page')) {
                const url = new URL(location.href);
                for (const parameter of ['interval', 'sessionView', 'chartContext', 'indicators', 'preset']) url.searchParams.delete(parameter);
                window.history.replaceState(window.history.state, '', url);
            }
            hiddenZones.clear();
            hiddenLevels.clear();
            startSelection();
        });
        on(get('retry'), 'click', () => startSelection(!snapshot));
        on(get('older'), 'click', loadEarlier);
        on(get('history-reload'), 'click', () => startSelection(false, true));
        // Let the browser manage keyboard focus, Escape and return to the details button.
        on(get('details-open'), 'click', () => details.showModal());
        // Return to the chart's live edge and keep the newly inspected latest candle selected.
        on(get('latest'), 'click', () => {
            pointerMoved = false;
            plot.latest();
            inspect(currentBars.at(-1).startMs, true);
        });
        on(get('prev'), 'click', () => inspectIndex(currentBars.findIndex(bar => bar.startMs === selected) - 1));
        on(get('next'), 'click', () => inspectIndex(currentBars.findIndex(bar => bar.startMs === selected) + 1));
        // Only an actual pointer input in the plot can select a candle through its crosshair.
        for (const event of ['pointerdown', 'pointermove']) on(get('plot'), event, () => { pointerMoved = true; });
        on(get('plot'), 'pointerleave', () => { pointerMoved = false; });
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
            // The forming switch replaces the watch options while retaining the completed selection and earlier pages.
            const layer = checkbox.dataset.chartLayer;
            layers[layer] = checkbox.checked;
            if (layer === 'forming') {
                savePreferences();
                startSelection(!snapshot);
                return;
            }
            plot?.setLayers(layers, hiddenLevels, hiddenZones);
            renderZoneKey(snapshot?.zones || []);
            refreshSource();
            savePreferences();
        });
        const sourceAge = setInterval(refreshSource, 15_000);
        if (root.dataset.symbol) startSelection();
        return {
            setSymbol,
            setTradePlanLevels,
            // Close the details and release network work and canvases when the chart host is absent.
            dispose() {
                details.close();
                pendingTradePlan = null;
                privateReady = false;
                privateLayer?.dispose();
                controller.abort();
                clearInterval(sourceAge);
                const result = stop();
                active = null;
                plot?.dispose();
                for (let index = relocatedNodes.length - 1; index >= 0; index--) {
                    const { node, parent, nextSibling } = relocatedNodes[index];
                    if (parent) parent.insertBefore(node, nextSibling && nextSibling.parentNode === parent ? nextSibling : null);
                }
                relocatedNodes.length = 0;
                for (const wrapper of root.querySelectorAll('.stock-chart-more-nav, .stock-chart-more-controls')) wrapper.remove();
                delete root.dataset.compactDesk;
                if (summaryHost) {
                    const head = root.querySelector('.stock-chart-head');
                    head.append(...summaryHost.childNodes);
                    head.hidden = false;
                }
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
