(() => {
    'use strict';

    // Indicator families. "period" holds [smallest, largest, default]; a family without it has fixed parameters.
    // "ownInterval" families may be calculated on a longer interval than the candles.
    const families = {
        sma: { label: 'SMA', group: 'Trend', display: 'price', unit: 'USD', period: [2, 250, 50], ownInterval: true, spare: [200, 20, 100],
            description: period => `Arithmetic mean of the last ${period} completed closes.` },
        ema: { label: 'EMA', group: 'Trend', display: 'price', unit: 'USD', period: [2, 250, 21], ownInterval: true, spare: [9, 50, 200],
            description: period => `Exponential average of completed closes with a ${period}-bar period.` +
                (period > 107 ? ' Its first values still carry part of the starting average.' : '') },
        bb: { label: 'Bollinger Bands', group: 'Volatility', display: 'price', unit: 'USD', period: [5, 100, 20], ownInterval: true, suffix: '/2',
            description: period => `SMA ${period} with upper and lower bands two standard deviations from the average.` },
        vwap: { label: 'RTH VWAP', group: 'Volume', display: 'price', unit: 'USD', sourceInterval: '1m',
            description: () => 'Volume-weighted average price that resets each New York regular session. Intraday only.' },
        rsi: { label: 'RSI', group: 'Momentum', display: 'pane', unit: '0–100', period: [2, 50, 14], ownInterval: true,
            description: period => `Relative strength of recent gains and losses over a ${period}-bar period. Reference lines at 30 and 70.` },
        macd: { label: 'MACD 12/26/9', group: 'Momentum', display: 'pane', unit: 'USD', ownInterval: true,
            description: () => 'Difference between the 12- and 26-period EMAs, its 9-period signal, and their difference.' },
        adx: { label: 'ADX', group: 'Momentum', display: 'pane', unit: '0–100', period: [2, 40, 14], ownInterval: true, suffix: ' with +DI/−DI',
            description: period => `Trend strength and positive and negative directional movement over a ${period}-bar period.` },
        atr: { label: 'ATR', group: 'Volatility', display: 'pane', unit: 'USD', period: [2, 50, 14], ownInterval: true, readout: true,
            description: period => `Smoothed true range over a ${period}-bar period, including price gaps.` },
        atrpct: { label: 'ATR percent', group: 'Volatility', display: 'pane', unit: '%', period: [2, 50, 14], ownInterval: true, readout: true,
            description: period => `ATR ${period} divided by the closing price, expressed as a percentage.` },
        obv: { label: 'OBV', group: 'Volume', display: 'pane', unit: 'shares',
            description: () => 'Cumulative volume, signed by the change in consecutive closing prices. Each independently anchored segment starts from a new zero origin.' },
        rvol: { label: 'Same-time RVOL', group: 'Volume', display: 'readout', unit: '×', sourceInterval: '1m',
            description: () => 'Cumulative regular-session volume divided by the median at the same completed time in 20 verified prior sessions.' }
    };
    // The same intervals the server accepts for an indicator, with their length in minutes and the name shown.
    const intervals = { '5m': [5, '5m'], '15m': [15, '15m'], '1h': [60, '1h'], '1d': [1440, 'Day'] };
    const presets = {
        intraday: ['vwap', 'ema20', 'atr14:readout'],
        swing: ['sma50:1d', 'sma200:1d', 'rsi14', 'atr14:readout']
    };
    const maximumStudies = 5;
    const maximumPanes = 2;
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

    // Decode only supported families, periods and options, so browser preferences cannot expand the server allowlist.
    function study(token) {
        if (typeof token !== 'string') return null;
        const [formula, ...options] = token.split(':');
        const match = /^([a-z]+)([1-9]\d{0,2})?$/.exec(formula);
        const entry = match && Object.hasOwn(families, match[1]) ? families[match[1]] : null;
        if (!entry || Boolean(entry.period) !== Boolean(match[2])) return null;
        const period = match[2] ? Number(match[2]) : null;
        if (period !== null && (period < entry.period[0] || period > entry.period[1])) return null;
        const readout = options.at(-1) === 'readout';
        if (readout) options.pop();
        const source = options[0] ?? '';
        if (readout && !entry.readout || options.length > 1 ||
            source && (!entry.ownInterval || !Object.hasOwn(intervals, source))) return null;
        return { family: match[1], period, formula, token, source, unit: entry.unit, sourceInterval: entry.sourceInterval,
            name: entry.label + (period === null ? '' : ' ' + period + (entry.suffix || '')),
            description: entry.description(period), display: readout ? 'readout' : entry.display };
    }

    // Writes the token of one family with its period, own interval and display.
    function tokenOf(family, period, source, readout) {
        return family + (period ?? '') + (source ? ':' + source : '') + (readout ? ':readout' : '');
    }

    // The interval a study is calculated on: its own when that is longer than the candles, else the candles.
    function effectiveInterval(item, interval) {
        if (item.sourceInterval) return item.sourceInterval;
        return item.source && minutesOf(item.source) > minutesOf(interval) ? item.source : interval;
    }

    // Candle length in minutes; a one-minute candle is the shortest.
    function minutesOf(interval) { return intervals[interval]?.[0] ?? 1; }

    // Treat pane versus readout as the same selection for a given formula and calculation interval.
    function sameStudy(left, right, interval) {
        return left.formula === right.formula && effectiveInterval(left, interval) === effectiveInterval(right, interval);
    }

    // Explains why a study cannot join a selection, or returns an empty string when it can.
    function refusal(next, others, interval) {
        if (others.some(item => sameStudy(item, next, interval))) return next.name + ' is already selected for this interval.';
        if (others.length >= maximumStudies) return 'A chart holds at most ' + maximumStudies + ' indicators. Remove one first.';
        if (next.display === 'pane' && others.filter(item => item.display === 'pane').length >= maximumPanes)
            return 'A chart holds at most ' + maximumPanes + ' indicators in their own pane.';
        return '';
    }

    // Validate the whole selection when restoring preferences or opening a shared URL; what does not fit is left out.
    function normalize(tokens, interval) {
        if (!Array.isArray(tokens)) return [];
        const selected = [];
        for (const token of tokens.slice(0, 32)) {
            const next = study(token);
            if (next && !refusal(next, selected, interval)) selected.push(next);
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
            return [{ ...study(token), ...meta, points, earlierUnavailable, separateHistory, slot, chartInterval: latest.seriesKey.interval,
                lastStart: Date.parse(latest.bars.at(-1)?.startUtc) }];
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
        // The list the user chose. It is kept while a candle interval makes two of its entries equal, so switching back restores it.
        let chosen = [];
        let current = [];
        let mobilePane = '';
        let panePinned = false;
        let selectionInterval;
        // The selection and candle interval the rows were built for; live updates only change texts inside them.
        let drawnFor = '';

        // Offer every family under its measurement group.
        function familyOptions() {
            const groups = new Map();
            for (const [id, entry] of Object.entries(families)) {
                if (!groups.has(entry.group)) {
                    const group = element('optgroup');
                    group.label = entry.group;
                    groups.set(entry.group, group);
                }
                groups.get(entry.group).append(new Option(entry.label, id));
            }
            return [...groups.values()];
        }

        // Name a study and its concrete source interval in the current candle view.
        function title(item, interval = get('interval').value) {
            return item.name + ' · ' + effectiveInterval(item, interval);
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

        // Show how many indicators are selected and block adding beyond the limit.
        function refreshPicker() {
            get('study-count').textContent = selected.length + ' of ' + maximumStudies;
            get('study-add').disabled = selected.length >= maximumStudies;
        }

        // Build a labelled edit control that shares one change handler.
        function control(tag, field, label) {
            const node = element(tag, null, 'input');
            node.dataset.studyEdit = field;
            node.setAttribute('aria-label', label);
            return node;
        }

        // Build the edit controls of one selected indicator: type, period, interval, display, latest value and removal.
        function buildRow(token, slot) {
            const item = study(token);
            const entry = families[item.family];
            const row = element('li');
            row.dataset.chartStudyRow = token;
            row.dataset.studySlot = slot;
            const type = control('select', 'family', 'Indicator type');
            type.append(...familyOptions());
            type.value = item.family;
            const period = control('input', 'period', item.name + ' period');
            period.type = 'number';
            period.hidden = !entry.period;
            if (entry.period) {
                [period.min, period.max] = entry.period;
                period.step = 1;
                period.value = item.period;
            }
            const interval = control('select', 'source', item.name + ' interval');
            const candles = get('interval').value;
            const longer = Object.entries(intervals).filter(([, [minutes]]) => minutes > minutesOf(candles));
            interval.append(new Option('Chart · ' + (intervals[candles]?.[1] ?? candles), ''), ...longer.map(([id, [, name]]) => new Option(name, id)));
            interval.value = longer.some(([id]) => id === item.source) ? item.source : '';
            interval.hidden = !entry.ownInterval;
            interval.disabled = !longer.length;
            const display = control('select', 'display', item.name + ' display');
            display.append(new Option('Value only', 'readout'), new Option('Own pane', ''));
            display.value = item.display === 'readout' ? 'readout' : '';
            display.hidden = !entry.readout;
            const value = element('span', null, 'stock-chart-study-value');
            const state = element('span', null, 'stock-chart-study-state');
            const detail = element('details');
            detail.append(element('summary', 'Details'), element('div'));
            const remove = element('button', '×', 'stock-chart-study-remove');
            remove.type = 'button';
            remove.dataset.chartStudyRemove = token;
            remove.setAttribute('aria-label', 'Remove ' + title(item));
            row.append(type, period, interval, display, value, state, detail, remove);
            return row;
        }

        // Values of one series at the newest candle, or null when that candle has none; an older value is never called the latest.
        function latestValues(item) {
            return item?.points?.get(item.lastStart)?.values ?? null;
        }

        // Write component values with their units; empty when a value is missing.
        function measured(values, item) {
            return (values ?? []).flatMap((value, index) => {
                if (value === null) return [];
                const column = item.columns[index] === 'value' ? '' : componentName(item.columns[index]) + ' ';
                return column + measurement(value, item.unit);
            }).join(' · ');
        }

        // Update the latest value, the state and the evidence of one row without touching its controls.
        function updateRow(row) {
            const token = row.dataset.chartStudyRow;
            const item = current.find(series => series.token === token) || study(token);
            row.title = item.reason || '';
            row.querySelector('.stock-chart-study-value').textContent = measured(latestValues(item), item);
            row.querySelector('.stock-chart-study-state').textContent = statusLabels[item.status] ?? 'Waiting for chart data';
            const slot = Number(row.dataset.studySlot);
            const lines = [element('p', item.description)];
            if (item.reason) lines.push(element('p', item.reason));
            if (item.earlierUnavailable) lines.push(element('p', 'Some earlier pages have incompatible or missing study evidence. Reload earlier history to refresh those values.'));
            if (item.separateHistory) lines.push(element('p', 'Earlier pages are separate observed calculation segments. Lines break at page boundaries; inspect a candle for that segment’s revision, receipt and observation time.'));
            if (item.formulaVersion) {
                lines.push(element('small', [item.sessionView, item.adjustmentPolicy, item.formulaVersion,
                    'Units ' + item.unit, item.reconstructed ? 'Reconstructed from retrieved history' : ''].filter(Boolean).join(' · ')));
                if (item.parameters) lines.push(element('small', 'Parameters ' + item.parameters));
                if (item.warmupObservations) lines.push(element('small', 'Initial value after ' + item.seedObservations +
                    ' completed observations · Plot after ' + item.warmupObservations));
                if (item.cutoffUtc) lines.push(element('small', 'Source cutoff ' + timestamp(item.cutoffUtc)));
                if (item.anchorUtc) lines.push(element('small', 'Latest segment anchor ' + timestamp(item.anchorUtc)));
                if (item.availableAtUtc) lines.push(element('small', 'Latest value available ' + timestamp(item.availableAtUtc)));
                if (item.baselineSessions) lines.push(element('small', 'Required baseline ' + item.baselineSessions + ' prior sessions'));
                if (item.baselineSampleCount != null) lines.push(element('small', 'Matching baseline sessions ' + item.baselineSampleCount));
                if (item.segmentStarts.length > 1) lines.push(element('small', 'Line breaks mark missing evidence, a price-basis change or a separate calculation segment.'));
                if (item.columns.length > 1) lines.push(element('small', item.columns.map((column, index) =>
                    componentName(column) + ' ' + (column === 'histogram' ? 'bars' : strokePatterns[(slot + index) % 4])).join(' · ')));
            }
            row.querySelector('details > div').replaceChildren(...lines);
        }

        // Rebuild the rows only when the selection or candle interval changed, so live updates never interrupt typing.
        function render() {
            const list = get('studies');
            const interval = get('interval').value;
            if (interval === '1w') {
                list.hidden = true;
                refreshPanes();
                return;
            }
            const signature = interval + '|' + selected.join(',');
            if (signature !== drawnFor) {
                const active = document.activeElement;
                const focusedRow = list.contains(active) ? active.closest('[data-chart-study-row]') : null;
                const focusedField = active?.dataset.studyEdit;
                const focusedSlot = Number(focusedRow?.dataset.studySlot);
                const open = new Set([...list.querySelectorAll('details[open]')].map(node => node.closest('li').dataset.studySlot));
                list.replaceChildren(...selected.map(buildRow));
                for (const row of list.children) row.querySelector('details').open = open.has(row.dataset.studySlot);
                drawnFor = signature;
                if (focusedRow) {
                    const row = list.children[Math.min(focusedSlot, list.children.length - 1)];
                    const field = row && focusedField ? row.querySelector('[data-study-edit="' + focusedField + '"]:not([hidden])') : null;
                    (field || row?.querySelector('button') || get('study-choice')).focus({ preventScroll: true });
                }
            }
            for (const row of list.children) updateRow(row);
            list.hidden = !selected.length;
            refreshPanes();
        }

        // Apply an edit and start just one new observation for the complete selection.
        function custom(tokens, message) {
            get('preset').value = 'custom';
            // Entries hidden on this candle interval stay in the chosen list while there is room for them.
            const hidden = chosen.filter(token => !selected.includes(token) && !tokens.includes(token));
            selected = tokens;
            chosen = normalize([...tokens, ...hidden], '1m');
            current = [];
            get('study-feedback').textContent = message;
            refreshPicker();
            render();
            changed();
        }

        // Explain a refused edit and put the row's controls back to the selected indicator.
        function refuse(message) {
            get('study-feedback').textContent = message;
            drawnFor = '';
            render();
        }

        // Turn one changed control into the row's new indicator; the position and therefore the colour stay.
        function editRow(row, field) {
            const slot = Number(row.dataset.studySlot);
            const item = study(selected[slot]);
            const read = name => row.querySelector('[data-study-edit="' + name + '"]').value;
            const family = read('family');
            const entry = families[family];
            const period = !entry.period ? null : field === 'family' ? entry.period[2] : Number(read('period'));
            if (entry.period && (!Number.isInteger(period) || period < entry.period[0] || period > entry.period[1])) {
                refuse(entry.label + ' needs a whole period from ' + entry.period[0] + ' to ' + entry.period[1] + '.');
                return;
            }
            const source = !entry.ownInterval ? '' : field === 'source' ? read('source') : item.source;
            const readout = entry.readout && (field === 'family' || read('display') === 'readout');
            const next = study(tokenOf(family, period, source, readout));
            if (!next || next.token === item.token) return;
            const others = selected.filter((_, index) => index !== slot).map(study);
            const problem = refusal(next, others, get('interval').value);
            if (problem) { refuse(problem); return; }
            custom(selected.map((token, index) => index === slot ? next.token : token), 'Changed to ' + title(next) + '.');
        }

        // Add the chosen family with its usual period, or the next usual one when that is already selected.
        function add() {
            const family = get('study-choice').value;
            const entry = families[family];
            const others = selected.map(study);
            const periods = entry.period ? [entry.period[2], ...(entry.spare ?? [])] : [null];
            let problem = '';
            for (const period of periods) {
                const next = study(tokenOf(family, period, '', entry.readout));
                problem = refusal(next, others, get('interval').value);
                if (!problem) { custom([...selected, next.token], 'Added ' + title(next) + '.'); return; }
            }
            get('study-feedback').textContent = problem;
        }

        // Restore finite settings and preserve the visible selection order independently of the wire order.
        function restore(tokens, preset) {
            const interval = get('interval').value;
            selectionInterval = interval === '1w' ? undefined : interval;
            // On one-minute candles every own interval is longer, so no two different entries are equal.
            chosen = normalize(tokens, '1m');
            selected = normalize(chosen, interval);
            current = [];
            mobilePane = '';
            panePinned = false;
            const matchesInterval = interval === '1w' || (preset === 'swing' ? interval === '1d' : interval !== '1d');
            const hasPreset = Object.hasOwn(presets, preset) && matchesInterval && [...selected].sort().join() === [...presets[preset]].sort().join();
            if (hasPreset) selected = chosen = [...presets[preset]];
            get('preset').value = hasPreset ? preset : 'custom';
            refreshPicker();
            render();
        }

        // Present all matched readings at the same selected candle, with no placeholder numeric value for gaps.
        function readouts(start) {
            return current.map(item => {
                const point = item.points.get(start);
                const value = point ? measured(point.values, item) : 'Unavailable for this candle';
                return [title(item), value, point?.availableAtUtc, point?.anchorUtc,
                    point?.sourceRevision, point?.reconstructed, point?.observedAtUtc, point?.historySegment,
                    item.formula, item.slot, item.token];
            });
        }

        // The selected indicators with their latest values, for a host that shows them outside the chart.
        function summary() {
            const interval = get('interval').value;
            if (interval === '1w') return [];
            return selected.map((token, slot) => {
                const item = current.find(series => series.token === token) || study(token);
                const values = latestValues(item);
                return { token, slot, label: item.name, interval: effectiveInterval(item, interval), onPrice: item.display === 'price',
                    value: values?.length === 1 ? values[0] : null, text: measured(values, item),
                    state: statusLabels[item.status] ?? 'Waiting for chart data', reason: item.reason || '' };
            });
        }

        get('study-choice').replaceChildren(...familyOptions());
        get('study-choice').value = 'ema';
        on(get('study-add'), 'click', add);
        on(get('studies'), 'click', event => {
            const token = event.target.closest('[data-chart-study-remove]')?.dataset.chartStudyRemove;
            if (token) custom(selected.filter(item => item !== token), 'Removed ' + title(study(token)) + '.');
        });
        on(get('studies'), 'change', event => {
            const field = event.target.dataset?.studyEdit;
            if (field) editRow(event.target.closest('[data-chart-study-row]'), field);
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
            summary,
            // Suspend the existing studies on weekly candles and restore their calculations on return.
            intervalChanged() {
                const interval = get('interval').value;
                if (interval !== '1w' && interval !== selectionInterval) restore(chosen, get('preset').value);
                else {
                    refreshPicker();
                    render();
                }
            },
            // Return chart tokens in selection order for browser preferences and visible colors.
            tokens: () => [...selected],
            // Return the complete chosen list for the saved preferences.
            chosen: () => [...chosen],
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
