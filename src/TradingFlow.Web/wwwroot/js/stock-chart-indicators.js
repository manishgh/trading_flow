(() => {
    'use strict';

    const catalog = {
        sma20: { name: 'SMA 20', group: 'Trend', display: 'price', daily: true, unit: 'USD', description: 'Arithmetic mean of the last 20 completed closes.' },
        sma50: { name: 'SMA 50', group: 'Trend', display: 'price', daily: true, unit: 'USD', description: 'Arithmetic mean of the last 50 completed closes.' },
        sma200: { name: 'SMA 200', group: 'Trend', display: 'price', daily: true, unit: 'USD', description: 'Arithmetic mean of the last 200 completed closes.' },
        ema9: { name: 'EMA 9', group: 'Trend', display: 'price', daily: true, unit: 'USD', description: 'Exponential average of completed closes with a 9-bar period.' },
        ema20: { name: 'EMA 20', group: 'Trend', display: 'price', daily: true, unit: 'USD', description: 'Exponential average of completed closes with a 20-bar period.' },
        ema50: { name: 'EMA 50', group: 'Trend', display: 'price', daily: true, unit: 'USD', description: 'Exponential average of completed closes with a 50-bar period.' },
        ema200: { name: 'EMA 200', group: 'Trend', display: 'price', daily: true, unit: 'USD', description: 'Exponential average of completed closes with a 200-bar period.' },
        rsi14: { name: 'RSI 14', group: 'Momentum', display: 'pane', unit: '0–100', description: 'Relative strength of recent gains and losses over a 14-bar period. Reference lines at 30 and 70.' },
        macd: { name: 'MACD 12/26/9', group: 'Momentum', display: 'pane', unit: 'USD', description: 'Difference between the 12- and 26-period EMAs, its 9-period signal, and their difference.' },
        adx14: { name: 'ADX 14 with +DI/−DI', group: 'Momentum', display: 'pane', unit: '0–100', description: 'Trend strength and positive and negative directional movement over a 14-bar period.' },
        atr14: { name: 'ATR 14', group: 'Volatility', display: 'pane', readout: true, unit: 'USD', description: 'Smoothed true range over a 14-bar period, including price gaps.' },
        atrpct14: { name: 'ATR percent 14', group: 'Volatility', display: 'pane', readout: true, unit: '%', description: 'ATR 14 divided by the closing price, expressed as a percentage.' },
        bb20: { name: 'Bollinger Bands 20/2', group: 'Volatility', display: 'price', unit: 'USD', description: 'SMA 20 with upper and lower bands two standard deviations from the average.' },
        vwap: { name: 'RTH VWAP', group: 'Volume', display: 'price', sourceInterval: '1m', unit: 'USD', description: 'Volume-weighted average price that resets each New York regular session. Intraday only.' },
        obv: { name: 'OBV', group: 'Volume', display: 'pane', unit: 'shares', description: 'Cumulative volume, signed by the change in consecutive closing prices. Each independently anchored segment starts from a new zero origin.' },
        rvol: { name: 'Same-time RVOL', group: 'Volume', display: 'readout', sourceInterval: '1m', unit: '×', description: 'Cumulative regular-session volume divided by the median at the same completed time in 20 verified prior sessions.' }
    };
    const presets = {
        intraday: ['vwap', 'ema20', 'atr14:readout'],
        swing: ['sma50:1d', 'sma200:1d', 'rsi14', 'atr14:readout']
    };
    const limits = { price: 4, pane: 2 };
    const statusLabels = { ready: '', partial: 'Partial history', unavailable: 'Unavailable', retry: 'Retry available' };
    const columnLabels = { value: '', lower: 'Lower', middle: 'Middle', upper: 'Upper', macd: 'MACD', signal: 'Signal', histogram: 'Histogram', adx: 'ADX', pdi: '+DI', mdi: '−DI' };
    const strokePatterns = ['solid', 'dashed', 'dotted', 'dash-dot'];
    const format = new Intl.NumberFormat(undefined, { maximumFractionDigits: 4 });
    const dollarFormat = new Intl.NumberFormat(undefined, { style: 'currency', currency: 'USD', minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const subDollarFormat = new Intl.NumberFormat(undefined, { style: 'currency', currency: 'USD', minimumFractionDigits: 2, maximumFractionDigits: 4 });
    const mobileQuery = '(max-width: 600px)';
    const preferenceKey = 'tradingflow.stock-chart';
    // Settings saved before the key was renamed; read once and replaced on the next save.
    const earlierPreferenceKey = 'tradingflow.stock-chart.v3';

    // Decode only supported names and options so browser preferences cannot expand the server allowlist.
    function study(token) {
        if (typeof token !== 'string') return null;
        const [id, option, extra] = token.split(':');
        const entry = Object.hasOwn(catalog, id) && catalog[id];
        if (!entry || extra !== undefined || (option !== undefined && !(option === '1d' && entry.daily) && !(option === 'readout' && entry.readout))) return null;
        return { ...entry, formula: id, token, source: option === '1d' ? '1d' : '', display: option === 'readout' ? 'readout' : entry.display };
    }

    // Treat pane versus readout as the same selection for a given formula and source.
    function sameStudy(left, right, interval) {
        return left.formula === right.formula && (left.source || interval) === (right.source || interval);
    }

    // Validate the whole display budget when restoring preferences or opening a shared URL.
    function normalize(tokens, interval) {
        if (!Array.isArray(tokens) || tokens.length > 9) return [];
        const selected = [];
        for (const token of tokens) {
            const next = study(token);
            if (!next) continue;
            const duplicate = selected.findIndex(item => sameStudy(item, next, interval));
            if (duplicate >= 0) {
                if (next.source === '1d') selected[duplicate] = next;
                continue;
            }
            if (selected.filter(item => item.display === next.display).length >= (limits[next.display] ?? Infinity)) continue;
            selected.push(next);
        }
        return selected.map(item => item.token);
    }

    // Read browser-local settings when storage is allowed and keep the chart usable in private contexts.
    function preferences() {
        try {
            const stored = JSON.parse(localStorage.getItem(preferenceKey) ?? localStorage.getItem(earlierPreferenceKey));
            return stored && typeof stored === 'object' ? stored : {};
        } catch {
            return {};
        }
    }

    // Save or reset only the chart's own versioned settings.
    function remember(settings) {
        try {
            if (settings) localStorage.setItem(preferenceKey, JSON.stringify(settings));
            else localStorage.removeItem(preferenceKey);
            localStorage.removeItem(earlierPreferenceKey);
        } catch { /* Browser storage can be disabled. */ }
    }

    // Separate observed history may have a different revision or seed, but never a different formula or price basis.
    function compatibleCalculation(one, two) {
        return ['sourceInterval', 'sessionView', 'adjustmentPolicy', 'formulaVersion', 'parameters']
            .every(key => one[key] === two[key]) && one.columns.join() === two.columns.join();
    }

    // Give candles and studies identical page ownership: newer observed gaps remain gaps instead of being backfilled.
    function pageRanges(latest, older) {
        // A gap in weekly history still owns its evaluated page interval.
        const startOf = page => Date.parse(page.evidence?.weekly?.evaluatedFromUtc || page.bars[0]?.startUtc);
        let boundary = startOf(latest);
        if (!Number.isFinite(boundary)) boundary = Infinity;
        const ranges = [{ page: latest, before: Infinity, historical: false }];
        for (const page of [...older].sort((a, b) => startOf(b) - startOf(a))) {
            ranges.push({ page, before: boundary, historical: true });
            const start = startOf(page);
            if (Number.isFinite(start)) boundary = Math.min(boundary, start);
        }
        return ranges;
    }

    // Keep samples anchored to the exact page bars; never carry stale overlaps or fill missing values.
    function joinedStudies(latest, older, tokens) {
        return tokens.flatMap((token, slot) => {
            const meta = latest.indicators.find(item => item.id === token);
            if (!meta) return [];
            const points = new Map();
            let earlierUnavailable = false;
            let separateHistory = false;
            // Join a page's compact column samples by the corresponding visible candle time.
            function add(page, series, before = Infinity, historySegment = false) {
                if (!series.samples.length) return false;
                const starts = new Map(series.segmentStarts.map((index, offset) => [index, series.segmentAnchorsUtc?.[offset] ?? null]));
                let runIndex = 0;
                let anchorUtc = series.anchorUtc;
                let firstPoint = true;
                for (let index = 0; index < page.bars.length; index++) {
                    const start = Date.parse(page.bars[index].startUtc);
                    if (start >= before) continue;
                    if (starts.has(index)) anchorUtc = starts.get(index);
                    const values = series.samples.map(column => column[index]);
                    if (values.every(value => value === null)) continue;
                    let run = series.availabilityRuns[runIndex];
                    while (run && index >= run[0] + run[1]) run = series.availabilityRuns[++runIndex];
                    const availableAtUtc = run && index >= run[0] ? new Date(run[2]).toISOString() : page.bars[index].availableAtUtc;
                    points.set(start, { values, availableAtUtc, anchorUtc,
                        sourceRevision: series.sourceRevision, reconstructed: series.reconstructed,
                        observedAtUtc: page.asOfUtc, historySegment,
                        segmentStart: firstPoint || starts.has(index) });
                    firstPoint = false;
                }
                return !firstPoint;
            }
            // Visit newest first and clip each earlier page at its newer neighbour, even when that neighbour has gaps.
            for (const { page, before } of pageRanges(latest, older).slice(1)) {
                const series = page.indicators.find(item => item.id === token);
                const sameBasis = ['provider', 'feed', 'adjustmentPolicy'].every(field => page.evidence[field] === latest.evidence[field]);
                if (series && sameBasis && compatibleCalculation(meta, series)) {
                    separateHistory = add(page, series, before, true) || separateHistory;
                }
                else earlierUnavailable = true;
            }
            add(latest, meta);
            return [{ ...study(token), ...meta, points, earlierUnavailable, separateHistory, slot, chartInterval: latest.seriesKey.interval }];
        });
    }

    // Display readable names for the study's individual plotted or inspected components.
    function componentName(name) {
        return columnLabels[name] || name;
    }

    // Combine the measurement with an explicit unit; dollar values use the same "$1,234.56" format as the Desk price pills.
    function measurement(value, unit) {
        if (unit === 'USD') return (Math.abs(value) < 1 ? subDollarFormat : dollarFormat).format(value);
        const number = format.format(value);
        return unit === '%' || unit === '×' ? number + unit : unit === 'shares' ? number + ' ' + unit : number;
    }

    // Mount selection controls and keep page/plot owners responsible for transport and candle navigation.
    function mount(get, on, element, timestamp, changed, paneChanged) {
        const media = matchMedia(mobileQuery);
        let selected = [];
        let current = [];
        let mobilePane = '';
        let panePinned = false;
        let selectionInterval;

        // Add one accessible option for each fixed indicator under its measurement group.
        function fillCatalog() {
            const groups = new Map();
            for (const [id, entry] of Object.entries(catalog)) {
                if (!groups.has(entry.group)) {
                    const group = element('optgroup');
                    group.label = entry.group;
                    groups.set(entry.group, group);
                }
                groups.get(entry.group).append(new Option(entry.name, id));
            }
            get('study-choice').replaceChildren(...groups.values());
        }

        // Build the allowed token directly from the formula's available source and presentation choices.
        function proposed() {
            const id = get('study-choice').value;
            const entry = catalog[id];
            const option = entry.daily ? get('study-source').value : entry.readout ? get('study-display').value : '';
            return study(option ? id + ':' + option : id);
        }

        // Name a study and its concrete source interval in the current candle view.
        function title(item, interval = get('interval').value) {
            return item.name + ' · ' + (item.sourceInterval || item.source || interval);
        }

        // Show the existing study to replace when its family is already selected or the pane limit is full.
        function refreshPicker() {
            if (get('interval').value === '1w') return;
            const next = proposed();
            const picked = selected.map(study);
            const equivalent = picked.find(item => sameStudy(item, next, get('interval').value));
            const sameDisplay = picked.filter(item => item.display === next.display);
            const noChange = equivalent?.display === next.display;
            const replacements = noChange ? [] : sameDisplay.length >= (limits[next.display] ?? Infinity)
                ? sameDisplay : equivalent ? [equivalent] : [];
            get('study-source-control').hidden = !next.daily;
            get('study-display-control').hidden = !next.readout;
            get('study-replace-control').hidden = !replacements.length;
            get('study-replace').replaceChildren(...replacements.map(item => new Option(title(item), item.token)));
            get('study-add').disabled = noChange;
            get('study-add').textContent = noChange ? 'Already selected' : replacements.length ? 'Replace indicator' : 'Add indicator';
            const limit = replacements.length && !equivalent ? (next.display === 'pane' ? ' · Two-pane limit reached.' : ' · Four-price-study limit reached.') : '';
            get('study-description').textContent = next.description + ' Units ' + next.unit + limit;
        }

        // Reflect the mobile pane selection even if a study has too little evidence to plot.
        function refreshPanes() {
            if (get('interval').value === '1w') {
                get('pane-control').hidden = true;
                paneChanged(null);
                return;
            }
            const panes = selected.map(study).filter(item => item.display === 'pane');
            if (!panePinned || !panes.some(item => item.token === mobilePane)) {
                mobilePane = panes.find(item => current.find(series => series.token === item.token)?.points.size)?.token || panes[0]?.token || '';
            }
            get('pane-control').hidden = !media.matches || panes.length < 2;
            get('pane-choice').replaceChildren(...panes.map(item => new Option(title(item), item.token)));
            get('pane-choice').value = mobilePane;
            paneChanged(media.matches ? mobilePane : null);
        }

        // Render stable focus and expanded evidence across subsequent live chart snapshots.
        function render() {
            const list = get('studies');
            if (get('interval').value === '1w') {
                list.hidden = true;
                refreshPanes();
                return;
            }
            const focused = document.activeElement?.closest('[data-chart-study-row]');
            const focusToken = focused?.dataset.chartStudyRow;
            const focusRemove = document.activeElement?.matches('[data-chart-study-remove]');
            const expanded = new Set([...list.querySelectorAll('details[open]')].map(detail => detail.parentElement.dataset.chartStudyRow));
            list.replaceChildren(...selected.map((token, slot) => {
                const row = element('li');
                row.dataset.chartStudyRow = token;
                row.dataset.studySlot = slot;
                const item = current.find(series => series.token === token) || study(token);
                const detail = element('details');
                detail.open = expanded.has(token);
                detail.append(element('summary', title(item)), element('p', item.description));
                const remove = element('button', '×', 'stock-chart-study-remove');
                remove.type = 'button';
                remove.dataset.chartStudyRemove = token;
                remove.setAttribute('aria-label', 'Remove ' + title(item));
                const reason = statusLabels[item.status] ?? 'Waiting for chart data';
                if (item.reason) {
                    row.title = item.reason;
                    detail.append(element('p', item.reason));
                }
                if (reason) row.append(element('span', reason, 'stock-chart-study-state'));
                if (item.earlierUnavailable) detail.append(element('p', 'Some earlier pages have incompatible or missing study evidence. Reload earlier history to refresh those values.'));
                if (item.separateHistory) detail.append(element('p', 'Earlier pages are separate observed calculation segments. Lines break at page boundaries; inspect a candle for that segment’s revision, receipt and seed.'));
                if (item.formulaVersion) {
                    detail.append(element('small', [item.sessionView, item.adjustmentPolicy, item.formulaVersion,
                        'Units ' + item.unit, item.reconstructed ? 'Reconstructed from retrieved history' : ''].filter(Boolean).join(' · ')));
                    if (item.parameters) detail.append(element('small', 'Parameters ' + item.parameters));
                    if (item.warmupObservations) detail.append(element('small', 'Initial value after ' + item.seedObservations +
                        ' completed observations · Plot after ' + item.warmupObservations));
                    if (item.cutoffUtc) detail.append(element('small', 'Source cutoff ' + timestamp(item.cutoffUtc)));
                    if (item.anchorUtc) detail.append(element('small', 'Latest segment anchor ' + timestamp(item.anchorUtc)));
                    if (item.availableAtUtc) detail.append(element('small', 'Latest value available ' + timestamp(item.availableAtUtc)));
                    if (item.baselineSessions) detail.append(element('small', 'Required baseline ' + item.baselineSessions + ' prior sessions'));
                    if (item.baselineSampleCount != null) detail.append(element('small', 'Matching baseline sessions ' + item.baselineSampleCount));
                    if (item.segmentStarts.length > 1) detail.append(element('small', 'Line breaks mark missing evidence, a price-basis change or a separate calculation segment.'));
                    if (item.columns.length > 1) detail.append(element('small', item.columns.map((column, index) =>
                        componentName(column) + ' ' + (column === 'histogram' ? 'bars' : strokePatterns[(slot + index) % 4])).join(' · ')));
                }
                row.append(detail, remove);
                return row;
            }));
            list.hidden = !selected.length;
            if (focusToken) {
                const row = [...list.children].find(item => item.dataset.chartStudyRow === focusToken);
                (row?.querySelector(focusRemove ? 'button' : 'summary') || get('library').querySelector('summary')).focus({ preventScroll: true });
            }
            refreshPanes();
        }

        // Apply a custom library edit and start just one new observation for the complete selection.
        function custom(tokens, message) {
            get('preset').value = 'custom';
            selected = tokens;
            current = [];
            get('study-feedback').textContent = message;
            refreshPicker();
            render();
            changed();
        }

        // Restore finite settings and preserve the visible selection order independently of the wire order.
        function restore(tokens, preset) {
            const interval = get('interval').value;
            selectionInterval = interval === '1w' ? undefined : interval;
            selected = normalize(tokens, interval);
            current = [];
            mobilePane = '';
            panePinned = false;
            const matchesInterval = interval === '1w' || (preset === 'swing' ? interval === '1d' : interval !== '1d');
            const hasPreset = Object.hasOwn(presets, preset) && matchesInterval && [...selected].sort().join() === [...presets[preset]].sort().join();
            if (hasPreset) selected = [...presets[preset]];
            get('preset').value = hasPreset ? preset : 'custom';
            refreshPicker();
            render();
        }

        // Present all matched readings at the same selected candle, with no placeholder numeric value for gaps.
        function readouts(start) {
            return current.map(item => {
                const point = item.points.get(start);
                const value = point ? point.values.flatMap((value, index) => {
                    if (value === null) return [];
                    const column = item.columns[index] === 'value' ? '' : componentName(item.columns[index]) + ' ';
                    return column + measurement(value, item.unit);
                }).join(' · ') : 'Unavailable for this candle';
                return [title(item), value, point?.availableAtUtc, point?.anchorUtc,
                    point?.sourceRevision, point?.reconstructed, point?.observedAtUtc, point?.historySegment,
                    item.formula, item.slot, item.token];
            });
        }

        fillCatalog();
        get('study-display').value = 'readout';
        for (const key of ['study-choice', 'study-source', 'study-display']) on(get(key), 'change', refreshPicker);
        on(get('study-add'), 'click', () => {
            const next = proposed();
            const replace = get('study-replace').value;
            const tokens = selected.filter(token => token !== replace && !sameStudy(study(token), next, get('interval').value));
            custom([...tokens, next.token], (replace ? 'Selected ' : 'Added ') + title(next) + '.');
        });
        on(get('studies'), 'click', event => {
            const token = event.target.closest('[data-chart-study-remove]')?.dataset.chartStudyRemove;
            if (token) custom(selected.filter(item => item !== token), 'Removed ' + title(study(token)) + '.');
        });
        on(get('preset'), 'change', () => {
            const preset = get('preset').value;
            const interval = get('interval').value;
            if (preset === 'swing') get('interval').value = '1d';
            if (preset === 'intraday' && interval === '1d') get('interval').value = '5m';
            if (presets[preset]) restore(presets[preset], preset);
            changed(preset);
        });
        on(get('pane-choice'), 'change', () => {
            mobilePane = get('pane-choice').value;
            panePinned = true;
            paneChanged(mobilePane);
        });
        on(media, 'change', refreshPanes);
        refreshPicker();

        return {
            restore,
            readouts,
            // Suspend the existing studies on weekly candles and restore their calculations on return.
            intervalChanged() {
                const interval = get('interval').value;
                if (interval !== '1w' && interval !== selectionInterval) restore(selected, get('preset').value);
                else {
                    refreshPicker();
                    render();
                }
            },
            // Return chart tokens in selection order for browser preferences and visible colors.
            tokens: () => [...selected],
            // Update status and evidence only from the currently accepted chart response.
            update(series) {
                current = series;
                refreshPicker();
                render();
            }
        };
    }

    window.TradingFlowStockChartIndicators = { mount, joinedStudies, pageRanges, preferences, remember, normalize };
})();
