// Read-only Catalysts OPRA Implied Move vs 20d ADR% ratio cards for News and Earnings screens.
(() => {
    'use strict';

    const root = document.querySelector('[data-catalysts-opra]');
    if (!root) return;

    const statusEl = root.querySelector('[data-catalysts-opra-status]');
    const gridEl = root.querySelector('[data-catalysts-opra-grid]');
    const refreshBtn = root.querySelector('[data-catalysts-opra-refresh]');
    const pageKind = String(root.dataset.catalystsPage || '').toLowerCase();
    const apiFetch = typeof window.tradingFlowFetch === 'function' ? window.tradingFlowFetch : window.fetch.bind(window);
    const symbolPattern = /^[A-Z0-9.-]{1,12}$/;
    const fallbackSearchTickerPattern = /^[A-Z][A-Z0-9.-]{0,4}$/;

    let currentController = null;
    let requestSeq = 0;
    let activeSymbols = normalizeSymbols(String(root.dataset.catalystsSymbols || 'SPY,QQQ').split(','));
    let lastLoadedSignature = '';

    // Normalizes, validates, deduplicates, and caps a list of US equity symbols at 6 entries with SPY/QQQ fallback.
    function normalizeSymbols(candidates, includeBenchmarkFallback = true) {
        const result = [];
        const seen = new Set();
        const push = raw => {
            const cleaned = String(raw || '').trim().toUpperCase();
            if (!cleaned || !symbolPattern.test(cleaned) || seen.has(cleaned) || result.length >= 6) return;
            seen.add(cleaned);
            result.push(cleaned);
        };
        for (const item of candidates || []) push(item);
        if (includeBenchmarkFallback) {
            push('SPY');
            push('QQQ');
        }
        return result.length ? result : ['SPY', 'QQQ'];
    }

    // Classifies an Implied Move / 20d ADR ratio into its non-colour glyph, badge text, and semantic tone.
    function formatRatioPill(ratio) {
        if (typeof ratio !== 'number' || !Number.isFinite(ratio) || ratio <= 0) {
            return { text: '—', tone: 'muted', title: 'Ratio unavailable (requires OPRA implied move and 20 completed daily sessions)' };
        }
        const label = ratio.toFixed(2) + 'x';
        if (ratio < 1.2) return { text: '✓ ' + label, tone: 'good', title: 'Implied move under 1.2x 20d ADR' };
        if (ratio <= 2.0) return { text: '◐ ' + label, tone: 'warn', title: 'Elevated implied move (1.2x–2.0x 20d ADR)' };
        return { text: '▲ ' + label, tone: 'bad', title: 'Extreme implied move (>2.0x 20d ADR)' };
    }

    // Formats front-expiry OPRA implied move percentage and trading-day horizon without substituting zero for missing values.
    function formatImpliedMove(card) {
        if (typeof card?.frontImpliedMovePct !== 'number' || !Number.isFinite(card.frontImpliedMovePct)) return '—';
        const days = typeof card.tradingDaysToFrontExpiry === 'number' ? ` (${card.tradingDaysToFrontExpiry}d)` : '';
        return `±${card.frontImpliedMovePct.toFixed(1)}%${days}`;
    }

    // Formats completed-bar 20-session ADR% from the server payload.
    function formatAdr(value) {
        return typeof value === 'number' && Number.isFinite(value) && value > 0 ? value.toFixed(2) + '%' : '—';
    }

    // Formats 25-delta call/put volatility skew in volatility points (e.g. "+4.2 pts").
    function formatSkew(value) {
        if (typeof value !== 'number' || !Number.isFinite(value)) return '—';
        const pts = value * 100;
        return (pts >= 0 ? '+' : '\u2212') + Math.abs(pts).toFixed(1) + ' pts';
    }

    // Builds one DOM card node for a CatalystOptionsAdrCard using textContent only (no HTML string interpolation).
    function buildCardNode(card) {
        const article = document.createElement('article');
        article.className = 'catalysts-opra-card';

        const head = document.createElement('div');
        head.className = 'catalysts-opra-card-head';
        const sym = document.createElement('strong');
        sym.textContent = card.symbol || '—';

        const ratioInfo = formatRatioPill(card.impliedMoveToAdrRatio);
        const pill = document.createElement('span');
        pill.className = 'catalysts-opra-ratio-pill';
        pill.dataset.tone = ratioInfo.tone;
        pill.textContent = ratioInfo.text;
        pill.title = ratioInfo.title;
        head.append(sym, pill);

        const metrics = document.createElement('dl');
        metrics.className = 'catalysts-opra-metrics';
        const rows = [
            ['OPRA Implied ±%', formatImpliedMove(card), 'Front-expiry ATM straddle implied move'],
            ['20d ADR%', formatAdr(card.adr20Pct), card.adrAsOfSessionDate ? `Completed 20d ADR as of ${card.adrAsOfSessionDate} NY` : (card.adrUnavailableReason || 'Completed 20d ADR unavailable')],
            ['25Δ Skew', formatSkew(card.skew25Delta), '25-delta call minus put implied volatility']
        ];
        for (const [label, value, title] of rows) {
            const cell = document.createElement('div');
            cell.title = title;
            const dt = document.createElement('dt');
            dt.textContent = label;
            const dd = document.createElement('dd');
            dd.className = 'num';
            dd.textContent = value;
            cell.append(dt, dd);
            metrics.append(cell);
        }

        const barWrap = document.createElement('div');
        barWrap.className = 'catalysts-opra-bar-wrap';
        const bar = document.createElement('progress');
        bar.className = 'catalysts-opra-bar';
        bar.dataset.tone = ratioInfo.tone;
        bar.max = 3;
        const ratioNumeric = typeof card.impliedMoveToAdrRatio === 'number' && Number.isFinite(card.impliedMoveToAdrRatio) && card.impliedMoveToAdrRatio > 0
            ? Math.min(3, card.impliedMoveToAdrRatio)
            : 0;
        bar.value = ratioNumeric;
        bar.setAttribute('aria-label', `${card.symbol} implied move to 20d ADR ratio: ${ratioInfo.text}`);
        barWrap.append(bar);

        const note = document.createElement('p');
        note.className = 'watch-fine catalysts-opra-note';
        note.textContent = [card.adrUnavailableReason, card.summaryNote].filter(Boolean).join(' · ');

        article.append(head, metrics, barWrap, note);
        return article;
    }

    // Fetches read-only OPRA implied move vs 20d ADR% cards for the requested symbols while aborting any superseded request.
    async function loadCards(symbols, { force = false } = {}) {
        const normalized = normalizeSymbols(symbols);
        const signature = normalized.join(',');
        if (!force && signature === lastLoadedSignature) return;
        activeSymbols = normalized;
        lastLoadedSignature = signature;

        if (currentController) currentController.abort();
        currentController = new AbortController();
        const seq = ++requestSeq;

        if (statusEl) statusEl.textContent = `Loading OPRA vs 20d ADR% for ${normalized.join(', ')}…`;
        if (refreshBtn) refreshBtn.disabled = true;

        try {
            const response = await apiFetch(`/api/v1/catalysts/options-adr?symbols=${encodeURIComponent(signature)}`, {
                method: 'GET',
                cache: 'no-store',
                signal: currentController.signal
            });
            if (!response.ok) {
                let detail = `HTTP ${response.status}`;
                try {
                    const errPayload = await response.json();
                    detail = errPayload?.message || errPayload?.error || detail;
                } catch {
                    // Keep HTTP status fallback when response is not JSON.
                }
                throw new Error(detail);
            }
            const payload = await response.json();
            if (seq !== requestSeq) return;
            const cards = Array.isArray(payload?.cards) ? payload.cards : [];
            if (gridEl) {
                gridEl.replaceChildren(...cards.map(buildCardNode));
            }
            const stamp = payload?.observedAtUtc
                ? new Date(payload.observedAtUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })
                : '';
            if (statusEl) statusEl.textContent = stamp
                ? `Read-only OPRA vs completed 20d ADR% · updated ${stamp}`
                : 'Read-only OPRA vs completed 20d ADR%';
        } catch (error) {
            if (error?.name === 'AbortError' || seq !== requestSeq) return;
            if (statusEl) statusEl.textContent = `OPRA vs 20d ADR% unavailable (${error?.message || 'network error'}).`;
        } finally {
            if (seq === requestSeq && refreshBtn) refreshBtn.disabled = false;
        }
    }

    // Resolves candidate US equity symbols from the Earnings render event, prioritizing server-matched tickers over raw search text to avoid querying company names.
    function resolveEarningsSymbols(detail) {
        const candidates = [];
        const matchedSymbols = Array.isArray(detail?.symbols) ? detail.symbols : [];
        for (const sym of matchedSymbols.slice(0, 4)) candidates.push(sym);
        if (!candidates.length) {
            const rawSearch = String(detail?.search ?? '').trim().toUpperCase();
            if (rawSearch) {
                for (const token of rawSearch.split(/[\s,]+/)) {
                    if (fallbackSearchTickerPattern.test(token)) candidates.push(token);
                }
            }
        }
        return normalizeSymbols(candidates, true);
    }

    refreshBtn?.addEventListener('click', () => {
        void loadCards(activeSymbols, { force: true });
    });

    if (pageKind === 'earnings') {
        window.addEventListener('tradingflow:earnings-symbols-changed', event => {
            void loadCards(resolveEarningsSymbols(event?.detail));
        });
    }

    window.addEventListener('pagehide', () => {
        if (currentController) {
            currentController.abort();
            currentController = null;
        }
    });

    void loadCards(activeSymbols, { force: true });
})();
