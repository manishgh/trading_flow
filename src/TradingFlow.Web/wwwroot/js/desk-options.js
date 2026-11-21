// Read-only OPRA options sentiment pulse for Desk (compact SPY/QQQ stress chip in top strip + selected stock strip).
(() => {
    const root = document.getElementById('desk-options-pulse');
    const stressChip = document.getElementById('desk-options-stress-chip');
    const selectedCard = document.getElementById('desk-options-selected');
    if (!root && !stressChip && !selectedCard) return;

    const statusEl = document.getElementById('desk-options-status');
    const refreshBtn = document.getElementById('desk-options-refresh');
    const apiFetch = typeof window.tradingFlowFetch === 'function' ? window.tradingFlowFetch : window.fetch.bind(window);

    let activeSymbol = null;
    let currentController = null;
    let requestSeq = 0;

    // Maps canonical RegimeLabel codes to plain-English trader badges, CSS tones, and explanatory tooltips.
    function formatRegime(label) {
        switch (label) {
            case 'term_backwardation_stress':
                return {
                    text: 'Near-term event risk',
                    tone: 'warn',
                    title: 'Near-term options cost much more in IV than 30-day options — traders are pricing in an immediate catalyst, earnings, or gap move.'
                };
            case 'call_skew_squeeze_risk':
                return {
                    text: 'Upside call chasing',
                    tone: 'warn',
                    title: 'Out-of-the-money Calls are pricier than Puts — heavy speculative upside demand or squeeze risk.'
                };
            case 'put_skew_hedging':
                return {
                    text: 'Downside hedging',
                    tone: 'loss',
                    title: 'Out-of-the-money Puts are pricier than Calls — traders are paying up for downside protection.'
                };
            case 'neutral':
                return {
                    text: 'Balanced options',
                    tone: 'gain',
                    title: 'Balanced call/put pricing and normal volatility curve across expirations.'
                };
            case 'no_liquid_options':
                return {
                    text: 'Thin options',
                    tone: 'muted',
                    title: 'Too few liquid options contracts passed liquidity filters (<=35% bid-ask spread, 2–60 DTE, 80%–120% moneyness).'
                };
            default:
                return {
                    text: 'Unavailable',
                    tone: 'muted',
                    title: 'OPRA options context could not be loaded for this symbol.'
                };
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
        const days = typeof snapshot.tradingDaysToFrontExpiry === 'number' && Number.isFinite(snapshot.tradingDaysToFrontExpiry) ? ` (${snapshot.tradingDaysToFrontExpiry}d)` : '';
        return `±${snapshot.frontImpliedMovePct.toFixed(1)}%${days}`;
    }

    // Formats liquid call-to-put daily volume ratio.
    function formatRatio(value) {
        return typeof value === 'number' && Number.isFinite(value) ? value.toFixed(2) + 'x' : '—';
    }

    // Builds plain-English explanatory tooltips for each metric value, including why a field shows '—'.
    function formatMetricTooltips(snapshot) {
        const hasMove = typeof snapshot?.frontImpliedMovePct === 'number' && Number.isFinite(snapshot.frontImpliedMovePct);
        const days = typeof snapshot?.tradingDaysToFrontExpiry === 'number' && Number.isFinite(snapshot.tradingDaysToFrontExpiry) ? ` over ${snapshot.tradingDaysToFrontExpiry} trading days` : '';
        const move = hasMove
            ? `Expected ±${snapshot.frontImpliedMovePct.toFixed(1)}% swing${days} priced from the nearest at-the-money Call + Put straddle.`
            : 'No liquid at-the-money Call + Put straddle met liquidity filters (<=35% bid-ask spread, 2–60 DTE, 80%–120% moneyness) on the nearest expiry.';

        const hasSkew = typeof snapshot?.skew25Delta === 'number' && Number.isFinite(snapshot.skew25Delta);
        let skew = 'No liquid ~25-delta Call/Put pair met liquidity filters (<=35% bid-ask spread, 2–60 DTE, 80%–120% moneyness) on this expiry.';
        if (hasSkew) {
            const pts = snapshot.skew25Delta * 100;
            skew = pts > 0
                ? `Downside Puts are +${pts.toFixed(1)} IV pts pricier than Calls — traders paying up for downside protection.`
                : pts < 0
                    ? `Upside Calls are ${Math.abs(pts).toFixed(1)} IV pts pricier than Puts — upside call chasing.`
                    : 'Downside Put and upside Call ~25-delta IV are balanced (0.0 pts).';
        }

        const hasTerm = typeof snapshot?.termStructureSpread === 'number' && Number.isFinite(snapshot.termStructureSpread);
        let term = 'Need liquid front-month and ~30-day expirations to compare near-term vs 30-day IV.';
        if (hasTerm) {
            const pts = snapshot.termStructureSpread * 100;
            term = pts > 0
                ? `Near-term IV is +${pts.toFixed(1)} pts above ~30-day IV — immediate catalyst or gap risk priced in.`
                : `Near-term IV is ${Math.abs(pts).toFixed(1)} pts below ~30-day IV — calm normal volatility curve.`;
        }

        const hasCp = typeof snapshot?.callPutVolumeRatio === 'number' && Number.isFinite(snapshot.callPutVolumeRatio);
        let cpvol = 'No liquid Put volume was recorded today to compute today\'s Call/Put volume ratio.';
        if (hasCp) {
            const side = snapshot.callPutVolumeRatio > 1 ? 'more Calls than Puts' : snapshot.callPutVolumeRatio < 1 ? 'more Puts than Calls' : 'even Call and Put volume';
            cpvol = `${snapshot.callPutVolumeRatio.toFixed(2)}x traded Call volume vs Put volume across liquid strikes (${side}).`;
        }

        return { move, skew, term, cpvol };
    }

    // Renders one OptionsSentimentSnapshot object into its card slot across the Desk pulse and chart detail using textContent only.
    function renderCard(slotName, snapshot) {
        const card = document.querySelector('[data-options-slot="' + slotName + '"]');
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
            badgeEl.title = regime.title;
            badgeEl.dataset.tooltip = regime.title;
        }

        const tips = formatMetricTooltips(snapshot);
        const moveEl = card.querySelector('[data-options-field="move"]');
        if (moveEl) {
            moveEl.textContent = formatMove(snapshot);
            moveEl.title = tips.move;
            const moveBox = moveEl.closest('div');
            if (moveBox) { moveBox.title = tips.move; moveBox.dataset.tooltip = tips.move; }
        }

        const skewEl = card.querySelector('[data-options-field="skew"]');
        if (skewEl) {
            skewEl.textContent = formatVolPts(snapshot.skew25Delta);
            skewEl.title = tips.skew;
            const skewBox = skewEl.closest('div');
            if (skewBox) { skewBox.title = tips.skew; skewBox.dataset.tooltip = tips.skew; }
        }

        const termEl = card.querySelector('[data-options-field="term"]');
        if (termEl) {
            termEl.textContent = formatVolPts(snapshot.termStructureSpread);
            termEl.title = tips.term;
            const termBox = termEl.closest('div');
            if (termBox) { termBox.title = tips.term; termBox.dataset.tooltip = tips.term; }
        }

        const cpEl = card.querySelector('[data-options-field="cpvol"]');
        if (cpEl) {
            cpEl.textContent = formatRatio(snapshot.callPutVolumeRatio);
            cpEl.title = tips.cpvol;
            const cpBox = cpEl.closest('div');
            if (cpBox) { cpBox.title = tips.cpvol; cpBox.dataset.tooltip = tips.cpvol; }
        }

        const noteEl = card.querySelector('[data-options-field="note"]');
        if (noteEl) noteEl.textContent = snapshot.summaryNote || '';
        const contractDetail = typeof snapshot.liquidContractCount === 'number' && Number.isFinite(snapshot.liquidContractCount)
            ? `${snapshot.liquidContractCount} liquid OPRA contracts (<=35% bid-ask spread, 2–60 DTE, 80%–120% moneyness)`
            : '';
        card.title = [contractDetail, snapshot.summaryNote].filter(Boolean).join(' · ');
    }

    // Formats one benchmark OPRA snapshot into a concise tooltip line for the unified top-strip stress chip.
    function formatBenchmarkTooltip(label, snapshot) {
        if (!snapshot) return `${label}: unavailable`;
        const regime = formatRegime(snapshot.regimeLabel);
        const contracts = typeof snapshot.liquidContractCount === 'number' && Number.isFinite(snapshot.liquidContractCount)
            ? ` · ${snapshot.liquidContractCount} liquid`
            : '';
        return `${label}: ${regime.text} · move ${formatMove(snapshot)} · skew ${formatVolPts(snapshot.skew25Delta)} · near vs 30d ${formatVolPts(snapshot.termStructureSpread)} · C/P ${formatRatio(snapshot.callPutVolumeRatio)}${contracts}`;
    }

    // Derives one compact SPY/QQQ options stress chip for the unified top bar while preserving full numbers in its tooltip.
    function renderStressChip(spy, qqq, observedAtUtc, errorMessage) {
        if (!stressChip) return;
        stressChip.hidden = false;
        if (errorMessage) {
            stressChip.textContent = 'Options stress: —';
            stressChip.dataset.tone = 'muted';
            stressChip.title = `OPRA options sentiment unavailable (${errorMessage}).`;
            return;
        }
        const items = [spy, qqq].filter(Boolean);
        const labels = items.map(item => item.regimeLabel);
        const hasBackwardation = labels.includes('term_backwardation_stress') ||
            items.some(item => typeof item.termStructureSpread === 'number' && Number.isFinite(item.termStructureSpread) && item.termStructureSpread > 0.03);
        const hasSkewStress = labels.includes('put_skew_hedging') || labels.includes('call_skew_squeeze_risk') ||
            items.some(item => typeof item.skew25Delta === 'number' && Number.isFinite(item.skew25Delta) && (item.skew25Delta > 0.06 || item.skew25Delta < -0.04));
        const hasNeutral = labels.includes('neutral');

        let level = '—';
        let tone = 'muted';
        if (hasBackwardation) {
            level = 'high';
            tone = 'warn';
        } else if (hasSkewStress) {
            level = 'high';
            tone = 'warn';
        } else if (hasNeutral) {
            level = 'normal';
            tone = 'good';
        }

        stressChip.textContent = `Options stress: ${level}`;
        stressChip.dataset.tone = tone;
        const stamp = observedAtUtc ? `Updated ${new Date(observedAtUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })} (5m cache)` : '';
        stressChip.title = [formatBenchmarkTooltip('SPY', spy), formatBenchmarkTooltip('QQQ', qqq), stamp].filter(Boolean).join('\n');
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
            renderStressChip(data.spy, data.qqq, data.observedAtUtc, null);
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
            renderStressChip(null, null, null, err?.message || 'network error');
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
