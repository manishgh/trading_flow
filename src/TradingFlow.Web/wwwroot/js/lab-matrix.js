/* Strategy Lab: a saved plan's Strategy x Month matrix and its slide-over drawer, read-only. Returns are never pooled. */
window.createLabMatrix = ({ api }) => {
    const matrixPanel = document.getElementById('saved-plan-matrix-panel');
    const matrixTitle = document.getElementById('saved-plan-matrix-title');
    const matrixBody = document.getElementById('saved-plan-matrix-body');
    const matrixDrawer = document.getElementById('lab-matrix-drawer');
    const matrixDrawerTitle = document.getElementById('lab-matrix-drawer-title');
    const matrixDrawerIdentity = document.getElementById('lab-matrix-drawer-identity');
    const matrixDrawerBody = document.getElementById('lab-matrix-drawer-body');
    const matrixDrawerReport = document.getElementById('lab-matrix-drawer-report');
    const matrixDrawerClose = document.getElementById('lab-matrix-drawer-close');
    const currencyFormat = new Intl.NumberFormat(undefined, { style: 'currency', currency: 'USD', maximumFractionDigits: 2 });
    let matrixRequestId = 0;
    let drawerTradesSeq = 0;
    // The plan whose matrix is on screen, however it was chosen.
    let shownPlanId = null;

    // Returns the server's plain sentence for one rejection; the code itself stays in the tooltip.
    function rejectionText(item) {
        return item.explanation || String(item.code || 'Rejected');
    }

    // Formats one immutable monthly return cell with a non-colour direction glyph and tone.
    function formatMatrixReturn(cell) {
        if (cell.returnPct === null || cell.returnPct === undefined) return { text: 'Unavailable', tone: 'muted' };
        const value = Number(cell.returnPct);
        if (!Number.isFinite(value)) return { text: 'Unavailable', tone: 'muted' };
        const abs = Math.abs(value).toFixed(2) + '%';
        if (value > 0) return { text: '▲ +' + abs, tone: 'up' };
        if (value < 0) return { text: '▼ \u2212' + abs, tone: 'down' };
        return { text: '0.00%', tone: 'flat' };
    }

    // Closes the Strategy Lab slide-over diagnostics drawer and clears its rendered DOM nodes.
    function closeMatrixDrawer() {
        drawerTradesSeq++;
        if (!matrixDrawer) return;
        if (matrixDrawer.open) matrixDrawer.close();
        matrixDrawerBody?.replaceChildren();
    }

    // Formats a nullable numeric USD figure safely without substituting zero for missing values.
    function formatDrawerMoney(value, signed = false) {
        if (value === null || value === undefined || !Number.isFinite(Number(value))) return '—';
        const num = Number(value);
        if (!signed) return currencyFormat.format(num);
        const prefix = num > 0 ? '+' : num < 0 ? '\u2212' : '';
        return prefix + currencyFormat.format(Math.abs(num));
    }

    // Builds the immutable NY-session monthly metrics card inside the slide-over drawer without aggregating across months.
    function renderDrawerMonthMetrics(row, selectedCell, selectedMonth) {
        const section = document.createElement('section');
        section.className = 'lab-drawer-section';
        const heading = document.createElement('h3');
        heading.textContent = selectedMonth ? `Monthly record · ${selectedMonth} (NY)` : 'Monthly record';
        section.append(heading);

        if (!selectedCell) {
            const note = document.createElement('p');
            note.className = 'note';
            note.textContent = row.monthlyUnavailableReason || 'No monthly valuation record is available for this selection.';
            section.append(note);
            return section;
        }

        const formatted = formatMatrixReturn(selectedCell);
        const list = document.createElement('dl');
        list.className = 'lab-drawer-metrics';
        const entries = [
            ['Monthly return', formatted.text + (selectedCell.partialMonth ? ' (partial month)' : ''), formatted.tone],
            ['Valuation basis', valuationSubtitle(selectedCell.valuationBasis) || selectedCell.valuationBasis || 'Unavailable', null],
            ['Opening → Closing equity', `${formatDrawerMoney(selectedCell.openingEquity)} → ${formatDrawerMoney(selectedCell.closingEquity)}`, null],
            ['Max drawdown', selectedCell.maxDrawdownPct != null && Number.isFinite(Number(selectedCell.maxDrawdownPct)) ? `${Number(selectedCell.maxDrawdownPct).toFixed(2)}%` : '—', null],
            ['Closed trades / Open positions', `${selectedCell.closedTrades ?? 0} closed · ${selectedCell.openPositions ?? 0} open`, null]
        ];
        for (const [label, value, tone] of entries) {
            const group = document.createElement('div');
            const dt = document.createElement('dt');
            dt.textContent = label;
            const dd = document.createElement('dd');
            dd.textContent = value;
            if (tone) {
                dd.className = 'lab-matrix-return';
                dd.dataset.tone = tone;
            }
            group.append(dt, dd);
            list.append(group);
        }
        section.append(list);

        if (selectedCell.unavailableReason) {
            const reason = document.createElement('p');
            reason.className = 'note lab-matrix-reason';
            reason.textContent = selectedCell.unavailableReason;
            section.append(reason);
        }
        return section;
    }

    // Builds the plain-English rejection funnel with native progress bars and canonical audit codes.
    function renderDrawerRejections(row) {
        const section = document.createElement('section');
        section.className = 'lab-drawer-section';
        const heading = document.createElement('h3');
        heading.textContent = 'Top rejections funnel';
        section.append(heading);

        const rejections = row.topRejections || [];
        if (!rejections.length) {
            const none = document.createElement('p');
            none.className = 'note';
            none.textContent = 'No strategy or eligibility rejections recorded for this job.';
            section.append(none);
            return section;
        }

        const maxCount = Math.max(1, ...rejections.map(item => Number(item.count) || 0));
        const list = document.createElement('ul');
        list.className = 'lab-drawer-funnel';
        for (const item of rejections) {
            const li = document.createElement('li');
            const rowHead = document.createElement('div');
            rowHead.className = 'lab-drawer-funnel-head';
            const label = document.createElement('strong');
            label.textContent = rejectionText(item);
            const count = document.createElement('span');
            count.className = 'num';
            count.textContent = String(item.count);
            rowHead.append(label, count);

            const bar = document.createElement('progress');
            bar.className = 'lab-drawer-progress';
            bar.max = maxCount;
            bar.value = Math.max(0, Number(item.count) || 0);
            bar.setAttribute('aria-label', `${rejectionText(item)}: ${item.count}`);

            const code = document.createElement('code');
            code.className = 'lab-drawer-code';
            code.textContent = item.code;

            li.append(rowHead, bar, code);
            list.append(li);
        }
        section.append(list);
        return section;
    }

    // Builds the drawer's trade section for one month and fills it from GET .../jobs/{jobId}/trades?month=yyyy-MM, so every
    // month shows its own trades, newest first; monthly % comes only from the stored MonthlyRunResult records. A reply for an older drawer opening is ignored.
    function renderDrawerSampleTrades(row, selectedMonth, selectedCell) {
        const section = document.createElement('section');
        section.className = 'lab-drawer-section';
        const heading = document.createElement('h3');
        heading.textContent = selectedMonth ? `Trades that exited in ${selectedMonth}` : 'Trades';
        const caveat = document.createElement('p');
        caveat.className = 'note';
        caveat.textContent = 'Newest first. Monthly % returns above come only from the stored monthly records.';
        const status = document.createElement('p');
        status.className = 'note';
        status.textContent = selectedMonth ? 'Loading trades…' : 'Choose a month to see its trades.';
        section.append(heading, caveat, status);
        const planId = shownPlanId;
        if (!selectedMonth || !planId || !row.jobId) return section;
        const seq = ++drawerTradesSeq;
        void window.tradingFlowFetch(`/api/v1/backtests/plans/${encodeURIComponent(planId)}/jobs/${encodeURIComponent(row.jobId)}/trades?month=${encodeURIComponent(selectedMonth)}`,
            { credentials: 'same-origin', cache: 'no-store' })
            .then(response => response.ok ? response.json() : Promise.reject(new Error('HTTP ' + response.status)))
            .then(answer => {
                if (seq !== drawerTradesSeq) return;
                status.remove();
                const list = answer?.trades || [];
                const closed = Number(selectedCell?.closedTrades);
                const mismatch = describeTradeCountMismatch(answer, list.length, closed, selectedMonth);
                if (mismatch) {
                    const note = document.createElement('p');
                    note.className = 'note';
                    note.textContent = mismatch;
                    section.append(note);
                }
                section.append(renderDrawerTradeTable(list, selectedMonth, closed));
            })
            .catch(error => {
                if (seq !== drawerTradesSeq) return;
                status.textContent = 'Trades could not be loaded (' + (error?.message || 'request failed') + ').';
            });
        return section;
    }

    // Explains a list that differs from the monthly record's closed-trade count: the published result should match it, while
    // the job's stored view keeps only the run's most recent trades. Empty when they agree.
    function describeTradeCountMismatch(answer, listed, closed, month) {
        if (!Number.isFinite(closed) || closed === listed) return '';
        return answer?.fromPublishedResult
            ? `The published result lists ${listed} trades that exited in ${month}; the monthly record counts ${closed}.`
            : `Showing ${listed} of ${closed} trades closed in ${month}. Only the run's most recent trades are stored with the job, and its full result could not be used: ${answer?.unavailableReason || 'reason not given'}`;
    }

    // Renders the month's trades as a table, or a plain note when none exited that month.
    function renderDrawerTradeTable(samples, selectedMonth, closedInMonth) {
        if (!samples.length && closedInMonth > 0) return document.createDocumentFragment();
        if (!samples.length) {
            const empty = document.createElement('p');
            empty.className = 'note';
            empty.textContent = `No completed trades exited in ${selectedMonth}.`;
            return empty;
        }

        const scroller = document.createElement('div');
        scroller.className = 'table-scroll';
        const table = document.createElement('table');
        table.className = 'grid-table lab-drawer-trades-table';
        const thead = document.createElement('thead');
        const headRow = document.createElement('tr');
        for (const [label, isNum] of [['Ticker', false], ['Side', false], ['Entry', true], ['Exit', true], ['Exit reason', false], ['R', true], ['Net', true]]) {
            const th = document.createElement('th');
            th.scope = 'col';
            if (isNum) th.className = 'num';
            th.textContent = label;
            headRow.append(th);
        }
        thead.append(headRow);
        const tbody = document.createElement('tbody');
        for (const trade of samples) {
            const tr = document.createElement('tr');
            const rVal = trade.rMultiple != null && Number.isFinite(Number(trade.rMultiple))
                ? (Number(trade.rMultiple) > 0 ? '+' : Number(trade.rMultiple) < 0 ? '\u2212' : '') + Math.abs(Number(trade.rMultiple)).toFixed(2) + 'R'
                : 'no stop';
            const cells = [
                [trade.ticker, false, null],
                [trade.direction, false, null],
                [formatDrawerMoney(trade.entryPrice), true, null],
                [formatDrawerMoney(trade.exitPrice), true, null],
                [trade.exitReason || '—', false, null],
                [rVal, true, trade.rMultiple == null ? null : Number(trade.rMultiple) > 0 ? 'up' : Number(trade.rMultiple) < 0 ? 'down' : 'flat'],
                [formatDrawerMoney(trade.netProfit, true), true, Number(trade.netProfit) > 0 ? 'up' : Number(trade.netProfit) < 0 ? 'down' : 'flat']
            ];
            for (const [text, isNum, tone] of cells) {
                const td = document.createElement('td');
                if (isNum) td.className = 'num';
                if (tone) {
                    const span = document.createElement('span');
                    span.className = 'lab-matrix-return';
                    span.dataset.tone = tone;
                    span.textContent = text;
                    td.append(span);
                } else {
                    td.textContent = text;
                }
                tr.append(td);
            }
            tbody.append(tr);
        }
        table.append(thead, tbody);
        scroller.append(table);
        return scroller;
    }

    // Opens the Strategy Lab slide-over diagnostics drawer for one strategy/cohort/batch row and optional NY month.
    function openMatrixDrawer(row, month = null) {
        if (!matrixDrawer || !matrixDrawerBody) return;
        const cells = row.cells || [];
        const selectedCell = month ? cells.find(c => c.month === month) || null : (cells[cells.length - 1] || null);
        const selectedMonth = month || selectedCell?.month || null;

        if (matrixDrawerTitle) {
            matrixDrawerTitle.textContent = `${row.strategyName || row.strategyId}${selectedMonth ? ' · ' + selectedMonth : ''}`;
        }
        if (matrixDrawerIdentity) {
            const parts = [
                row.strategyId,
                row.semanticVersion ? 'v' + row.semanticVersion : '',
                row.cohortName,
                row.contentSha256 ? 'SHA-256 ' + row.contentSha256 : '',
                row.createdAtUtc ? 'Batch ' + formatBatchStamp(row.createdAtUtc) : '',
                'Status ' + row.status
            ].filter(Boolean);
            matrixDrawerIdentity.textContent = parts.join(' · ');
        }
        if (matrixDrawerReport) {
            const params = new URLSearchParams({ jobId: row.jobId });
            if (selectedMonth) params.set('month', selectedMonth);
            matrixDrawerReport.href = '/Backtests?' + params.toString();
        }

        matrixDrawerBody.replaceChildren(
            renderDrawerMonthMetrics(row, selectedCell, selectedMonth),
            renderDrawerRejections(row),
            renderDrawerSampleTrades(row, selectedMonth, selectedCell)
        );
        if (!matrixDrawer.open) matrixDrawer.showModal();
    }

    matrixDrawerClose?.addEventListener('click', closeMatrixDrawer);
    matrixDrawer?.addEventListener('click', event => {
        if (event.target === matrixDrawer) closeMatrixDrawer();
    });

    // Cancels any in-flight matrix fetch and hides the matrix panel so stale responses cannot reopen it.
    function hideSavedPlanMatrix() {
        matrixRequestId++;
        closeMatrixDrawer();
        if (matrixPanel) matrixPanel.hidden = true;
        matrixBody?.replaceChildren();
    }

    // Translates the server valuation basis token into a short readable subtitle, returning null when unknown/unavailable.
    function valuationSubtitle(basis) {
        const value = String(basis || '').trim();
        if (!value || value.toLowerCase() === 'unavailable') return null;
        if (value.startsWith('marked_to_market_')) return 'Marked-to-market';
        if (value === 'realized_closed_trade_equity') return 'Realized closed-trade';
        return value.replaceAll('_', ' ');
    }

    // Formats a UTC creation timestamp compactly so repeated start batches of the same strategy/cohort stay distinct.
    function formatBatchStamp(value) {
        if (!value) return '';
        const stamp = new Date(value);
        return Number.isFinite(stamp.getTime()) ? stamp.toISOString().slice(0, 16).replace('T', ' ') + 'Z' : '';
    }

    // Renders the read-only Strategy × Month comparison matrix using text nodes only; never pools or accumulates returns.
    function renderSavedPlanMatrix(matrix) {
        if (!matrixPanel || !matrixBody) return;
        shownPlanId = matrix.planId;
        matrixBody.replaceChildren();
        if (matrixTitle) matrixTitle.textContent = `Saved plan ${matrix.planId} · ${matrix.runName}`;
        matrixPanel.hidden = false;
        const rows = matrix.rows || [];
        const months = matrix.months || [];
        if (!rows.length) {
            const empty = document.createElement('p');
            empty.className = 'empty-state';
            empty.textContent = 'No executions have been recorded for this saved plan yet.';
            matrixBody.append(empty);
            return;
        }

        const scroller = document.createElement('div');
        scroller.className = 'table-scroll';
        scroller.setAttribute('role', 'region');
        scroller.setAttribute('aria-label', 'Saved plan monthly returns matrix');
        scroller.tabIndex = 0;

        const table = document.createElement('table');
        table.className = 'grid-table lab-matrix-table';
        const thead = document.createElement('thead');
        const headRow = document.createElement('tr');
        for (const [label, isNumeric] of [['Strategy / version / cohort / batch', false], ['Status', false], ...months.map(m => [m, true]), ['Top rejections', false]]) {
            const th = document.createElement('th');
            th.scope = 'col';
            if (isNumeric) th.className = 'num';
            th.textContent = label;
            headRow.append(th);
        }
        thead.append(headRow);
        table.append(thead);

        const tbody = document.createElement('tbody');
        for (const row of rows) {
            const tr = document.createElement('tr');
            const strategyCell = document.createElement('td');
            const link = document.createElement('a');
            link.href = '/Backtests?jobId=' + encodeURIComponent(row.jobId);
            const strong = document.createElement('strong');
            strong.textContent = row.strategyName || row.strategyId;
            link.append(strong);
            const shortHash = row.contentSha256 ? String(row.contentSha256).slice(0, 8) : '';
            const batchStamp = formatBatchStamp(row.createdAtUtc);
            const meta = document.createElement('span');
            meta.className = 'cell-sub';
            meta.textContent = [row.semanticVersion ? 'v' + row.semanticVersion : '', row.cohortName, shortHash, batchStamp].filter(Boolean).join(' · ');
            const metaTitleParts = [];
            if (row.contentSha256) metaTitleParts.push('SHA-256 ' + row.contentSha256);
            if (row.createdAtUtc) metaTitleParts.push('Batch created ' + row.createdAtUtc);
            if (metaTitleParts.length) meta.title = metaTitleParts.join(' · ');
            strategyCell.append(link, meta);
            tr.append(strategyCell);

            const statusCell = document.createElement('td');
            const statusTag = document.createElement('span');
            statusTag.className = 'tag ' + (row.status === 'completed' ? 'tag-neutral' : 'tag-accent');
            statusTag.textContent = row.status;
            statusCell.append(statusTag);
            if (row.monthlyUnavailableReason) {
                const reasonNote = document.createElement('span');
                reasonNote.className = 'cell-sub lab-matrix-reason';
                reasonNote.textContent = row.monthlyUnavailableReason;
                statusCell.append(reasonNote);
            }
            tr.append(statusCell);

            const byMonth = new Map((row.cells || []).map(cell => [cell.month, cell]));
            for (const month of months) {
                const td = document.createElement('td');
                td.className = 'num lab-matrix-cell';
                const cell = byMonth.get(month);
                if (!cell) {
                    const missing = document.createElement('span');
                    missing.className = 'note';
                    missing.textContent = '—';
                    if (row.monthlyUnavailableReason) td.title = row.monthlyUnavailableReason;
                    td.append(missing);
                } else {
                    const cellButton = document.createElement('button');
                    cellButton.type = 'button';
                    cellButton.className = 'lab-matrix-cell-btn';
                    cellButton.setAttribute('aria-label', `Inspect ${row.strategyName || row.strategyId} ${month}`);
                    const formatted = formatMatrixReturn(cell);
                    const value = document.createElement('span');
                    value.className = 'lab-matrix-return';
                    value.dataset.tone = formatted.tone;
                    value.textContent = formatted.text;
                    cellButton.append(value);
                    if (cell.partialMonth) {
                        const partial = document.createElement('span');
                        partial.className = 'lab-matrix-partial';
                        partial.textContent = ' (partial)';
                        cellButton.append(partial);
                    }
                    const basisLabel = valuationSubtitle(cell.valuationBasis);
                    const basis = document.createElement('span');
                    basis.className = 'cell-sub lab-matrix-basis';
                    basis.textContent = basisLabel || 'Valuation unavailable';
                    basis.title = cell.valuationBasis || '';
                    cellButton.append(basis);
                    if (cell.unavailableReason) {
                        const cellReason = document.createElement('span');
                        cellReason.className = 'cell-sub lab-matrix-reason';
                        cellReason.textContent = cell.unavailableReason;
                        cellButton.append(cellReason);
                    }
                    cellButton.addEventListener('click', () => openMatrixDrawer(row, month));
                    td.append(cellButton);
                    const details = [];
                    if (cell.openingEquity != null && cell.closingEquity != null) {
                        details.push(`Equity ${Number(cell.openingEquity).toFixed(2)} -> ${Number(cell.closingEquity).toFixed(2)}`);
                    }
                    if (cell.maxDrawdownPct != null) details.push(`Max DD ${Number(cell.maxDrawdownPct).toFixed(2)}%`);
                    if (cell.closedTrades != null || cell.openPositions != null) {
                        details.push(`Closed ${cell.closedTrades ?? 0} · Open ${cell.openPositions ?? 0}`);
                    }
                    if (details.length) td.title = details.join(' · ');
                }
                tr.append(td);
            }

            const diagCell = document.createElement('td');
            const diagActions = document.createElement('div');
            diagActions.className = 'lab-matrix-diag-actions';
            const inspectButton = document.createElement('button');
            inspectButton.type = 'button';
            inspectButton.className = 'btn btn-secondary btn-sm lab-matrix-inspect-btn';
            inspectButton.textContent = 'Inspect';
            inspectButton.addEventListener('click', () => openMatrixDrawer(row, null));
            diagActions.append(inspectButton);
            const rejections = row.topRejections || [];
            if (!rejections.length) {
                const none = document.createElement('span');
                none.className = 'note';
                none.textContent = 'None';
                diagActions.append(none);
                diagCell.append(diagActions);
            } else {
                const details = document.createElement('details');
                details.className = 'lab-matrix-rejections';
                const summary = document.createElement('summary');
                summary.textContent = `${rejections.length} reason${rejections.length === 1 ? '' : 's'}`;
                const list = document.createElement('ul');
                for (const item of rejections) {
                    const li = document.createElement('li');
                    li.title = item.code;
                    li.textContent = `${rejectionText(item)} (${item.count})`;
                    list.append(li);
                }
                details.append(summary, list);
                diagCell.append(diagActions, details);
            }
            tr.append(diagCell);
            tbody.append(tr);
        }

        table.append(tbody);
        scroller.append(table);
        matrixBody.append(scroller);
    }

    // Loads the read-only monthly comparison matrix for one saved plan while ignoring superseded responses.
    async function loadSavedPlanMatrix(planId) {
        if (!matrixPanel || !matrixBody) return;
        const parsed = Number(planId);
        if (!Number.isInteger(parsed) || parsed <= 0) {
            hideSavedPlanMatrix();
            return;
        }
        const requestId = ++matrixRequestId;
        try {
            const matrix = await api(`backtests/plans/${encodeURIComponent(parsed)}/monthly-matrix`);
            if (requestId !== matrixRequestId || !matrixPanel.isConnected) return;
            renderSavedPlanMatrix(matrix);
        } catch (error) {
            if (requestId !== matrixRequestId || error?.name === 'AbortError') return;
            matrixBody.replaceChildren();
            const note = document.createElement('p');
            note.className = 'note';
            note.textContent = error.message || 'Saved plan monthly matrix could not be loaded.';
            matrixBody.append(note);
            matrixPanel.hidden = false;
        }
    }

    // Drops the matrix and any answer still on its way when the page is left.
    function discard() {
        matrixRequestId++;
        matrixBody?.replaceChildren();
        if (matrixPanel) matrixPanel.hidden = true;
        closeMatrixDrawer();
    }
    // The plan the server rendered this page for, when there is one.
    function currentPlanId() {
        return matrixPanel?.dataset.currentPlanId;
    }
    // Shows the page's own plan again, or nothing when it has none.
    function showCurrentPlan() {
        if (currentPlanId()) void loadSavedPlanMatrix(currentPlanId());
        else hideSavedPlanMatrix();
    }

    return { load: loadSavedPlanMatrix, hide: hideSavedPlanMatrix, discard, currentPlanId, showCurrentPlan };
};
