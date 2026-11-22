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

    // Shows the server's one-day expected move ÷ 20d ADR ratio with the band the server assigned (normal, elevated,
    // extreme), so the bands live in one place (CatalystOptionsAdrService.ClassifyImpliedMoveBand).
    function formatRatioPill(ratio, band) {
        if (typeof ratio !== 'number' || !Number.isFinite(ratio) || ratio <= 0 || !band || band === 'unavailable') {
            return { text: '—', tone: 'muted', title: 'Ratio unavailable (needs an options expected move, its days to expiry and 20 completed daily sessions)' };
        }
        const label = ratio.toFixed(2) + 'x';
        const basis = 'How much movement option prices expect for one day, compared with the stock’s 20-day average high-to-low range. ' +
            'The expected move is the price of the nearest at-the-money call plus put, spread evenly over the trading days to expiry ' +
            '(divided by the square root of the number of days). About 0.5x is an ordinary day. Display guide only, not a trading rule';
        if (band === 'normal') return { text: '✓ ' + label, tone: 'good', title: basis + ': normal (under 0.75x).' };
        if (band === 'elevated') return { text: '◐ ' + label, tone: 'warn', title: basis + ': elevated (0.75x–1.25x).' };
        return { text: '▲ ' + label, tone: 'bad', title: basis + ': extreme (over 1.25x).' };
    }

    // Formats front-expiry OPRA expected move percentage and trading-day horizon without substituting zero for missing values.
    function formatImpliedMove(card) {
        if (typeof card?.frontImpliedMovePct !== 'number' || !Number.isFinite(card.frontImpliedMovePct)) return '—';
        const days = typeof card.tradingDaysToFrontExpiry === 'number' ? ` (${card.tradingDaysToFrontExpiry}d)` : '';
        return `±${card.frontImpliedMovePct.toFixed(1)}%${days}`;
    }

    // Formats completed-bar 20-session ADR% from the server payload.
    function formatAdr(value) {
        return typeof value === 'number' && Number.isFinite(value) && value > 0 ? value.toFixed(2) + '%' : '—';
    }

    // Formats 25-delta downside Put minus upside Call volatility skew in volatility points (e.g. "+4.2 pts").
    function formatSkew(value) {
        if (typeof value !== 'number' || !Number.isFinite(value)) return '—';
        const pts = value * 100;
        return (pts >= 0 ? '+' : '\u2212') + Math.abs(pts).toFixed(1) + ' pts';
    }

    // Builds an explanatory tooltip for a card's Put vs Call fear value, including why '—' appears when illiquid.
    function formatSkewTooltip(value) {
        if (typeof value !== 'number' || !Number.isFinite(value)) {
            return 'No liquid ~25-delta Call/Put pair met liquidity filters (<=35% bid-ask spread, 2–60 DTE, 80%–120% moneyness) on this expiry.';
        }
        const pts = value * 100;
        if (pts > 0) return `Downside Puts are +${pts.toFixed(1)} IV pts pricier than Calls — traders paying up for downside protection.`;
        if (pts < 0) return `Upside Calls are ${Math.abs(pts).toFixed(1)} IV pts pricier than Puts — upside call chasing.`;
        return 'Downside Put and upside Call ~25-delta IV are balanced (0.0 pts).';
    }

    // Builds one DOM card node for a CatalystOptionsAdrCard using textContent only (no HTML string interpolation).
    function buildCardNode(card) {
        const article = document.createElement('article');
        article.className = 'catalysts-opra-card';

        const head = document.createElement('div');
        head.className = 'catalysts-opra-card-head';
        const sym = document.createElement('strong');
        sym.textContent = card.symbol || '—';

        const ratioInfo = formatRatioPill(card.impliedMoveToAdrRatio, card.impliedMoveBand);
        const pill = document.createElement('span');
        pill.className = 'catalysts-opra-ratio-pill';
        pill.dataset.tone = ratioInfo.tone;
        pill.textContent = ratioInfo.text;
        pill.title = ratioInfo.title;
        head.append(sym, pill);

        const metrics = document.createElement('dl');
        metrics.className = 'catalysts-opra-metrics';
        const hasMove = typeof card?.frontImpliedMovePct === 'number' && Number.isFinite(card.frontImpliedMovePct);
        const rows = [
            [
                'Expected move',
                formatImpliedMove(card),
                'Expected ±% stock swing by the nearest options expiration, priced from the at-the-money Call + Put straddle. Compare against your stop-loss distance.',
                hasMove ? `Expected ±${card.frontImpliedMovePct.toFixed(1)}% swing priced from the nearest ATM Call + Put straddle.` : 'No liquid at-the-money Call + Put straddle met liquidity filters (<=35% bid-ask spread, 2–60 DTE, 80%–120% moneyness) on the nearest expiry.'
            ],
            [
                '20d ADR ÷ latest close',
                formatAdr(card.adr20Pct),
                'Average daily high−low range over the last 20 completed sessions, divided by the latest close (method latest_close).',
                card.adrAsOfSessionDate ? `Completed 20d ADR as of ${card.adrAsOfSessionDate} NY` : (card.adrUnavailableReason || 'Completed 20d ADR unavailable')
            ],
            [
                'Put vs Call fear',
                formatSkew(card.skew25Delta),
                "Downside Put IV minus upside Call IV (~25 delta). Positive (+) = traders paying up for downside protection; Negative (−) = upside call chasing; '—' = no liquid 25-delta pair.",
                formatSkewTooltip(card.skew25Delta)
            ]
        ];
        for (const [label, value, labelTitle, valueTitle] of rows) {
            const cell = document.createElement('div');
            const dt = document.createElement('dt');
            dt.textContent = label;
            dt.title = labelTitle;
            const dd = document.createElement('dd');
            dd.className = 'num';
            dd.textContent = value;
            dd.title = valueTitle;
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

        // The server's summary sentence repeats the numbers above, so it moves into the card tooltip; only a reason
        // why ADR is missing stays visible, because it explains a blank value.
        article.title = card.summaryNote || '';
        article.append(head, metrics, barWrap);
        if (card.adrUnavailableReason) {
            const note = document.createElement('p');
            note.className = 'watch-fine catalysts-opra-note';
            note.textContent = card.adrUnavailableReason;
            article.append(note);
        }
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
