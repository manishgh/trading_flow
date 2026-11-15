(() => {
    'use strict';

    const maximumWindowMs = 31 * 24 * 60 * 60 * 1000;
    const unavailable = 'Paper chart evidence is unavailable. Open the selection again.';

    // Private journal text and prices never enter public chart storage or the price series.
    function node(tag, value, className) {
        const element = document.createElement(tag);
        if (value) element.textContent = value;
        if (className) element.className = className;
        return element;
    }

    // Keep the raw decimal string and execution identifiers independent from the displayed candle.
    function evidenceRow(event, timestamp) {
        const entry = node('li');
        entry.append(node('strong', event.label));
        const time = event.eventAtUtc ? timestamp(event.eventAtUtc, true) : 'Recorded time unavailable';
        entry.append(node('span', time, 'cell-sub'));
        if (event.endAtUtc) entry.append(node('span', 'Ended ' + timestamp(event.endAtUtc, true), 'cell-sub'));
        if (event.rawPrice != null)
            entry.append(node('span', 'Raw price $' + event.rawPrice + ' · Price overlay unavailable', 'cell-sub'));
        if (event.quantity != null) entry.append(node('span', event.quantity + ' shares', 'cell-sub'));
        if (event.executionId) entry.append(node('span', 'Execution ' + event.executionId, 'cell-sub'));
        entry.append(node('span', 'Order ' + event.clientOrderId, 'cell-sub'));
        if (event.reason) entry.append(node('span', event.reason, 'cell-sub'));
        return entry;
    }

    // Connect one optional private context to the shared chart's one native details dialog.
    function mount(root, context, setMarkers, timestamp) {
        const dialog = root.querySelector('[data-chart-details]');
        const toggles = dialog.querySelector('.stock-chart-toggles');
        const checkbox = node('input');
        checkbox.type = 'checkbox';
        checkbox.checked = true;
        const toggle = node('label');
        toggle.append(checkbox, document.createTextNode(' Paper order evidence'));
        toggles.append(toggle);

        const section = node('section', null, 'stock-chart-detail-section');
        const heading = node('h3', 'Paper order evidence');
        const status = node('p', null, 'stock-chart-note');
        status.setAttribute('role', 'status');
        const activeHeading = node('h4', 'Latest verified active levels');
        const levels = node('ul', null, 'stock-chart-detail-list');
        const rows = node('ul', null, 'stock-chart-detail-list');
        const more = node('button', 'Load more paper events', 'btn btn-secondary');
        const exact = node('button', 'Open the exact order chart', 'btn btn-secondary');
        more.type = exact.type = 'button';
        section.append(heading, status, activeHeading, levels, rows, more, exact);
        dialog.append(section);

        const lifetime = new AbortController();
        let token = context.token;
        context.token = null;
        let request, timer, windowKey, continuation, remembered;
        const events = new Map();
        const expires = Math.max(0, Date.parse(context.expiresAtUtc) - Date.parse(context.issuedAtUtc));

        // Cancel stale reads and remove their evidence before changing windows, contexts, or sign-in state.
        function clear({ expired = false } = {}) {
            request?.abort();
            request = null;
            clearTimeout(timer);
            windowKey = null;
            continuation = null;
            events.clear();
            levels.replaceChildren();
            rows.replaceChildren();
            activeHeading.hidden = levels.hidden = more.hidden = exact.hidden = true;
            setMarkers([]);
            if (expired) {
                token = null;
                checkbox.disabled = true;
            }
            status.textContent = token ? 'Select a completed chart window to read paper evidence.' : unavailable;
        }

        // Shared chart selection changes invalidate the previous viewport as well as its evidence.
        function reset(options) {
            remembered = null;
            clear(options);
        }

        // Apply only the response for the exact active request, with a fresh grant checked by the server.
        async function load(after) {
            if (!windowKey || !token || !checkbox.checked || document.hidden) return;
            request?.abort();
            const pending = new AbortController();
            request = pending;
            more.disabled = true;
            status.textContent = 'Reading local paper evidence…';
            try {
                const response = await window.tradingFlowFetch(context.endpoint, {
                    method: 'POST', cache: 'no-store', signal: pending.signal,
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ token, symbol: context.symbol, ...windowKey, continuation: after })
                });
                if (request !== pending || pending.signal.aborted) return;
                if ([401, 403, 404].includes(response.status)) { reset({ expired: true }); return; }
                if (!response.ok) throw new Error(response.status === 429
                    ? 'Too many chart requests. Wait briefly and retry.' : 'Paper evidence could not be read. Retry the selection.');
                const page = await response.json();
                if (request !== pending || pending.signal.aborted) return;
                if (!after) events.clear();
                for (const event of page.events) events.set(event.id, event);
                continuation = page.continuation;
                more.hidden = !continuation;
                exact.hidden = !page.incomplete || context.generationId == null;
                activeHeading.hidden = levels.hidden = page.activeLevels.length === 0;
                levels.replaceChildren(...page.activeLevels.map(event => evidenceRow(event, timestamp)));
                rows.replaceChildren(...[...events.values()].map(event => evidenceRow(event, timestamp)));
                const markers = new Map([...events.values(), ...page.activeLevels]
                    .filter(event => event.eventAtUtc)
                    .map(event => [event.id, { id: event.id, eventAtUtc: event.eventAtUtc }]));
                setMarkers([...markers.values()]);
                status.textContent = [
                    'Journal read ' + timestamp(page.readAtUtc, true),
                    page.detail,
                    page.truncated ? 'More paper events are available.' : '',
                    !events.size && !page.activeLevels.length && !page.incomplete ? 'No retained paper events in this window.' : ''
                ].filter(Boolean).join(' · ');
            } catch (error) {
                if (request !== pending || pending.signal.aborted) return;
                // Failed revalidation withdraws existing evidence rather than leaving old private markers visible.
                clear();
                status.textContent = error.message;
            } finally {
                if (request === pending) request = null;
                more.disabled = false;
            }
        }

        // Debounce viewport changes and ask the operator to narrow windows wider than 744 elapsed hours.
        function setWindow(range, reason) {
            remembered = { range, reason };
            if (!token || !checkbox.checked) return;
            const end = range && Math.min(range.toMs, range.asOfMs);
            const width = range && end - range.fromMs;
            if (!Number.isFinite(width) || width <= 0 || width > maximumWindowMs) {
                clear();
                status.textContent = reason === 'weekly' ? 'Paper evidence is available on daily and intraday charts.' :
                    'Zoom to a completed chart window of 31 days or less to see paper evidence.';
                return;
            }
            const selection = { fromUtc: new Date(range.fromMs).toISOString(), toUtc: new Date(end).toISOString() };
            if (windowKey?.fromUtc === selection.fromUtc && windowKey?.toUtc === selection.toUtc) return;
            clear();
            windowKey = selection;
            timer = setTimeout(() => void load(null), 250);
        }

        checkbox.addEventListener('change', () => {
            section.hidden = !checkbox.checked;
            clear();
            if (checkbox.checked && remembered) setWindow(remembered.range, remembered.reason);
        }, { signal: lifetime.signal });
        more.addEventListener('click', () => void load(continuation), { signal: lifetime.signal });
        exact.addEventListener('click', () => context.openExact(), { signal: lifetime.signal });
        document.addEventListener('visibilitychange', () => {
            clear();
            if (!document.hidden && remembered) setWindow(remembered.range, remembered.reason);
        }, { signal: lifetime.signal });
        const refresh = setInterval(() => {
            if (!request && remembered) {
                if (windowKey) void load(null);
                else setWindow(remembered.range, remembered.reason);
            }
        }, 45000);
        const expiry = setTimeout(() => reset({ expired: true }), expires);
        reset();
        return {
            setWindow,
            reset,
            // Remove transient UI, handlers and the capability when this chart is unmounted.
            dispose() {
                reset({ expired: true });
                lifetime.abort();
                clearTimeout(expiry);
                clearInterval(refresh);
                toggle.remove();
                section.remove();
            }
        };
    }

    window.TradingFlowPrivateChart = { mount };
    const workspace = document.querySelector('[data-paper-chart-host]');
    if (!workspace) return;
    const panel = workspace.querySelector('[data-paper-chart-panel]');
    const title = workspace.querySelector('[data-paper-chart-title]');
    const message = workspace.querySelector('[data-paper-chart-message]');
    const summary = workspace.querySelector('[data-paper-chart-selection]');
    const source = workspace.querySelector('[data-paper-chart-source]');
    const chart = workspace.querySelector('[data-stock-chart]');
    const full = chart.querySelector('[data-chart-full]');
    if (full) full.hidden = true;
    let selectionRequest, mounted, trigger;

    // Drop all in-memory private context before selecting a different intent or leaving the page.
    function close() {
        selectionRequest?.abort();
        selectionRequest = null;
        void mounted?.dispose();
        mounted = null;
        panel.hidden = true;
        chart.dataset.symbol = '';
        summary.textContent = '';
        source.removeAttribute('href');
        message.hidden = true;
    }

    // Only the originating Razor page can issue a context. It resolves the selection from durable local evidence.
    async function open(symbol, clientOrderId, button) {
        close();
        trigger = button;
        const pending = new AbortController();
        selectionRequest = pending;
        const url = new URL(workspace.dataset.selectionUrl, location.href);
        url.searchParams.set('symbol', symbol);
        if (clientOrderId) url.searchParams.set('clientOrderId', clientOrderId);
        message.textContent = 'Opening paper chart…';
        message.hidden = false;
        workspace.scrollIntoView({ block: 'start' });
        try {
            const response = await fetch(url, {
                headers: { 'X-TradingFlow-Chart': '1' }, credentials: 'same-origin', cache: 'no-store',
                signal: pending.signal, redirect: 'error'
            });
            if (!response.ok) throw new Error(response.status === 429 ? 'Too many chart requests. Wait briefly and retry.' : unavailable);
            const context = await response.json();
            if (selectionRequest !== pending || pending.signal.aborted) return;
            const sourceUrl = new URL(workspace.dataset.sourceUrl, location.href);
            sourceUrl.hash = context.generationId == null ? 'paper-order-' + context.clientOrderId : 'TradingPositions';
            source.href = sourceUrl.href;
            title.textContent = context.symbol + ' paper chart';
            summary.textContent = [context.profile, 'Run ' + context.runLabel,
                context.generationId == null ? 'Order ' + context.clientOrderId : 'Position generation ' + context.generationId
            ].join(' · ');
            chart.dataset.symbol = context.symbol;
            panel.hidden = false;
            message.hidden = true;
            const openExact = () => void open(symbol, context.clientOrderId, button);
            context.endpoint = workspace.dataset.evidenceUrl;
            context.openExact = openExact;
            mounted = window.TradingFlowStockChart.mount(chart, { privateContext: context });
            title.focus({ preventScroll: true });
        } catch (error) {
            if (selectionRequest !== pending || pending.signal.aborted) return;
            close();
            message.textContent = error.message;
            message.hidden = false;
        }
    }

    document.addEventListener('click', event => {
        const button = event.target.closest('[data-paper-chart-symbol]');
        if (button) void open(button.dataset.paperChartSymbol, button.dataset.paperChartOrder, button);
    });
    workspace.querySelector('[data-paper-chart-close]').addEventListener('click', () => {
        close();
        trigger?.focus();
    });
    window.addEventListener('pagehide', close);
})();
