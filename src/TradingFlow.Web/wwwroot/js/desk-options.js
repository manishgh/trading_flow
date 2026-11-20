// Read-only OPRA options sentiment pulse for Desk (SPY, QQQ, and the currently selected stock).
(() => {
    const root = document.getElementById('desk-options-pulse');
    if (!root) return;

    const statusEl = document.getElementById('desk-options-status');
    const refreshBtn = document.getElementById('desk-options-refresh');
    const selectedCard = document.getElementById('desk-options-selected');
    const apiFetch = typeof window.tradingFlowFetch === 'function' ? window.tradingFlowFetch : window.fetch.bind(window);

    let activeSymbol = null;
    let currentController = null;
    let requestSeq = 0;

    // Maps canonical RegimeLabel codes to short trader-readable badges and CSS tones.
    function formatRegime(label) {
        switch (label) {
            case 'call_skew_squeeze_risk': return { text: 'Call squeeze risk', tone: 'warn' };
            case 'put_skew_hedging': return { text: 'Put hedging', tone: 'loss' };
            case 'term_backwardation_stress': return { text: 'Term backwardation', tone: 'warn' };
            case 'neutral': return { text: 'Balanced', tone: 'gain' };
            case 'no_liquid_options': return { text: 'No liquid chain', tone: 'muted' };
            default: return { text: 'Unavailable', tone: 'muted' };
        }
    }

    // Formats decimal volatility-point spreads (e.g. 0.062 -> "+6.2 pts").
    function formatVolPts(value) {
        if (typeof value !== 'number' || !Number.isFinite(value)) return '—';
        const pts = value * 100;
        return (pts >= 0 ? '+' : '') + pts.toFixed(1) + ' pts';
    }

    // Formats front-expiry ATM straddle implied move and trading-day horizon.
    function formatMove(snapshot) {
        if (typeof snapshot?.frontImpliedMovePct !== 'number' || !Number.isFinite(snapshot.frontImpliedMovePct)) return '—';
        const days = typeof snapshot.tradingDaysToFrontExpiry === 'number' ? ` (${snapshot.tradingDaysToFrontExpiry}d)` : '';
        return `±${snapshot.frontImpliedMovePct.toFixed(1)}%${days}`;
    }

    // Formats liquid call-to-put daily volume ratio.
    function formatRatio(value) {
        return typeof value === 'number' && Number.isFinite(value) ? value.toFixed(2) + 'x' : '—';
    }

    // Renders one OptionsSentimentSnapshot object into its card slot using textContent only.
    function renderCard(slotName, snapshot) {
        const card = root.querySelector(`[data-options-slot="${slotName}"]`);
        if (!card) return;
        if (!snapshot) {
            card.hidden = true;
            return;
        }
        card.hidden = false;
        const symbolEl = card.querySelector('[data-options-field="symbol"]');
        if (symbolEl) symbolEl.textContent = snapshot.symbol || '—';

        const regime = formatRegime(snapshot.regimeLabel);
        const badgeEl = card.querySelector('[data-options-field="badge"]');
        if (badgeEl) {
            badgeEl.textContent = regime.text;
            badgeEl.dataset.tone = regime.tone;
        }

        const moveEl = card.querySelector('[data-options-field="move"]');
        if (moveEl) moveEl.textContent = formatMove(snapshot);

        const skewEl = card.querySelector('[data-options-field="skew"]');
        if (skewEl) skewEl.textContent = formatVolPts(snapshot.skew25Delta);

        const termEl = card.querySelector('[data-options-field="term"]');
        if (termEl) termEl.textContent = formatVolPts(snapshot.termStructureSpread);

        const cpEl = card.querySelector('[data-options-field="cpvol"]');
        if (cpEl) cpEl.textContent = formatRatio(snapshot.callPutVolumeRatio);

        const noteEl = card.querySelector('[data-options-field="note"]');
        if (noteEl) noteEl.textContent = snapshot.summaryNote || '';
    }

    // Cancels any superseded request and fetches SPY, QQQ, and optional selected-symbol OPRA context.
    async function loadOptionsSentiment(symbol) {
        if (currentController) {
            currentController.abort();
        }
        currentController = new AbortController();
        const seq = ++requestSeq;
        const query = symbol ? `?symbol=${encodeURIComponent(symbol)}` : '';
        if (statusEl) statusEl.textContent = symbol ? `Refreshing OPRA context for SPY, QQQ and ${symbol}…` : 'Refreshing OPRA context for SPY & QQQ…';
        if (refreshBtn) refreshBtn.disabled = true;

        try {
            const response = await apiFetch(`/api/v1/desk/options-sentiment${query}`, {
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
                    // Preserve HTTP status fallback when response body is not JSON.
                }
                throw new Error(detail);
            }
            const data = await response.json();
            if (seq !== requestSeq) return;
            renderCard('spy', data.spy);
            renderCard('qqq', data.qqq);
            if (data.selected) {
                renderCard('selected', data.selected);
            } else if (selectedCard) {
                selectedCard.hidden = true;
            }
            const stamp = data.observedAtUtc ? new Date(data.observedAtUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }) : '';
            if (statusEl) statusEl.textContent = stamp ? `Read-only OPRA snapshot · updated ${stamp} (5m cache)` : 'Read-only OPRA snapshot';
        } catch (err) {
            if (err && err.name === 'AbortError') return;
            if (seq !== requestSeq) return;
            if (statusEl) statusEl.textContent = `OPRA options sentiment unavailable (${err?.message || 'network error'}).`;
        } finally {
            if (seq === requestSeq && refreshBtn) {
                refreshBtn.disabled = false;
            }
        }
    }

    refreshBtn?.addEventListener('click', () => {
        loadOptionsSentiment(activeSymbol);
    });

    window.addEventListener('tradingflow:desk-symbol-selected', (event) => {
        const nextSymbol = event?.detail?.symbol || null;
        if (nextSymbol === activeSymbol) return;
        activeSymbol = nextSymbol;
        if (!activeSymbol && selectedCard) {
            selectedCard.hidden = true;
            return;
        }
        loadOptionsSentiment(activeSymbol);
    });

    window.addEventListener('pagehide', () => {
        if (currentController) {
            currentController.abort();
            currentController = null;
        }
    });

    loadOptionsSentiment(null);
})();
