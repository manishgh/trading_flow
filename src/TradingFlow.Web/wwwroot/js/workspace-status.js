// One bounded status read supplies every panel on the page; outdated observations cannot enable controls.
(() => {
    const strip = document.querySelector('[data-workspace-status]');
    if (!strip) return;
    const field = key => strip.querySelector('[data-workspace-' + key + ']');
    const paused = strip.dataset.tradingPaused === 'true';
    const restricted = paused
        ? 'Trading is paused. This host rejects order and position changes, including exits and protection changes.'
        : 'Trading status is unavailable. Retry before submitting an order or changing a position.';
    const unknown = {
        tradingRuntime: paused ? 'Trading paused' : 'Trading state unknown', marketSession: 'Market session unknown', marketFeeds: 'Market feed state unknown',
        openingRestriction: restricted, exitRestriction: restricted,
        preparationWorker: { state: 'unknown', detail: 'Preparation worker status is unknown. Review saved evidence and retry.' }
    };
    window.tradingFlowWorkspaceStatus = unknown;
    let inFlight = false;
    let lastRead;
    const time = value => value ? new Date(value).toLocaleString() : 'not observed';

    // Publish one observation for Desk and Preparation and expose the source's own timestamps.
    function show(status, message) {
        window.tradingFlowWorkspaceStatus = status;
        field('runtime').textContent = status.tradingRuntime;
        field('market').textContent = status.marketSession;
        field('feeds').textContent = status.marketFeeds;
        field('checked').textContent = message;
        field('orders').textContent = status.openingRestriction || 'Order actions still require the server’s individual authorization and market checks.';
        const sources = [
            ['Exchange calendar', time(status.marketObservedAtUtc)],
            ['Finviz earnings calendar', time(status.earnings?.lastCalendarRefreshUtc)],
            ['Earnings analysis', time(status.earnings?.lastAnalysisUtc)],
            ...((status.news?.sources || []).map(source => [source.source, source.state + ' · ' + time(source.attemptedAtUtc) + ' · ' + source.detail])),
            ['Stock quotes', 'Read per stock on Desk. Feed configuration does not establish quote freshness.']
        ];
        field('sources').replaceChildren(...sources.map(([name, detail]) => {
            const row = document.createElement('li');
            row.textContent = name + ' · ' + detail;
            return row;
        }));
        document.dispatchEvent(new CustomEvent('workspace-status-changed', { detail: status }));
    }

    // Do not overlap reads, and stop treating an old successful response as current after a failed read.
    async function refresh() {
        if (inFlight || document.hidden) return;
        inFlight = true;
        try {
            const summary = document.querySelector('[data-preparation-summary]') ? '?includePreparation=true' : '';
            const response = await window.tradingFlowFetch('/api/v1/operations/workspace' + summary, { signal: AbortSignal.timeout(5000) });
            if (!response.ok) throw new Error('status unavailable');
            const status = await response.json();
            lastRead = status.observedAtUtc;
            show(status, 'Checked ' + time(lastRead));
        } catch {
            show(unknown, 'Status read failed · last successful check ' + time(lastRead) + ' · retrying automatically');
        } finally {
            inFlight = false;
        }
    }
    refresh();
    setInterval(refresh, 30000);
    document.addEventListener('visibilitychange', () => {
        if (!document.hidden) {
            if (!lastRead || Date.now() - Date.parse(lastRead) > 40000) show(unknown, 'Status expired · refreshing…');
            refresh();
        }
    });
})();
