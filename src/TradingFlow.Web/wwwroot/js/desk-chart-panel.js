/* Desk chart column: the mounted chart, its indicator pills and the advisory trade plan lines. Display only. */
'use strict';
window.createDeskChartPanel = ({ el, getSelectedRow, selectedStrategyChoice, spreadDetail }) => {
    const { money, fixedMoney, when, normalizeChartSymbol } = window.TradingFlowDeskFormat;
    let deskChart, chartSymbol, deskChartZones = [], deskChartZonesSymbol = null, deskChartSeriesInfo = '', deskHighlightedOverlay = null;
    let deskStudies = [], deskStudyInterval = '1d';
    let tradePlanController = null, tradePlanSeq = 0, tradePlanKey = '', tradePlanPrice = null, tradePlanRequestedAt = 0;
    // The chart's plan is requested again when the quote has moved this far from its price, at most this often.
    const tradePlanMoveShare = 0.005, tradePlanMinimumGapMs = 30000;
    const deskDefaultChartStudies = ['ema21:1d', 'sma50:1d', 'sma200:1d'];

    // Returns the current server-observed reference price (quote midpoint or previous close) for support/resistance labeling.
    function deskReferencePrice(row) {
        const mid = row?.quote?.midPrice;
        if (typeof mid === 'number' && Number.isFinite(mid) && mid > 0) return mid;
        const prev = row?.previousClose?.close;
        return typeof prev === 'number' && Number.isFinite(prev) && prev > 0 ? prev : null;
    }
    // Receives the chart's selected indicators with their latest values; other symbols' late answers are ignored.
    function handleDeskStudies(studies, symbol, interval) {
        if (normalizeChartSymbol(symbol) !== normalizeChartSymbol(chartSymbol)) return;
        deskStudies = studies;
        deskStudyInterval = interval;
        renderStudyPills(getSelectedRow());
    }
    // Shows one pill per indicator selected in the chart, in its line colour; pills keep their focus while values change.
    function renderStudyPills(row) {
        const host = el('study-pills');
        if (!host) return;
        const signature = deskStudyInterval + '|' + deskStudies.map(item => item.token).join(',');
        if (host.dataset.signature !== signature) {
            host.dataset.signature = signature;
            host.replaceChildren(...(deskStudyInterval === '1w' ? [weeklyNote()] : deskStudies.map(createStudyPill)));
        }
        if (deskStudyInterval === '1w') return;
        const reference = deskReferencePrice(row);
        deskStudies.forEach((item, index) => updateStudyPill(host.children[index], item, reference));
    }
    // Says why no indicator is shown on weekly candles.
    function weeklyNote() {
        const note = document.createElement('span');
        note.className = 'watch-fine';
        note.textContent = 'Indicators are shown on daily and intraday candles.';
        return note;
    }
    // Builds a focusable pill whose key is the selection token used for highlighting on the chart.
    function createStudyPill(item) {
        const pill = document.createElement('span');
        pill.className = 'watch-ind-pill';
        pill.setAttribute('role', 'button');
        pill.tabIndex = 0;
        pill.dataset.highlightKey = item.token;
        pill.setAttribute('aria-pressed', String(deskHighlightedOverlay === item.token));
        pill.dataset.highlighted = String(deskHighlightedOverlay === item.token);
        pill.style.setProperty('--ind-swatch', 'var(--stock-chart-study-' + item.slot % 5 + ')');
        return pill;
    }
    // Writes the value and, for a line on the price scale, whether it lies below (support) or above (resistance) the price.
    function updateStudyPill(pill, item, reference) {
        const name = item.label + (item.interval === '1d' ? ' day' : ' ' + item.interval);
        const onPrice = item.onPrice && typeof item.value === 'number' && reference != null;
        const role = onPrice ? (reference >= item.value ? 'support' : 'resistance') : '';
        if (role) pill.dataset.role = role; else delete pill.dataset.role;
        const missing = item.reason || item.state || 'no value';
        pill.textContent = item.text
            ? name + ' ' + (role === 'resistance' ? '↑ ' : role === 'support' ? '↓ ' : '') + item.text
            : name + ' — ' + missing;
        const tip = [name + ' ' + (item.text ? item.text + ' at the latest candle' : missing), role === 'resistance' ? 'resistance (above price)' : role === 'support' ? 'support (below price)' : '',
            item.text && item.reason ? item.reason : '', 'Click to highlight on chart'].filter(Boolean).join(' · ');
        pill.title = tip;
        pill.dataset.tooltip = tip;
    }
    // Selects the nearest confirmed server support zone (preferring currentOnly and zones at or below the reference price).
    function selectTodaySupportZone(zones, refPrice) {
        const supports = (zones || []).filter(zone =>
            zone && zone.type === 'support' && Number.isFinite(zone.lower) && Number.isFinite(zone.upper) &&
            zone.lower > 0 && zone.upper >= zone.lower);
        if (!supports.length) return null;
        const pool = supports.some(zone => zone.currentOnly) ? supports.filter(zone => zone.currentOnly) : supports;
        if (refPrice == null) return pool.reduce((best, zone) => zone.upper > best.upper ? zone : best, pool[0]);
        const inBand = pool.filter(zone => zone.isPriceInBand || (zone.lower <= refPrice && refPrice <= zone.upper));
        if (inBand.length) return inBand.reduce((best, zone) => zone.upper > best.upper ? zone : best, inBand[0]);
        const below = pool.filter(zone => zone.upper <= refPrice);
        if (below.length) return below.reduce((best, zone) => zone.upper > best.upper ? zone : best, below[0]);
        return pool.reduce((best, zone) =>
            Math.abs(zone.lower - refPrice) < Math.abs(best.lower - refPrice) ? zone : best, pool[0]);
    }
    // Renders the minimal-word Today support pill from confirmed server chart zones with instant hover tooltip.
    function renderTodaySupportPill(row) {
        const node = el('today-support');
        if (!node) return;
        const symbolKey = normalizeChartSymbol(row?.membership?.symbol);
        const zone = symbolKey && deskChartZonesSymbol === symbolKey
            ? selectTodaySupportZone(deskChartZones, deskReferencePrice(row))
            : null;
        if (!zone) {
            node.textContent = 'Today support —';
            delete node.dataset.role;
            const emptyTip = 'No confirmed support zone for this view · Click to highlight support bands on chart';
            node.title = emptyTip;
            node.dataset.tooltip = emptyTip;
            return;
        }
        const band = zone.upper - zone.lower >= 0.05
            ? fixedMoney(zone.lower) + '–' + fixedMoney(zone.upper)
            : fixedMoney(zone.upper);
        node.dataset.role = 'support';
        node.textContent = 'Today support ' + band;
        const tip = [zone.currentOnly ? 'Current zone' : 'Historical zone',
            zone.sourceTimeframe ? zone.sourceTimeframe + ' pivots' : '',
            zone.effectiveAtUtc ? 'Inputs available ' + when(zone.effectiveAtUtc) : '',
            'Click to highlight on chart'].filter(Boolean).join(' · ');
        node.title = tip;
        node.dataset.tooltip = tip;
    }
    // Formats the completed daily ATR pill to 2 decimals using server values only with instant hover tooltip.
    function renderAtrPill(value, detail) {
        const node = el('atr');
        if (!node) return;
        const tip = ['14-session daily Average True Range (completed sessions)', detail].filter(Boolean).join(' · ');
        node.title = tip;
        node.dataset.tooltip = tip;
        node.textContent = typeof value === 'number' && Number.isFinite(value) && value >= 0
            ? 'ATR ' + fixedMoney(value)
            : 'ATR —';
    }
    // Formats completed daily 1D/1W/1M high-low volatility percentages from server observation with plain-English tooltip.
    function renderVolatilityPill(row) {
        const node = el('volatility');
        if (!node) return;
        const trusted = ['ready', 'insufficient'].includes(row?.observation?.state);
        const vol = trusted ? row?.observation?.volatility : null;
        const pct = value => typeof value === 'number' && Number.isFinite(value) && value >= 0
            ? value.toFixed(1) + '%'
            : '—';
        node.textContent = 'Range 1D ' + pct(vol?.dailyRangePct) + ' · 1W ' + pct(vol?.weekRangePct) + ' · 1M ' + pct(vol?.monthRangePct);
        const volTip = 'Price range as a share of the close, (high − low) ÷ close: 1D = last completed session, 1W = last 5 sessions, 1M = last 21 sessions';
        node.title = volTip;
        node.dataset.tooltip = volTip;
    }
    // Syncs active indicator highlight state across the Desk indicator pills without triggering redundant chart redraws.
    function syncDeskHighlightUi(key) {
        deskHighlightedOverlay = key ? String(key).toLowerCase() : null;
        const container = el('technicals-block');
        if (!container) return;
        for (const pill of container.querySelectorAll('[data-highlight-key]')) {
            const active = pill.dataset.highlightKey === deskHighlightedOverlay;
            pill.dataset.highlighted = String(active);
            pill.setAttribute('aria-pressed', String(active));
        }
    }
    // Toggles interactive chart highlighting for a clicked SMA or support pill and forwards it to the mounted chart.
    function toggleDeskHighlight(key) {
        const normalized = key ? String(key).toLowerCase() : null;
        const next = deskHighlightedOverlay === normalized ? null : normalized;
        syncDeskHighlightUi(next);
        deskChart?.setHighlightedOverlay?.(next);
    }
    // Receives visible server-computed zones from the mounted chart and updates Today support only for the active symbol.
    function handleDeskChartZones(zones, symbol) {
        const normalized = normalizeChartSymbol(symbol);
        const activeSymbol = normalizeChartSymbol(chartSymbol);
        if (normalized && activeSymbol && normalized !== activeSymbol) return;
        deskChartZones = Array.isArray(zones) ? zones : [];
        deskChartZonesSymbol = normalized || null;
        const row = getSelectedRow();
        renderTodaySupportPill(row);
    }
    // Combines bid/ask/spread quote details with the chart's completed-series note on the compact focus ⓘ info badge.
    function updateFocusInfoTooltip(row) {
        const info = el('focus-info');
        if (!info) return;
        const quoteParts = row ? [
            spreadDetail(row),
            'Provider time ' + when(row.quote?.timestamp) + (row.quote?.receivedAtUtc ? ' · received ' + when(row.quote.receivedAtUtc) : '')
        ] : [];
        info.title = [...quoteParts, deskChartSeriesInfo].filter(Boolean).join('\n');
    }
    // Captures compact series/feed status updates from the mounted chart so Desk keeps them inside the ⓘ tooltip.
    function handleDeskChartSeriesInfo(infoText) {
        deskChartSeriesInfo = infoText || '';
        const row = getSelectedRow();
        updateFocusInfoTooltip(row);
    }
    // Start history only for a selected stock and ignore quote ticks that keep the same selection.
    function syncChart(symbol) {
        if (chartSymbol === symbol) return;
        chartSymbol = symbol;
        deskChartZones = [];
        deskChartZonesSymbol = null;
        deskChartSeriesInfo = '';
        syncDeskHighlightUi(null);
        deskStudies = [];
        renderStudyPills(null);
        renderTodaySupportPill(null);
        if (symbol && !deskChart) {
            const root = el('focus-content').querySelector('[data-stock-chart]');
            if (root) deskChart = window.TradingFlowStockChart?.mount(root, {
                summaryHost: el('chart-summary'),
                defaultInterval: '1d',
                defaultStudies: deskDefaultChartStudies,
                onZones: handleDeskChartZones,
                compactDesk: true,
                controlsHost: el('chart-controls-slot'),
                quickIntervalHost: el('interval-quick'),
                onSeriesInfo: handleDeskChartSeriesInfo,
                onHighlightChange: syncDeskHighlightUi,
                onStudies: handleDeskStudies
            });
        }
        deskChart?.setSymbol(symbol);
        window.dispatchEvent(new CustomEvent('tradingflow:desk-symbol-selected', { detail: { symbol: symbol || null } }));
    }
    // Renders the read-only server bracket plan summary inside the Col 3 ticket dock without client-side risk math.
    function renderTicketPlanSummary(plan) {
        const node = el('ticket-plan-summary');
        if (!node) return;
        if (!plan) {
            node.textContent = '';
            node.title = '';
            node.hidden = true;
            return;
        }
        const parts = [
            typeof plan.riskBudget === 'number' && Number.isFinite(plan.riskBudget) ? '1R budget ' + money(plan.riskBudget) : '',
            typeof plan.limit === 'number' && Number.isFinite(plan.limit) ? (plan.direction === 'short' ? 'Short at bid ' : 'Buy at ask ') + fixedMoney(plan.limit) : '',
            typeof plan.stop === 'number' && Number.isFinite(plan.stop) ? 'Stop ' + fixedMoney(plan.stop) : '',
            typeof plan.target === 'number' && Number.isFinite(plan.target) ? 'Target ' + fixedMoney(plan.target) : ''
        ].filter(Boolean);
        node.textContent = parts.join(' · ');
        node.title = [plan.stopNote, plan.targetNote].filter(Boolean).join(' · ');
        node.hidden = parts.length === 0;
    }
    // Aborts any in-flight advisory trade-plan lookup, invalidates stale responses, and removes Entry/Stop/Target chart lines.
    function clearChartTradePlan() {
        tradePlanSeq++;
        tradePlanKey = '';
        tradePlanPrice = null;
        tradePlanRequestedAt = 0;
        if (tradePlanController) {
            tradePlanController.abort();
            tradePlanController = null;
        }
        deskChart?.setTradePlanLevels(null);
        renderTicketPlanSummary(null);
    }

    // The price a plan would be made at now: a short strategy sells to enter, so the bid; a long strategy buys at the ask.
    function tradePlanReferencePrice(row, choice) {
        const isShort = String(choice.direction || '').toLowerCase() === 'short';
        const price = isShort ? row?.quote?.bidPrice : row?.quote?.askPrice;
        return typeof price === 'number' && Number.isFinite(price) && price > 0 ? price : null;
    }
    // Removes a plan that could not be renewed and forgets its price, so the next quote after the minimum gap asks again.
    function dropTradePlan() {
        tradePlanPrice = null;
        deskChart?.setTradePlanLevels(null);
        renderTicketPlanSummary(null);
    }
    // True when the quote has moved far enough from the planned price, and long enough after the last request, to plan again.
    function tradePlanOutdated(price) {
        if (price === null || Date.now() - tradePlanRequestedAt < tradePlanMinimumGapMs) return false;
        return tradePlanPrice === null || Math.abs(price - tradePlanPrice) / tradePlanPrice >= tradePlanMoveShare;
    }
    // Requests read-only server strategy reference levels (Entry/Stop/Target) for the selected stock and strategy, and again
    // as the quote moves; the old lines stay until the new plan arrives. Fails closed on change or error with zero client math.
    async function syncChartTradePlan(row) {
        const choice = selectedStrategyChoice();
        const symbol = row?.membership?.symbol || null;
        if (!symbol || !choice?.strategy) {
            if (tradePlanKey || tradePlanController) clearChartTradePlan();
            return;
        }
        const nextKey = JSON.stringify({ symbol, strategy: choice.strategy });
        const price = tradePlanReferencePrice(row, choice);
        if (nextKey !== tradePlanKey) clearChartTradePlan();
        else if (!tradePlanOutdated(price)) return;
        else tradePlanController?.abort();
        tradePlanKey = nextKey;
        tradePlanPrice = price;
        tradePlanRequestedAt = Date.now();
        const controller = new AbortController();
        tradePlanController = controller;
        const seq = ++tradePlanSeq;
        try {
            const response = await window.tradingFlowFetch('/api/v1/orders/plan', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    contractVersion: '1.0',
                    symbol,
                    strategy: choice.strategy,
                    limitPrice: price
                }),
                signal: controller.signal
            });
            if (seq !== tradePlanSeq || controller.signal.aborted) return;
            if (!response.ok) {
                dropTradePlan();
                return;
            }
            const plan = await response.json();
            if (seq !== tradePlanSeq || controller.signal.aborted) return;
            if (plan?.stop == null && plan?.target == null) {
                deskChart?.setTradePlanLevels(null);
                renderTicketPlanSummary(null);
                return;
            }
            deskChart?.setTradePlanLevels({
                limit: plan?.limit ?? null,
                stop: plan?.stop ?? null,
                target: plan?.target ?? null,
                direction: plan?.direction === 'short' ? 'short' : 'long'
            });
            renderTicketPlanSummary(plan);
        } catch (error) {
            if (error?.name === 'AbortError') return;
            if (seq === tradePlanSeq) dropTradePlan();
        } finally {
            if (tradePlanController === controller) tradePlanController = null;
        }
    }

    // Releases the chart when the page is left.
    function disposeChart() {
        void deskChart?.dispose();
        deskChart = null;
        chartSymbol = null;
        deskChartZones = [];
        deskChartZonesSymbol = null;
        const root = el('focus-content').querySelector('[data-stock-chart]');
        if (root) root.dataset.symbol = '';
    }

    return { renderStudyPills, renderTodaySupportPill, renderAtrPill, renderVolatilityPill, toggleDeskHighlight,
        updateFocusInfoTooltip, syncChart, clearChartTradePlan, syncChartTradePlan, disposeChart };
};
