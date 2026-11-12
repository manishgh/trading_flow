(() => {
    const root = document.querySelector('[data-trading-positions]');
    if (!root) return;
    const body = root.querySelector('[data-position-body]');
    const status = root.querySelector('[data-position-status]');
    const empty = root.querySelector('[data-position-empty]');
    const region = root.querySelector('.table-scroll');
    const count = root.querySelector('[data-position-count]');
    const protection = root.querySelector('[data-position-protection-summary]');
    const portfolio = document.querySelector('[data-orders-portfolio]');
    const canReview = root.dataset.canReview === 'true';
    let loading = false;
    let readAt = root.dataset.readAt || null;
    let activeRequest;
    let stopped = false;
    const maximumAgeMs = 30000;
    const requestTimeoutMs = 8000;
    // Keep absent broker fields unknown, and distinguish positive and negative P/L explicitly.
    const money = value => value == null ? 'Unknown' : Number(value).toLocaleString(undefined, { style: 'currency', currency: 'USD' });
    const quantity = value => Number(value).toLocaleString(undefined, { maximumFractionDigits: 4 });
    const signedMoney = value => value == null ? 'Unknown' : (value > 0 ? '+' : value < 0 ? '−' : '') + money(Math.abs(value));
    const signedPercent = value => value == null ? 'Unknown' : (value > 0 ? '+' : value < 0 ? '−' : '') + Math.abs(value).toFixed(2) + '%';
    const signedClass = value => value == null ? 'unknown' : `value-signed ${value > 0 ? 'up' : value < 0 ? 'down' : 'flat'}`;

    // Carry the journal's view into the existing reviewed sell ticket without stale ticket inputs.
    function reviewUrl(symbol) {
        const url = new URL(window.location.href);
        url.searchParams.set('ticker', symbol);
        url.searchParams.set('side', 'sell');
        url.searchParams.delete('limitPrice');
        url.searchParams.delete('quantity');
        url.searchParams.delete('handler');
        if (root.dataset.env) url.searchParams.set('env', root.dataset.env);
        return url.pathname + url.search;
    }

    // Create text-only table cells so provider values cannot inject markup.
    function cell(row, value, className = '') {
        const td = document.createElement('td');
        td.textContent = value;
        td.className = className;
        row.append(td);
        return td;
    }

    // Preserve prior observations while withdrawing every position review on a failed or old read.
    function stale(detail) {
        root.dataset.state = 'stale';
        status.dataset.state = 'stale';
        status.textContent = detail + (readAt ? ` Last successful account read: ${readAt}. Displayed values may be stale.` : ' Account holdings are unknown; this does not mean there are no positions.');
        count.textContent = 'Unknown';
        if (!body.children.length) {
            empty.hidden = false;
            empty.textContent = 'Account holdings are unavailable. This does not mean the account has no positions.';
        }
        root.querySelectorAll('[data-position-review]').forEach(link => {
            link.removeAttribute('href');
            link.setAttribute('aria-disabled', 'true');
            link.title = 'Refresh account holdings before reviewing this position.';
        });
        protection.textContent = 'Broker protection is unknown while account updates are unavailable.';
        if (portfolio) {
            portfolio.dataset.state = readAt ? 'stale' : 'unavailable';
            portfolio.querySelector('.micro-label').textContent = readAt ? 'Portfolio · stale' : 'Portfolio · unavailable';
            portfolio.title = status.textContent;
        }
    }

    // Sum actual signed holdings, leaving totals unknown when any component was not reported.
    function sumKnown(values) {
        return values.some(value => value == null || !Number.isFinite(Number(value))) ? null : values.reduce((total, value) => total + Number(value), 0);
    }

    // Refresh the header from the same full-account response as the position rows.
    function updatePortfolio(snapshot) {
        if (!portfolio) return;
        const marketValue = sumKnown(snapshot.trades.map(trade => trade.holding?.marketValue));
        const cost = sumKnown(snapshot.trades.map(trade => trade.holding?.costBasis));
        const pl = snapshot.totalUnrealizedPl ?? null;
        const percentage = pl != null && cost != null && cost !== 0 ? pl / cost * 100 : null;
        portfolio.dataset.state = 'live';
        portfolio.title = `Account read ${snapshot.readAtUtc}`;
        portfolio.querySelector('.micro-label').textContent = `Portfolio · ${snapshot.trades.length} open`;
        portfolio.querySelector('.metric-value').textContent = money(marketValue);
        const detail = portfolio.querySelector('.metric-detail');
        const value = document.createElement('span');
        value.className = signedClass(pl);
        value.textContent = `${signedMoney(pl)} (${signedPercent(percentage)})`;
        detail.replaceChildren(document.createTextNode(`cost ${money(cost)} · `), value);
    }

    // Reconcile by symbol without replacing a focused review link.
    function update(trades) {
        const rows = new Map([...body.children].map(row => [row.dataset.positionSymbol, row]));
        const symbols = new Set(trades.map(trade => trade.ticker));
        for (const [symbol, row] of rows) if (!symbols.has(symbol)) row.remove();
        for (const trade of trades) {
            let row = rows.get(trade.ticker);
            if (!row) {
                row = document.createElement('tr');
                row.dataset.positionSymbol = trade.ticker;
            }
            // Keep the review anchor stable across polls so keyboard focus survives.
            let review = row.querySelector('[data-position-review]');
            const focused = review === document.activeElement;
            const cells = [...row.children];
            const values = [trade.ticker, quantity(trade.quantity), money(trade.entryPrice), money(trade.holding?.marketValue), signedMoney(trade.unrealizedPl), trade.protectionSummary || 'Broker protection unknown'];
            values.forEach((value, index) => {
                const td = cells[index] || cell(row, '');
                td.textContent = value;
                td.className = index > 0 && index < 5 ? 'num' : '';
                if (index === 4) td.className = `num ${signedClass(trade.unrealizedPl)}`;
                if (index === 5) {
                    td.classList.add(trade.protectionKnown ? trade.hasBrokerStop ? 'protection-known' : 'accent-note' : 'unknown');
                    td.title = value;
                }
            });
            const actions = cells[6] || cell(row, '');
            if (canReview && Number(trade.quantity) > 0) {
                if (!review) {
                    review = document.createElement('a');
                    review.className = 'btn btn-secondary btn-sm';
                    review.dataset.positionReview = '';
                    review.textContent = 'Review';
                    actions.replaceChildren(review);
                }
                review.href = reviewUrl(trade.ticker);
                review.removeAttribute('aria-disabled');
                review.title = `Review a sell ticket for ${trade.ticker}; no order is sent until server review and confirmation.`;
            } else {
                actions.textContent = Number(trade.quantity) < 0 ? 'Short position · close review unavailable' : 'Review unavailable';
                actions.className = 'note';
            }
            if (!row.isConnected) body.append(row);
            if (focused && review?.isConnected) review.focus({ preventScroll: true });
        }
        region.hidden = trades.length === 0;
        empty.hidden = trades.length > 0;
        empty.textContent = 'No open positions in the account.';
    }

    // Read only the existing coherent broker holdings/protection endpoint.
    async function readSnapshot(signal) {
        const response = await window.tradingFlowFetch('/api/v1/running-trades?source=all', { headers: { Accept: 'application/json' }, cache: 'no-store', signal });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        return response.json();
    }

    // Bound network waits and accept a response as current only when its broker read time is known and recent.
    async function poll() {
        if (loading || stopped) return;
        loading = true;
        const controller = new AbortController();
        activeRequest = controller;
        let timeout;
        try {
            const deadline = new Promise((_, reject) => {
                timeout = window.setTimeout(() => { reject(new Error('Broker read timed out after 8 s')); controller.abort(); }, requestTimeoutMs);
            });
            const snapshot = await Promise.race([readSnapshot(controller.signal), deadline]);
            if (snapshot.available !== true) {
                stale(snapshot.detail || 'The broker account could not be read.');
                return;
            }
            if (!Array.isArray(snapshot.trades)) throw new Error('Account response has no holdings list');
            const time = new Date(snapshot.readAtUtc || '');
            const ageMs = Date.now() - time.valueOf();
            if (Number.isNaN(time.valueOf()) || ageMs < -5000 || ageMs > maximumAgeMs) {
                stale(`Broker snapshot read time is ${snapshot.readAtUtc || 'unknown'}; a read within 30 s is required.`);
                return;
            }
            update(snapshot.trades);
            updatePortfolio(snapshot);
            readAt = snapshot.readAtUtc;
            root.dataset.state = 'live';
            status.dataset.state = 'live';
            const age = `${Math.max(0, Math.floor(ageMs / 1000))} s old`;
            status.textContent = `Account read ${readAt} · ${age} · refreshes every 10 s`;
            count.textContent = snapshot.slotsUsed == null ? 'Unknown' : snapshot.slotsUsed;
            protection.textContent = `${snapshot.unprotectedCount ?? 'Unknown'} without a broker stop · ${snapshot.unknownProtectionCount ?? 'Unknown'} protection unknown`;
        } catch (error) {
            if (!stopped) stale(`Account updates disconnected: ${error.message}.`);
        } finally {
            window.clearTimeout(timeout);
            loading = false;
            if (activeRequest === controller) activeRequest = null;
        }
    }

    // Expire displayed holdings even if repeated network reads stop yielding responses.
    function checkAge() {
        if (root.dataset.state !== 'live') return;
        const ageMs = Date.now() - new Date(readAt || '').valueOf();
        if (!Number.isFinite(ageMs) || ageMs > maximumAgeMs) stale('The last broker snapshot is more than 30 s old.');
    }

    // Navigation owns the polling lifetime; no background account requests survive this page.
    function dispose() {
        stopped = true;
        activeRequest?.abort();
        window.clearInterval(pollTimer);
        window.clearInterval(ageTimer);
    }

    // A restored document must obtain a new account snapshot instead of reviving its stopped polling state.
    function restore(event) {
        if (event.persisted) window.location.reload();
    }
    root.addEventListener('tradingflow:refresh', poll);
    window.addEventListener('pagehide', dispose, { once: true });
    window.addEventListener('pageshow', restore);
    void poll();
    const pollTimer = window.setInterval(poll, 10000);
    const ageTimer = window.setInterval(checkAge, 1000);
})();
