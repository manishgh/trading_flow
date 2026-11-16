(() => {
    'use strict';
    const root = document.querySelector('[data-chart-acceptance]');
    if (!root) return;
    const get = name => root.querySelector('[data-acceptance-' + name + ']');
    const endpoint = '/api/operations/chart-acceptance';
    const activeStates = new Set(['observing', 'finalizing']);
    let chart, timer, deadlineTimer, pending = false, attempted = false, leaving = false, current, deferredStop;

    function finishRequest() {
        pending = false;
        controls();
        if (deferredStop && !leaving) {
            const { reason } = deferredStop;
            deferredStop = null;
            queueMicrotask(() => { void stop(reason); });
        }
    }

    async function request(suffix = '', method = 'GET', keepalive = false) {
        const response = await window.tradingFlowFetch(endpoint + suffix,
            { method, credentials: 'same-origin', cache: 'no-store', keepalive });
        const body = await response.json();
        if (!response.ok) throw new Error(body.detail || 'The chart check request could not be confirmed.');
        return body;
    }

    function controls() {
        get('start').disabled = pending || attempted || current?.state !== 'idle';
        get('stop').disabled = pending || !activeStates.has(current?.state);
        get('refresh').disabled = pending;
    }

    function disposeChart() {
        clearTimeout(deadlineTimer);
        if (chart) void chart.dispose();
        chart = null;
        get('chart').hidden = true;
    }

    function render(value) {
        current = value;
        const labels = { idle: 'No chart check is active.', observing: 'Observing AAPL · warm-up may still be in progress.',
            finalizing: 'Observation ended · finishing evidence files.', incomplete: 'Incomplete observation.',
            stopped: 'Observation stopped.', expired: 'Five-minute observation ended.' };
        get('status').textContent = (labels[value.state] || 'Check state unavailable.') +
            (value.reason ? ' ' + value.reason : '') +
            (value.state === 'observing' && !chart ? ' This tab has no attached acceptance watch; it will not reconnect automatically.' : '');
        get('evidence').textContent = [value.deadlineUtc ? 'Deadline (UTC): ' + value.deadlineUtc : '',
            value.watchAttached ? 'Chart watch attached.' : '', value.scopeRemovalRequested ? 'Scope removal requested.' : '',
            value.removalAcknowledged ? 'Provider removal acknowledged.' : '',
            value.resultPath ? 'Evidence reference: ' + value.resultPath + '. Requires receipt and display review; this is not an automatic pass.' : '']
            .filter(Boolean).join(' ');
        if (value.state !== 'observing') disposeChart();
        controls();
        clearTimeout(timer);
        if (!leaving && activeStates.has(value.state)) timer = setTimeout(refresh, 2000);
    }

    async function refresh() {
        if (pending || leaving) return;
        pending = true;
        controls();
        try { render(await request()); }
        catch (error) {
            get('status').textContent = error.message + ' Status is unknown. No start will be retried.';
            current = null;
            controls();
        }
        finally { finishRequest(); }
    }

    async function stop(reason) {
        if (leaving) return;
        if (pending) { deferredStop = { reason }; return; }
        pending = true;
        controls();
        // Let the server end capture before detaching its watch; premature detachment means incomplete evidence.
        try { render(await request('/stop', 'POST')); }
        catch (error) { get('status').textContent = (reason ? reason + ' ' : '') + error.message + ' Stop is unconfirmed; the server deadline still applies. Refresh status.'; }
        finally { finishRequest(); }
    }

    get('start').addEventListener('click', async () => {
        if (pending || attempted || current?.state !== 'idle') return;
        // An ambiguous POST can have succeeded. This tab must never issue a second Start.
        attempted = pending = true;
        clearTimeout(timer);
        controls();
        let result;
        try {
            result = await request('/start', 'POST');
            if (leaving) return;
            if (!result.correlation || result.state !== 'observing' || !Number.isFinite(Date.parse(result.deadlineUtc)))
                throw new Error('The accepted check did not provide a usable watch capability.');
            get('chart').hidden = false;
            chart = window.TradingFlowStockChart.mount(get('chart').querySelector('[data-stock-chart]'), {
                acceptanceCorrelation: result.correlation,
                onAcceptanceEnded: reason => { queueMicrotask(() => { void stop(reason); }); }
            });
            result.correlation = null;
            deadlineTimer = setTimeout(() => { void stop('The five-minute deadline was reached.'); },
                Math.max(0, Date.parse(result.deadlineUtc) - Date.now()));
            render(result);
        } catch (error) {
            disposeChart();
            get('status').textContent = error.message + ' Start will not be retried. Refresh status and stop any active observation.';
            current = null;
        } finally {
            if (result) result.correlation = null;
            finishRequest();
        }
    });
    get('stop').addEventListener('click', () => { void stop(); });
    get('refresh').addEventListener('click', () => { void refresh(); });
    window.addEventListener('pagehide', () => {
        leaving = true;
        clearTimeout(timer);
        disposeChart();
        // Best effort only: the server owns the absolute deadline if navigation interrupts CSRF or delivery.
        if (activeStates.has(current?.state)) void request('/stop', 'POST', true).catch(() => {});
    });
    window.addEventListener('pageshow', event => {
        if (!event.persisted) return;
        leaving = false;
        void refresh();
    });
    void refresh();
})();
