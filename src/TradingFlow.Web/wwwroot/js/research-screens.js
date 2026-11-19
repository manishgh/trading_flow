(function () {
    'use strict';

    const panel = document.querySelector('[data-research-screens-panel]');
    if (!panel) return;

    const q = (sel) => panel.querySelector(sel);
    const els = {
        globalStatus: q('[data-rs-global-status]'),
        screenName: q('[data-rs-screen-name]'),
        from: q('[data-rs-from]'),
        to: q('[data-rs-to]'),
        dailyBars: q('[data-rs-daily-bars]'),
        corporateActions: q('[data-rs-corporate-actions]'),
        exchangeSessions: q('[data-rs-exchange-sessions]'),
        securityMaster: q('[data-rs-security-master]'),
        exclusionList: q('[data-rs-exclusion-list]'),
        minPrice: q('[data-rs-min-price]'),
        priceBasis: q('[data-rs-price-basis]'),
        averageSessions: q('[data-rs-average-sessions]'),
        minDollarVolume: q('[data-rs-min-dollar-volume]'),
        minAdrPct: q('[data-rs-min-adr-pct]'),
        minHistory: q('[data-rs-min-history]'),
        splitLookback: q('[data-rs-split-lookback]'),
        includeFredVix: q('[data-rs-include-fred-vix]'),
        buildBtn: q('[data-rs-build-btn]'),
        buildStatus: q('[data-rs-build-status]'),
        planScreen: q('[data-rs-plan-screen]'),
        planFrom: q('[data-rs-plan-from]'),
        planTo: q('[data-rs-plan-to]'),
        priorSessions: q('[data-rs-prior-sessions]'),
        includeHeld: q('[data-rs-include-held]'),
        planBtn: q('[data-rs-plan-btn]'),
        collectBtn: q('[data-rs-collect-btn]'),
        planSummary: q('[data-rs-plan-summary]'),
        collectStatus: q('[data-rs-collect-status]')
    };

    let optionsCache = null;
    let confirmedPlan = null;
    let loaded = false;
    const lifetime = new AbortController();
    window.addEventListener('pagehide', () => lifetime.abort());

    async function apiJson(path, body) {
        const init = body === undefined
            ? { method: 'GET', headers: { Accept: 'application/json' }, cache: 'no-store', signal: lifetime.signal }
            : { method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/json' }, body: JSON.stringify(body), signal: lifetime.signal };
        const response = await window.tradingFlowFetch('/api/v1/' + path, init);
        const text = await response.text();
        let data = null;
        try { data = text ? JSON.parse(text) : null; } catch { data = null; }
        if (!response.ok) {
            const detail = data && (data.message || data.detail || data.title) ? (data.message || data.detail || data.title) : ('HTTP ' + response.status);
            throw new Error(detail);
        }
        return data;
    }

    function abortableDelay(ms, signal) {
        return new Promise((resolve, reject) => {
            if (signal.aborted) { reject(new DOMException('Aborted', 'AbortError')); return; }
            const timer = setTimeout(() => { signal.removeEventListener('abort', onAbort); resolve(); }, ms);
            function onAbort() { clearTimeout(timer); reject(new DOMException('Aborted', 'AbortError')); }
            signal.addEventListener('abort', onAbort, { once: true });
        });
    }

    function setOptions(select, items, emptyLabel, preferredPredicate) {
        select.innerHTML = '';
        if (emptyLabel !== null) {
            const opt = document.createElement('option');
            opt.value = '';
            opt.textContent = emptyLabel;
            select.append(opt);
        }
        let selectedValue = '';
        for (const item of items) {
            const opt = document.createElement('option');
            opt.value = item.value;
            opt.textContent = item.label;
            select.append(opt);
            if (!selectedValue && preferredPredicate && preferredPredicate(item)) {
                selectedValue = item.value;
            }
        }
        if (selectedValue) {
            select.value = selectedValue;
        } else if (emptyLabel === null && items.length > 0) {
            select.value = items[0].value;
        }
    }

    function invalidatePlan() {
        confirmedPlan = null;
        els.collectBtn.hidden = true;
        els.collectBtn.disabled = true;
        els.planSummary.textContent = '';
    }

    function syncSelectedScreenBounds() {
        invalidatePlan();
        if (!optionsCache || !optionsCache.publishedScreens) return;
        const selected = optionsCache.publishedScreens.find((s) => s.name === els.planScreen.value);
        if (!selected) return;
        els.planFrom.value = selected.from;
        els.planTo.value = selected.to;
    }

    async function loadOptions(selectScreenName) {
        try {
            const data = await apiJson('backtests/research-screens/options');
            optionsCache = data;
            loaded = true;
            els.globalStatus.hidden = true;

            setOptions(els.dailyBars, (data.dailyBars || []).map((d) => ({ value: d.datasetId, label: d.summary })), null);
            setOptions(els.corporateActions, (data.corporateActions || []).map((d) => ({ value: d.datasetId, label: d.summary })), null);
            setOptions(els.exchangeSessions, (data.exchangeSessions || []).map((d) => ({ value: d.datasetId, label: d.summary })), null);
            setOptions(els.securityMaster, (data.securityMasters || []).map((d) => ({ value: d.datasetId, label: d.summary })), 'None', () => true);
            setOptions(
                els.exclusionList,
                (data.exclusionLists || []).map((item) => ({ value: item.key, label: item.label })),
                'No symbol exclusions',
                (item) => /finviz-funds-/i.test(item.value)
            );

            const unranked = (data.publishedScreens || []).filter((s) => !s.isRanked);
            const screenItems = unranked.map((s) => ({
                value: s.name,
                label: s.name + ' (' + s.from + ' .. ' + s.to + (s.fiveMinuteDatasetIds.length ? ', ' + s.fiveMinuteDatasetIds.length + ' 5m dataset' : '') + ')'
            }));
            setOptions(
                els.planScreen,
                screenItems,
                screenItems.length === 0 ? 'No unranked screens published yet' : null,
                selectScreenName ? (item) => item.value === selectScreenName : undefined
            );
            syncSelectedScreenBounds();
        } catch (err) {
            els.globalStatus.hidden = false;
            els.globalStatus.textContent = 'Could not load research screen options: ' + err.message;
        }
    }

    async function refreshBacktestStockSources(selectScreenName) {
        const stockSelect = document.getElementById('StockSource');
        if (!stockSelect) return;
        const previousValue = selectScreenName ? 'research_screen:' + selectScreenName : stockSelect.value;
        const sources = await apiJson('backtests/sources');
        const groupLabels = {
            wishlist: 'Wishlists',
            finviz: 'Saved Finviz lists',
            research_screen: 'Published research screens'
        };
        const groups = new Map();
        for (const source of sources) {
            if (!groups.has(source.kind)) groups.set(source.kind, []);
            groups.get(source.kind).push(source);
        }
        stockSelect.innerHTML = '';
        for (const [kind, items] of groups.entries()) {
            const optgroup = document.createElement('optgroup');
            optgroup.label = groupLabels[kind] || kind;
            for (const item of items) {
                const option = document.createElement('option');
                option.value = item.key;
                option.textContent = item.name;
                option.dataset.sourceKind = item.kind;
                option.dataset.sourceKey = item.key;
                if (item.wishlistId) option.dataset.wishlistId = item.wishlistId;
                if (item.query) option.dataset.query = item.query;
                optgroup.append(option);
            }
            stockSelect.append(optgroup);
        }
        if (previousValue && Array.from(stockSelect.options).some((opt) => opt.value === previousValue)) {
            stockSelect.value = previousValue;
            stockSelect.dispatchEvent(new Event('change', { bubbles: true }));
        }
    }

    async function pollJob(jobId, statusEl, onCompleted) {
        while (!lifetime.signal.aborted) {
            await abortableDelay(1500, lifetime.signal);
            const snapshot = await apiJson('backtests/research-screens/jobs/' + encodeURIComponent(jobId));
            statusEl.textContent = '[' + snapshot.status + '] ' + snapshot.message;
            if (snapshot.status === 'completed') {
                await onCompleted(snapshot);
                return;
            }
            if (snapshot.status === 'failed' || snapshot.status === 'cancelled') {
                throw new Error(snapshot.errorMessage || snapshot.message || ('Job ' + snapshot.status));
            }
        }
    }

    function parseOptionalNumber(input) {
        const raw = input.value.trim();
        return raw === '' ? null : Number(raw);
    }

    els.buildBtn.addEventListener('click', async () => {
        els.buildBtn.disabled = true;
        els.buildStatus.textContent = 'Submitting screen build...';
        try {
            const payload = {
                screenName: els.screenName.value.trim(),
                dailyBarsDatasetId: els.dailyBars.value,
                corporateActionsDatasetId: els.corporateActions.value,
                from: els.from.value,
                to: els.to.value,
                minPrice: Number(els.minPrice.value),
                priceBasis: els.priceBasis.value,
                averageSessions: Number(els.averageSessions.value),
                minAverageDollarVolume: parseOptionalNumber(els.minDollarVolume),
                minAverageRangePercent: parseOptionalNumber(els.minAdrPct),
                minHistorySessions: Number(els.minHistory.value),
                splitLookbackSessions: parseOptionalNumber(els.splitLookback),
                exchangeSessionsDatasetId: els.exchangeSessions.value || null,
                securityMasterDatasetId: els.securityMaster.value || null,
                exclusionListKey: els.exclusionList.value || null,
                includeFredVix: els.includeFredVix.checked
            };
            const job = await apiJson('backtests/research-screens/build', payload);
            els.buildStatus.textContent = '[' + job.status + '] ' + job.message;
            await pollJob(job.jobId, els.buildStatus, async (done) => {
                const name = done.screenResult ? done.screenResult.screenName : payload.screenName;
                await loadOptions(name);
                await refreshBacktestStockSources(name);
                els.buildStatus.textContent = 'Completed: ' + done.message + ' Available in Stock source picker.';
            });
        } catch (err) {
            els.buildStatus.textContent = 'Screen build failed: ' + err.message;
        } finally {
            els.buildBtn.disabled = false;
        }
    });

    els.planScreen.addEventListener('change', syncSelectedScreenBounds);
    els.planFrom.addEventListener('change', invalidatePlan);
    els.planTo.addEventListener('change', invalidatePlan);
    els.priorSessions.addEventListener('change', invalidatePlan);
    els.includeHeld.addEventListener('change', invalidatePlan);

    els.planBtn.addEventListener('click', async () => {
        invalidatePlan();
        els.planBtn.disabled = true;
        els.collectStatus.textContent = '';
        els.planSummary.textContent = 'Calculating missing 5-minute stock-sessions (dry run, no download)...';
        try {
            const summary = await apiJson('backtests/research-screens/minute-bars/plan', {
                screenName: els.planScreen.value,
                from: els.planFrom.value || null,
                to: els.planTo.value || null,
                priorSessions: Number(els.priorSessions.value || 0),
                includeHeldSessions: els.includeHeld.checked
            });
            if (summary.alreadyComplete || summary.missingStockSessions === 0) {
                els.planSummary.textContent =
                    '5-minute bars are already complete for ' + summary.screenName + ' (' +
                    summary.alreadyCollectedStockSessions.toLocaleString() + ' of ' +
                    summary.totalRequiredStockSessions.toLocaleString() + ' stock-sessions present; 0 missing).';
                return;
            }
            confirmedPlan = {
                planFileName: summary.planFileName,
                expectedRequestSha256: summary.requestSha256
            };
            els.planSummary.textContent =
                'Dry-run estimate for ' + summary.screenName + ' (' + summary.from + ' to ' + summary.to + '): ' +
                summary.totalRequiredStockSessions.toLocaleString() + ' required stock-sessions · ' +
                summary.alreadyCollectedStockSessions.toLocaleString() + ' already in catalog · ' +
                summary.missingStockSessions.toLocaleString() + ' missing (' +
                summary.uniqueSymbolCount.toLocaleString() + ' symbols, ' +
                summary.httpRequestBatchCount.toLocaleString() + ' Alpaca HTTP batches, ~' +
                summary.estimatedBars.toLocaleString() + ' bars, ~' +
                summary.estimatedDownloadMegabytes + ' MB). ' + summary.estimateBasis;
            els.collectBtn.hidden = false;
            els.collectBtn.disabled = false;
        } catch (err) {
            els.planSummary.textContent = 'Estimate failed: ' + err.message;
        } finally {
            els.planBtn.disabled = false;
        }
    });

    els.collectBtn.addEventListener('click', async () => {
        if (!confirmedPlan) return;
        els.collectBtn.disabled = true;
        els.planBtn.disabled = true;
        els.collectStatus.textContent = 'Queuing confirmed 5-minute bar download...';
        try {
            const job = await apiJson('backtests/research-screens/minute-bars/collect', confirmedPlan);
            els.collectStatus.textContent = '[' + job.status + '] ' + job.message;
            await pollJob(job.jobId, els.collectStatus, async (done) => {
                invalidatePlan();
                const dsId = done.collectionResult ? done.collectionResult.datasetId : '';
                els.collectStatus.textContent = 'Completed: ' + done.message + (dsId ? ' (DatasetId: ' + dsId + ')' : '');
                await loadOptions(els.planScreen.value);
            });
        } catch (err) {
            els.collectStatus.textContent = 'Collection failed: ' + err.message;
            els.collectBtn.disabled = false;
        } finally {
            els.planBtn.disabled = false;
        }
    });

    panel.addEventListener('toggle', () => {
        if (panel.open && !loaded) {
            loadOptions();
        }
    });
})();
