/* Shared run commands; the request survives transport retries until the operator edits the form. */
(() => {
    const kind = location.pathname.toLowerCase() === "/paper" ? "paper" : "backtest";
    const form = kind === "backtest" ? document.querySelector('form.lab-rail') : document.querySelector('form[data-run-workflow]');
    if (!form) return;
    let pending;
    let savedPlan;
    let savedPlans = [];
    const startKeys = new Map();
    const planPreview = form.querySelector('[data-backtest-plan-preview]');
    const planPicker = form.querySelector('[data-saved-backtest-plan]');
    const startPlanButton = form.querySelector('[data-start-backtest-plan]');
    const savePlanButton = form.querySelector('[data-backtest-action="save"]');
    const paperStockReview = form.querySelector('[data-paper-stock-review]');
    const paperStart = form.querySelector('[data-paper-action="start"]');
    let previewed = false;
    if (savePlanButton) savePlanButton.disabled = true;
    const evidencePicker = form.querySelector('[data-evidence-picker]');
    const evidenceSummary = form.querySelector('[data-evidence-summary]');
    const evidenceAvailability = form.querySelector('[data-evidence-availability]');
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
    let evidenceChoices = [];
    const lifetime = new AbortController();
    // Discards backend-bound choices on departure, including documents retained by browser history.
    function leavePage() {
        lifetime.abort(); pending = undefined; savedPlan = undefined; savedPlans = []; evidenceChoices = []; previewed = false;
        matrixRequestId++;
        startKeys.clear();
        if (evidencePicker) evidencePicker.value = '';
        if (startPlanButton) startPlanButton.disabled = true;
        if (savePlanButton) savePlanButton.disabled = true;
        planPreview?.replaceChildren(); evidenceSummary?.replaceChildren();
        if (matrixBody) matrixBody.replaceChildren();
        if (matrixPanel) matrixPanel.hidden = true;
        closeMatrixDrawer();
        clearPaperStockReview();
    }
    window.addEventListener('pagehide', leavePage);
    // Browser-history restoration reloads the authenticated server catalog instead of reviving old-account choices.
    window.addEventListener('pageshow', event => { if (event.persisted) location.reload(); });
    const preparationKey=kind==='paper'?new URLSearchParams(location.search).get('preparationKey'):null;
    if(preparationKey){
        api('preparation/accepted?key='+encodeURIComponent(preparationKey)).then(review=>{
            const label=document.createElement('p');label.textContent='Accepted preparation snapshot '+review.universeSnapshotId+' · '+review.symbols.join(', ')+' · expires '+new Date(review.expiresAtUtc).toLocaleString()+'. This bound membership replaces the source selectors below.';form.prepend(label);
            for(const key of ['WishlistId','ScreenerFilter','UniverseSource','PaperMembershipMode'])form.querySelectorAll('[name="'+key+'"]').forEach(x=>x.disabled=true);
        }).catch(error=>{status.textContent=error.message;});
    }
    let busy = false;
    const status = document.createElement("p");
    status.setAttribute("role", "status");
    status.dataset.runWorkflowStatus = "";
    form.append(status);
    const validation = form.querySelector('[data-run-validation]');

    // Reset the confirmation whenever its frozen browser command is discarded.
    function clearPaperStockReview() {
        if (!paperStockReview || !paperStart) return;
        paperStockReview.replaceChildren();
        paperStockReview.hidden = true;
        paperStart.hidden = true;
    }

    // The reviewed union and mode come from the server response that the start command names.
    function showPaperStockReview(universe) {
        const review = universe.paperMembership;
        const summary = document.createElement('p');
        const mode = review?.mode === 'refresh_selected_sources' ? 'Refresh selected sources' : 'Fixed at launch';
        const symbols = universe.members?.map(member => member.symbol) ?? universe.symbols;
        summary.textContent = 'Stock membership · ' + mode + ' · ' + symbols.length + ' stocks at launch';
        const sourceList = document.createElement('ul');
        for (const source of review?.sources ?? []) {
            const item = document.createElement('li');
            item.textContent = source.isBoundedObservation
                ? 'Finviz query ' + source.normalizedQuery + ' · ' + source.memberCount + ' stocks in the bounded observation'
                : 'Stock group · ' + source.memberCount + ' stocks';
            sourceList.append(item);
        }
        const members = document.createElement('p');
        members.textContent = symbols.join(' · ');
        paperStockReview.replaceChildren(summary, sourceList, members);
        paperStockReview.hidden = false;
        paperStart.hidden = false;
    }
    function clearValidation() {
        if (!validation) return;
        validation.hidden = true; validation.replaceChildren();
        form.querySelectorAll('[data-run-field-error]').forEach(message => message.remove());
        form.querySelectorAll('[aria-invalid="true"]').forEach(field => {
            field.removeAttribute('aria-invalid');
            const descriptions = (field.getAttribute('aria-describedby') || '').split(' ').filter(id => !id.endsWith('-run-error')).join(' ');
            if (descriptions) field.setAttribute('aria-describedby', descriptions);
            else field.removeAttribute('aria-describedby');
        });
    }
    function showValidation(errors) {
        if (!validation) return;
        clearValidation();
        const heading = document.createElement('p'); heading.textContent = 'Review these fields before starting the run:';
        const list = document.createElement('ul');
        for (const [index, [name, message]] of errors.entries()) {
            const item = document.createElement('li'); item.textContent = message; list.append(item);
            const field = form.elements.namedItem(name);
            if (!(field instanceof HTMLElement)) continue;
            // Every enclosing configuration disclosure must expose a field that needs correction.
            for (let disclosure = field.closest('details'); disclosure; disclosure = disclosure.parentElement?.closest('details')) disclosure.open = true;
            const error = document.createElement('p'); error.id = name + '-' + index + '-run-error'; error.dataset.runFieldError = ''; error.className = 'note'; error.textContent = message;
            field.after(error); field.setAttribute('aria-invalid', 'true');
            field.setAttribute('aria-describedby', [field.getAttribute('aria-describedby'), error.id].filter(Boolean).join(' '));
        }
        validation.append(heading, list); validation.hidden = false;
        // A configuration may be collapsed after another run; errors must remain reachable.
        const disclosure = validation.closest('details'); if (disclosure) disclosure.open = true;
        validation.focus();
    }
    // Form edits discard the previous preview and command; saved-plan selection keeps its frozen settings.
    function edited(event) {
        if (event?.target === planPicker) return;
        pending = undefined; savedPlan = undefined; previewed = false;
        if (startPlanButton) startPlanButton.disabled = true;
        if (savePlanButton) savePlanButton.disabled = true;
        if (planPicker && planPicker.value) {
            planPicker.value = '';
            if (matrixPanel?.dataset.currentPlanId) void loadSavedPlanMatrix(matrixPanel.dataset.currentPlanId);
            else hideSavedPlanMatrix();
        }
        planPreview?.replaceChildren();
        clearPaperStockReview();
        clearValidation();
    }
    form.addEventListener("input", edited);
    form.addEventListener("change", edited);
    // A profile change displays that server profile's settings before the operator previews or saves them.
    function applyBacktestProfile(event) {
        const option = event.target.selectedOptions[0];
        if (!option) return;
        for (const [name, key] of [['StartingCapital', 'startingCapital'], ['AccountRiskBudgetPct', 'accountRisk'],
            ['MaxPositionNotionalPct', 'positionNotional'], ['MaxConcurrentPositions', 'openPositions'],
            ['LookbackDays', 'lookbackDays'], ['CachePolicy', 'cachePolicy']]) {
            const field = form.elements.namedItem(name);
            if (field && option.dataset[key] !== undefined) field.value = option.dataset[key];
        }
        syncProfileWindowPreset(option);
    }
    // Shows the "Profile window" preset only when the selected profile has a window, labelled with its dates.
    function syncProfileWindowPreset(option) {
        const button = form.querySelector('[data-window-preset="profile"]');
        if (!button) return;
        const start = option?.dataset.windowStart || '', end = option?.dataset.windowEnd || '';
        button.hidden = !(start && end);
        button.dataset.windowStart = start;
        button.dataset.windowEnd = end;
        button.textContent = start && end ? 'Profile window ' + start.slice(0, 10) + ' – ' + end.slice(0, 10) : 'Profile window';
        button.title = 'Test window of the selected backtest profile: its own dates, or its rolling number of New York calendar days up to today.';
    }
    // Lists the registered held-back test periods of the selected strategies and warns when the chosen dates overlap one.
    // A warning only: it does not stop the run.
    function syncHeldBackPeriods() {
        const block = form.querySelector('[data-held-back]');
        const warning = block?.querySelector('[data-held-back-warning]');
        if (!warning) return;
        const selected = new Set([...form.querySelectorAll('input[name="StrategyPaths"]:checked')].map(box => box.dataset.strategyId));
        const { start, end } = chosenTestWindow(new FormData(form));
        const overlapping = [];
        let shown = 0;
        for (const period of block.querySelectorAll('[data-held-back-period]')) {
            period.hidden = !selected.has(period.dataset.strategyId);
            if (period.hidden) continue;
            shown++;
            if (start && end && start < new Date(period.dataset.end) && end > new Date(period.dataset.start)) overlapping.push(period.dataset.strategyId);
        }
        block.querySelector('[data-held-back-none]').hidden = shown > 0 || selected.size === 0;
        warning.hidden = overlapping.length === 0;
        const message = overlapping.length
            ? 'The chosen dates overlap the held-back test period of ' + [...new Set(overlapping)].join(', ') + '. Results seen there can no longer serve as an untouched test.'
            : '';
        // Rewriting an unchanged alert would make screen readers repeat it on every keystroke.
        if (warning.textContent !== message) warning.textContent = message;
    }
    if (kind === 'backtest') form.querySelector('#SelectedConfigPath')?.addEventListener('change', applyBacktestProfile);

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
        const planId = matrixPanel?.dataset.currentPlanId;
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

    // Wires 1-click Long / Short / Clear strategy selection buttons and notifies the form change handler.
    function wireStrategyDirectionHelpers() {
        for (const button of form.querySelectorAll('[data-strategy-select]')) {
            button.addEventListener('click', () => {
                const mode = button.dataset.strategySelect;
                let lastChanged = null;
                for (const option of form.querySelectorAll('.lab-strategy-option')) {
                    const checkbox = option.querySelector('input[type="checkbox"][name="StrategyPaths"]');
                    if (!checkbox) continue;
                    const direction = String(option.dataset.direction || '').toLowerCase();
                    const nextChecked = mode === 'clear' ? false : direction === mode;
                    if (checkbox.checked !== nextChecked) {
                        checkbox.checked = nextChecked;
                        lastChanged = checkbox;
                    }
                }
                (lastChanged || form).dispatchEvent(new Event('change', { bubbles: true }));
            });
        }
    }

    // Wires the quick UTC test-window buttons (the selected profile's own window and Clear dates) and notifies validation/form state.
    function wireWindowPresetHelpers() {
        const startInput = form.elements.namedItem('TestWindowStartUtc');
        const endInput = form.elements.namedItem('TestWindowEndUtc');
        if (!(startInput instanceof HTMLInputElement) || !(endInput instanceof HTMLInputElement)) return;
        for (const button of form.querySelectorAll('[data-window-preset]')) {
            button.addEventListener('click', () => {
                const preset = button.dataset.windowPreset;
                startInput.value = preset === 'clear' ? '' : (button.dataset.windowStart || '');
                endInput.value = preset === 'clear' ? '' : (button.dataset.windowEnd || '');
                startInput.dispatchEvent(new Event('input', { bubbles: true }));
                endInput.dispatchEvent(new Event('change', { bubbles: true }));
            });
        }
    }

    // Pre-fills 1000 stocks per group, more than any published research screen holds, so a research screen runs as one
    // group unless the operator types a size or manual groups. A "whole screen" marker, not a tuning number.
    function applyResearchScreenGroupDefault() {
        const sourceSelect = form.querySelector('#StockSource');
        const groupSize = form.elements.namedItem('GroupSize');
        const manualCohorts = form.elements.namedItem('ManualCohorts');
        if (!(sourceSelect instanceof HTMLSelectElement) || !(groupSize instanceof HTMLInputElement)) return;
        const selected = sourceSelect.selectedOptions[0];
        const isResearchScreen = selected?.dataset.sourceKind === 'research_screen' || String(selected?.value || '').startsWith('research_screen:');
        const hasGroupSize = String(groupSize.value || '').trim().length > 0;
        const hasManual = manualCohorts instanceof HTMLTextAreaElement && String(manualCohorts.value || '').trim().length > 0;
        if (isResearchScreen && !hasGroupSize && !hasManual) {
            groupSize.value = '1000';
        }
    }

    if (kind === 'backtest') {
        wireStrategyDirectionHelpers();
        wireWindowPresetHelpers();
        syncProfileWindowPreset(form.querySelector('#SelectedConfigPath')?.selectedOptions[0]);
        form.addEventListener('input', syncHeldBackPeriods);
        form.addEventListener('change', syncHeldBackPeriods);
        syncHeldBackPeriods();
        form.querySelector('#StockSource')?.addEventListener('change', applyResearchScreenGroupDefault);
        applyResearchScreenGroupDefault();
        if (matrixPanel?.dataset.currentPlanId) {
            void loadSavedPlanMatrix(matrixPanel.dataset.currentPlanId);
        }
    }
    function utcInput(value) {
        const parts = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?$/.exec(value);
        if (!parts) return null;
        const [year, month, day, hour, minute, second] = parts.slice(1).map(part => Number(part || 0));
        const result = new Date(Date.UTC(year, month - 1, day, hour, minute, second));
        return result.getUTCFullYear() === year && result.getUTCMonth() === month - 1 && result.getUTCDate() === day && result.getUTCHours() === hour && result.getUTCMinutes() === minute && result.getUTCSeconds() === second ? result : null;
    }
    // Reads the chosen test window without showing anything: both dates, or the lookback ending now, with any entry errors.
    function chosenTestWindow(data) {
        const get = name => String(data.get(name) || '').trim();
        const errors = [], from = get('TestWindowStartUtc'), to = get('TestWindowEndUtc');
        let start, end;
        if (from || to) {
            start = utcInput(from); end = utcInput(to);
            if (!start) errors.push(['TestWindowStartUtc', 'Enter a valid From UTC date and time.']);
            if (!end) errors.push(['TestWindowEndUtc', 'Enter a valid To UTC date and time.']);
            if (start && end && end <= start) errors.push(['TestWindowEndUtc', 'To UTC must be later than From UTC.']);
            if (end && end > new Date()) errors.push(['TestWindowEndUtc', 'To UTC cannot be in the future.']);
        } else {
            const days = Number(get('LookbackDays'));
            if (!Number.isInteger(days) || days < 5 || days > 1825) errors.push(['LookbackDays', 'Fallback lookback must be a whole number from 5 to 1825 days.']);
            else { end = new Date(); start = new Date(end.getTime() - days * 86400000); }
        }
        return { start, end, errors };
    }
    // Validates the whole backtest form, shows any errors, and returns the window to run, or null.
    function backtestWindow(data) {
        const get = name => String(data.get(name) || '').trim();
        const { start, end, errors } = chosenTestWindow(data);
        if (get('RunName') && !/^[A-Za-z0-9_-]{1,100}$/.test(get('RunName')))
            errors.push(['RunName', 'Run name must use at most 100 letters, digits, hyphens or underscores.']);
        for (const [name, label, maximum, whole] of [
            ['StartingCapital', 'Starting capital in USD', Infinity, false],
            ['AccountRiskBudgetPct', 'Account risk as a percentage of equity', 100, false],
            ['MaxPositionNotionalPct', 'Maximum position notional as a percentage of equity', 100, false],
            ['MaxConcurrentPositions', 'Maximum concurrent positions', 1000, true]
        ]) {
            const value = Number(get(name));
            if (!Number.isFinite(value) || value <= 0 || value > maximum || (whole && !Number.isInteger(value)))
                errors.push([name, label + (whole ? ' must be a whole number from 1 to 1000.' : maximum === 100 ? ' must be greater than zero and at most 100%.' : ' must be greater than zero.')]);
        }
        // Reuse the existing group parser before capture so its error can reveal the collapsed field.
        if (get('ManualCohorts')) {
            try { manualGroups(get('ManualCohorts')); }
            catch (error) { errors.push(['ManualCohorts', error.message]); }
        }
        if (!data.getAll('StrategyPaths').length) errors.push(['', 'Select at least one currently authorized research strategy.']);
        if (!get('StockSource')) errors.push(['StockSource', 'Select a wishlist or saved Finviz stock source.']);
        if (errors.length) { showValidation(errors); return null; }
        clearValidation();
        return { start: start.toISOString(), end: end.toISOString() };
    }
    // Binds requests to the current document so a departed account cannot publish a late response.
    async function api(path, body) {
        const response = await window.tradingFlowFetch("/api/v1/" + path, body ? {
            method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body), signal: lifetime.signal
        } : { cache: "no-store", signal: lifetime.signal });
        if (response.status === 401 || response.status === 403) leavePage();
        if (!response.ok) {
            let message = "Request failed (" + response.status + ")";
            try { const error = await response.json(); message = error.message || message; } catch {}
            throw new Error(message);
        }
        return response.json();
    }
    // Formats only server evidence facts; neither availability nor classification is inferred in the browser.
    function evidenceLines(summary) {
        if (!summary) return [];
        if (summary.historicalNewsDiagnostic) return diagnosticEvidenceLines(summary);
        return [
            `Evidence: ${summary.reference.id}`,
            `Manifest SHA-256: ${summary.reference.manifestSha256}`,
            `Provider: ${summary.provider} · Version: ${summary.version}`,
            `Taxonomy versions: ${summary.taxonomyVersions.join(', ') || 'None reported'}`,
            `Classifier versions: ${summary.classifierVersions.join(', ') || 'None reported'}`,
            `Evaluation SHA-256: ${summary.evaluationSha256s.join(', ') || 'None reported'}`,
            `Availability basis: ${summary.availabilityBasis.join(', ') || 'None reported'}`,
            `Coverage scopes: ${summary.coverageScopes.join(', ') || 'None reported'}`,
            `Event lookback days: ${summary.eventLookbackDays ?? 'Not specified'}`,
            `Maximum coverage age hours: ${summary.maxCoverageAgeHours ?? 'Not specified'}`,
            `Dependency graph SHA-256: ${summary.dependencyGraphSha256}`,
            `Unknown reasons: ${summary.unknownReasons.join('; ') || 'None reported'}`
        ];
    }
    // Displays the server's research assumption alongside original receipt clocks; it never computes eligibility.
    function diagnosticEvidenceLines(summary) {
        const diagnostic = summary.historicalNewsDiagnostic;
        const assessment = diagnostic.companyAssessments;
        return [
            'Historical news diagnostic — provider update + 24h, then next session',
            `Evidence: ${summary.reference.id}`,
            `Manifest SHA-256: ${summary.reference.manifestSha256}`,
            `Policy: ${diagnostic.policyId}`,
            `Policy SHA-256: ${diagnostic.policySha256}`,
            `Publication age: up to ${diagnostic.freshnessHours} hours`,
            `Actual receipts UTC: ${diagnostic.actualReceivedFromUtc} through ${diagnostic.actualReceivedThroughUtc}`,
            'Original historical availability and classifier accuracy are unproved. Research only.',
            `Query evidence: ${diagnostic.queryEvidenceNotes.join('; ') || 'None reported'}`,
            `Dependency graph SHA-256: ${summary.dependencyGraphSha256}`,
            `Unknown reasons: ${summary.unknownReasons.join('; ') || 'None reported'}`,
            ...(assessment ? [
                'Company news assessments — retrospective research; accuracy not yet measured',
                `Classifier: ${assessment.classifierVersion}`,
                `Model: ${assessment.model}@${assessment.modelRevision}`,
                `Model artifact SHA-256: ${assessment.modelArtifactSha256}`,
                `Company subject mapping: ${assessment.subjectMappingVersion}`,
                `Training-data cutoff bound UTC: ${assessment.trainingDataCutoffUtc || 'Unknown'}. ${assessment.trainingDataProvenance}`,
                `Actually assessed UTC: ${assessment.assessedFromUtc} through ${assessment.assessedThroughUtc}`,
                `Assessments: ${assessment.assessmentCount}. ${Object.entries(assessment.stateCounts).sort(([a], [b]) => a.localeCompare(b)).map(([key, value]) => `${key}: ${value}`).join(', ')}`,
                'Model confidence describes its text label, not the probability of a profitable trade.'
            ] : [])
        ];
    }
    // Uses text nodes so provider descriptions and unknown reasons cannot become markup.
    function renderEvidence(target, summary) {
        if (!target) return;
        target.replaceChildren();
        for (const line of evidenceLines(summary)) {
            const row = document.createElement('p'); row.textContent = line; target.append(row);
        }
    }
    // Sends only the immutable catalog reference, rejecting a removed or unavailable choice before capture.
    function selectedEvidence() {
        if (!evidencePicker?.value) return null;
        const choice = evidenceChoices.find(item => item.reference?.id === evidencePicker.value);
        if (!choice?.available || !choice.reference) throw new Error(choice?.unavailableReason || 'This evidence choice is no longer available. Reload the choices.');
        return { id: choice.reference.id, manifestSha256: choice.reference.manifestSha256 };
    }
    // Refreshes the server catalog without restoring a selection whose expected manifest bytes changed.
    async function loadEvidenceChoices() {
        if (!evidencePicker) return;
        const previous = evidenceChoices.find(item => item.reference?.id === evidencePicker.value)?.reference;
        const catalog = await api('catalog');
        evidenceChoices = catalog.evidenceChoices || [];
        evidencePicker.replaceChildren(new Option('No evidence selected', ''));
        for (const [index, choice] of evidenceChoices.entries()) {
            const option = new Option(choice.name + (choice.available ? '' : ' · Unavailable: ' + choice.unavailableReason), choice.reference?.id || `unavailable-${index}`);
            option.disabled = !choice.available || !choice.reference;
            evidencePicker.add(option);
        }
        const retained = previous && evidenceChoices.find(item => item.available && item.reference?.id === previous.id && item.reference.manifestSha256 === previous.manifestSha256);
        evidencePicker.value = retained ? retained.reference.id : '';
        showEvidenceChoice();
    }
    // Displays catalog availability reasons independently of whether an evidence choice can be selected.
    function showEvidenceChoice() {
        const choice = evidenceChoices.find(item => item.reference?.id === evidencePicker?.value);
        if (evidenceAvailability) evidenceAvailability.textContent = choice?.unavailableReason ||
            evidenceChoices.filter(item => !item.available).map(item => `${item.name}: ${item.unavailableReason}`).join(' ') ||
            (evidenceChoices.length ? 'Only server-approved immutable evidence can be selected.' : 'No approved decision-context evidence is available.');
        renderEvidence(evidenceSummary, choice?.summary);
    }
    evidencePicker?.addEventListener('change', showEvidenceChoice);
    if (evidencePicker) loadEvidenceChoices().catch(error => { evidenceAvailability.textContent = error.message; });
    // Reopens the job's frozen server summary beside its monthly results without recalculating evidence.
    async function loadJobEvidence() {
        const panel = document.querySelector('[data-job-evidence]');
        if (!panel) return;
        const id = panel.dataset.jobEvidence;
        const detail = await api(`workflow/jobs/${encodeURIComponent(id)}/details`);
        if (!panel.isConnected || detail.job.jobId.toLowerCase() !== id.toLowerCase()) return;
        const summary = detail.evidenceSummary || detail.job.evidenceSummary;
        renderEvidence(panel.querySelector('[data-job-evidence-summary]'), summary);
        panel.hidden = !summary;
    }
    if (kind === 'backtest') loadJobEvidence().catch(error => { status.textContent = error.message; });
    // Renders the preview's frozen server settings identically when the saved plan is reopened.
    function settingsLines(settings) {
        if (!settings) return [];
        const lines = [
            `Profile: ${settings.configurationKey}`,
            `Configuration SHA-256: ${settings.configurationSha256}`,
            `Fixed buy fee USD: ${settings.fixedBuyFee} · Fixed sell fee USD: ${settings.fixedSellFee}`,
            `SEC fee rate: ${settings.secFeeRate} · FINRA TAF per share USD: ${settings.finraTafPerShare} · FINRA TAF cap USD: ${settings.finraTafCap}`,
            `Maximum bar participation percent: ${settings.maxBarParticipationPct} · Impact bps at maximum participation: ${settings.impactBpsAtMaxParticipation}`,
            `Execution replay policy: ${settings.executionReplayPolicyId ?? 'Not specified'}`,
            `Strategy horizon: ${settings.horizon ?? 'swing'}`,
            `Cutoff policy: ${settings.cutoffPolicy ?? 'Not specified'}`,
            `Precommitted liquidation source UTC: ${settings.precommittedLiquidationSourceUtc ? new Date(settings.precommittedLiquidationSourceUtc).toISOString().slice(0, 19) + 'Z' : 'Not specified'}`
        ];
        if (settings.requiredMarketDataContext) lines.push(`Required market data: ${settings.requiredMarketDataContext}`);
        if (settings.accountLossReplay) lines.push(`Account-loss limits: ${settings.accountLossReplay}`);
        for (const strategy of settings.strategies) {
            lines.push(`Recipe: ${strategy.name} · ${strategyIdentity(strategy.identity)}`, `Adverse slippage bps: ${strategy.slippageBps}`);
            if (strategy.derivedFromIdentity) lines.push(`Parent: ${strategyIdentity(strategy.derivedFromIdentity)}`);
            if (strategy.admissionProfile) {
                const profile = strategy.admissionProfile;
                lines.push(`Admission: ${profile.profileId} ${profile.profileVersion}`, `Execution timing: ${profile.executionTimingPolicy} · Risk policy: ${profile.riskPolicy}`);
            }
        }
        return lines;
    }
    // Retains each full canonical hash when rendering recipe or original-parent identity.
    function strategyIdentity(identity) {
        return `${identity.strategyId} ${identity.semanticVersion} (${identity.contentSha256})`;
    }
    // Displays returned exclusions and effective portfolios before any execution is accepted.
    function showPlan(plan) {
        if (!planPreview) return;
        planPreview.replaceChildren();
        const heading = document.createElement('h3');
        heading.textContent = plan.planId ? 'Saved plan selections' : 'Selections to save';
        const description = document.createElement('p');
        description.textContent = `${plan.run.runName}: ${plan.executionCount} independent portfolios. ${plan.membershipDescription}`;
        const list = document.createElement('ul');
        for (const group of plan.cohorts) {
            const item = document.createElement('li');
            item.textContent = `${group.name}: ${group.symbols.join(', ')} · starting capital ${group.startingCapital} per strategy`;
            list.append(item);
        }
        const exclusions = document.createElement('p');
        exclusions.textContent = 'Excluded stocks: ' + (plan.excludedSymbols?.length ? plan.excludedSymbols.join(', ') : 'None');
        const selection = document.createElement('p');
        selection.textContent = describePlanSelection(plan);
        planPreview.append(heading, description, exclusions, selection, list);
        const strategies = document.createElement('p');
        strategies.textContent = 'Strategies: ' + plan.run.strategies.map(item => `${item.strategyId} ${item.semanticVersion} (${item.contentSha256})`).join('; ');
        planPreview.append(strategies);
        for (const line of settingsLines(plan.resolvedSettings)) {
            const row = document.createElement('p'); row.textContent = line; planPreview.append(row);
        }
        const evidence = document.createElement('div');
        renderEvidence(evidence, plan.evidenceSummary);
        if (!plan.evidenceSummary && plan.run.decisionContextEvidence) {
            const reference = plan.run.decisionContextEvidence;
            evidence.textContent = `Evidence: ${reference.id} · Manifest SHA-256: ${reference.manifestSha256}. Summary unavailable.`;
        }
        planPreview.append(evidence);
    }
    // Lists saved plans without automatically starting or changing their membership.
    async function loadPlans() {
        if (!planPicker) return;
        savedPlans = await api('backtests/plans');
        planPicker.replaceChildren(new Option('Select a saved plan', ''));
        for (const plan of savedPlans) planPicker.add(new Option(`${plan.run.runName} · ${plan.cohorts.length} groups · ${new Date(plan.savedAtUtc).toLocaleString()}`, String(plan.planId)));
    }
    // Reads an optional numeric field; a blank field means that rule is not applied.
    function optionalNumber(name) {
        const value = String(form.elements[name]?.value ?? '').trim();
        return value === '' ? null : Number(value);
    }
    // Builds the plan's per-day eligibility rules, or null when no minimum price is entered (the rules are off).
    function dailyEligibility() {
        const minPrice = optionalNumber('EligibilityMinPrice');
        if (minPrice === null) return null;
        if (optionalNumber('EligibilityAverageSessions') === null || optionalNumber('EligibilityMinHistorySessions') === null)
            throw new Error('Daily eligibility needs "Average over" and "Minimum history" when a minimum price is entered.');
        return { minPrice, priceBasis: form.elements.EligibilityPriceBasis.value,
            averageSessions: optionalNumber('EligibilityAverageSessions'), minHistorySessions: optionalNumber('EligibilityMinHistorySessions'),
            minAverageVolume: optionalNumber('EligibilityMinAverageVolume'), minAverageDollarVolume: optionalNumber('EligibilityMinAverageDollarVolume'),
            atrPeriod: optionalNumber('EligibilityAtrPeriod'), minAtr: optionalNumber('EligibilityMinAtr'),
            minAverageRangePct: optionalNumber('EligibilityMinAverageRangePct') };
    }
    // Describes the saved group order and daily eligibility so the reviewer sees exactly what the plan stores.
    function describePlanSelection(plan) {
        const order = plan.groupOrder === 'random' ? `Group order: random, seed ${plan.groupSeed}` : 'Group order: alphabetical';
        const rules = plan.run.eligibility;
        if (!rules) return `${order}. Daily eligibility: off (every stock may trade every day).`;
        const set = value => value !== null && value !== undefined;
        const parts = [`price at least $${rules.minPrice} (${rules.priceBasis === 'session_open' ? "that day's open" : 'previous close'})`,
            `averages over ${rules.averageSessions} sessions`, `at least ${rules.minHistorySessions} sessions of history`];
        if (set(rules.minAverageVolume)) parts.push(`average volume at least ${rules.minAverageVolume} shares`);
        if (set(rules.minAverageDollarVolume)) parts.push(`average dollar volume at least $${rules.minAverageDollarVolume}`);
        if (set(rules.minAtr)) parts.push(`ATR(${rules.atrPeriod}) above $${rules.minAtr}`);
        if (set(rules.minAverageRangePct)) parts.push(`ADR% at least ${rules.minAverageRangePct}%`);
        return `${order}. Daily eligibility from earlier sessions: ${parts.join(', ')}.`;
    }
    // Parses the human-entered rows; the server performs authoritative membership/capital validation.
    function manualGroups(value) {
        return value.split(/\r?\n/).filter(line => line.trim()).map(line => {
            const parts = line.split('|').map(part => part.trim());
            if (parts.length !== 3 || !parts[0] || !Number.isFinite(Number(parts[1])) || Number(parts[1]) <= 0)
                throw new Error('Enter each manual group as name | positive starting capital | comma-separated stock symbols.');
            return { name: parts[0], startingCapital: Number(parts[1]), symbols: parts[2].split(',').map(symbol => symbol.trim()).filter(Boolean) };
        });
    }
    planPicker?.addEventListener('change', () => {
        previewed = false;
        if (savePlanButton) savePlanButton.disabled = true;
        savedPlan = savedPlans.find(plan => String(plan.planId) === planPicker.value);
        startPlanButton.disabled = !savedPlan;
        if (savedPlan) {
            showPlan(savedPlan);
            void loadSavedPlanMatrix(savedPlan.planId);
        } else if (matrixPanel?.dataset.currentPlanId) {
            void loadSavedPlanMatrix(matrixPanel.dataset.currentPlanId);
        } else {
            hideSavedPlanMatrix();
        }
    });
    // A stable retry key prevents transport retries from creating duplicate portfolios.
    startPlanButton?.addEventListener('click', async () => {
        if (!savedPlan || busy) return;
        busy = true; startPlanButton.disabled = true;
        try {
            const id = savedPlan.planId;
            if (!startKeys.has(id)) startKeys.set(id, crypto.randomUUID());
            const response = await api(`backtests/plans/${id}/start`, { contractVersion: '1.0', idempotencyKey: startKeys.get(id) });
            status.textContent = `${response.jobs.length} independent strategy/group runs accepted.`;
            for (const job of response.jobs) {
                const link = document.createElement('a'); link.href = '/Backtests?jobId=' + job.jobId;
                link.textContent = ` ${job.cohort.cohortName} · ${job.cohort.strategy.strategyId} · ${job.status}`;
                status.append(document.createElement('br'), link);
            }
            void loadSavedPlanMatrix(id);
        } catch (error) { status.textContent = error.message; }
        finally { busy = false; startPlanButton.disabled = !savedPlan; }
    });
    if (planPicker) loadPlans().catch(error => { status.textContent = error.message; });
    // Captures form choices for the shared API, retaining run identity across transport retries.
    form.addEventListener("submit", async event => {
        if (kind === "backtest" && event.submitter?.formAction.includes("CancelBacktest")) {
            event.preventDefault();
            if (busy) return;
            busy = true;
            try {
                const id = new URL(event.submitter.formAction).searchParams.get("id");
                const result = await api("workflow/jobs/" + id + "/cancel", { contractVersion:"1.0", reason:"Operator requested cancellation." });
                status.textContent = result.status + " · cancellation requested";
            } catch (error) { status.textContent = error.message; }
            finally { busy = false; }
            return;
        }
        if (kind === "paper" && !event.submitter?.formAction.includes("RunLive")) return;
        event.preventDefault();
        if (busy) return;
        const savingPlan = kind === 'backtest' && event.submitter?.dataset.backtestAction === 'save';
        const confirmingPaper = kind === 'paper' && event.submitter?.dataset.paperAction === 'start';
        if (kind === 'paper' && confirmingPaper && !pending) {
            status.textContent = 'Preview the selected stocks before starting.';
            return;
        }
        if (kind === 'paper' && !confirmingPaper) {
            pending = undefined;
            clearPaperStockReview();
        }
        if (savingPlan && !previewed) { status.textContent = 'Preview the current fields and resolved costs before saving.'; return; }
        if (kind === 'backtest' && !savingPlan) {
            previewed = false;
            if (savePlanButton) savePlanButton.disabled = true;
        }
        const data = new FormData(form);
        const testWindow = kind === 'backtest' ? backtestWindow(data) : null;
        if (kind === 'backtest' && !testWindow) return;
        busy = true; form.inert = true;
        const button = event.submitter;
        if (button) button.disabled = true;
        status.textContent = "Capturing universe and checking strategy authorization…";
        try {
            if (!pending) {
                const get = key => String(data.get(key) || "");
                const mode = kind === "backtest" ? "backtest" : get("PaperExecutionMode") === "experiment" ? "paper_experiment" : "paper_shadow";
                const catalog = await api("catalog");
                const options = kind === "backtest" ? catalog.strategies : mode === "paper_experiment" ? catalog.paperExperimentStrategies : catalog.paperShadowStrategies;
                const paths = kind === "backtest" ? data.getAll("StrategyPaths") : [get("SelectedStrategyPath")];
                const selected = paths.map(path => options.find(item => item.path === path));
                if (!selected.length || selected.some(item => !item)) throw new Error("Select currently authorized strategies.");
                const horizons = [...new Set(selected.map(item => item.horizon))];
                if (horizons.length !== 1 || !horizons[0]) throw new Error('Use one strategy horizon per saved plan. Reload the catalog if horizon information is missing.');
                const identities = selected.map(item => ({ strategyId: item.artifactStrategyId, semanticVersion: item.semanticVersion, contentSha256: item.contentSha256 }));
                const decisionContextEvidence = kind === 'backtest' ? selectedEvidence() : null;
                const sources = [];
                const source = get("UniverseSource") || "wishlist";
                if (kind === 'backtest') {
                    const choice = form.elements.StockSource.selectedOptions[0];
                    if (!choice) throw new Error('Select a wishlist or saved Finviz stock source.');
                    sources.push({ sourceKind: choice.dataset.sourceKind, sourceKey: choice.dataset.sourceKey,
                        wishlistId: choice.dataset.wishlistId || null, query: choice.dataset.query || null });
                } else if (!preparationKey) {
                    if (source !== 'screener') {
                        if (!get('WishlistId')) throw new Error('Select a stock group.');
                        sources.push({ sourceKind: 'wishlist', sourceKey: get('WishlistId'), wishlistId: get('WishlistId') });
                    }
                    if (source !== 'wishlist') {
                        if (!get('ScreenerFilter').trim()) throw new Error('Select a Finviz screen or enter a query.');
                        sources.push({ sourceKind: 'finviz', sourceKey: get('ScreenerFilter'), query: get('ScreenerFilter') });
                    }
                }
                const membershipMode = get('PaperMembershipMode') || 'fixed_at_launch';
                const universe = preparationKey ? await api("preparation/accepted?key="+encodeURIComponent(preparationKey)) :
                    await api("universes/preview", { contractVersion: "1.0", horizon: horizons[0], sources,
                        ...(kind === 'paper' ? { paperMembershipMode: membershipMode } : {}) });
                if(preparationKey && Date.now()>=Date.parse(universe.expiresAtUtc))throw new Error("Preparation evidence expired. Return to preparation and collect a new review.");
                const base = {
                    contractVersion: "1.0", runName: get("RunName").trim() || kind + "_" + Date.now(),
                    universeSnapshotId: universe.universeSnapshotId, idempotencyKey: crypto.randomUUID(),
                    configurationKey: (get("SelectedConfigPath") || get("BaseConfigPath")).split(/[\\/]/).pop()
                };
                if (kind === "backtest") {
                    pending = { ...base, strategies: identities, testWindowStartUtc: testWindow.start, testWindowEndUtc: testWindow.end,
                        startingCapital: Number(get("StartingCapital")), accountRiskBudgetPct: Number(get("AccountRiskBudgetPct")),
                        maxPositionNotionalPct: Number(get("MaxPositionNotionalPct")), maxConcurrentPositions: Number(get("MaxConcurrentPositions")),
                        cachePolicy: get("CachePolicy") || "use_cache", retentionPolicy: "full",
                        executionReplayPolicyId: get("ExecutionReplayPolicyId") || null,
                        ...(dailyEligibility() ? { eligibility: dailyEligibility() } : {}),
                        ...(decisionContextEvidence ? { decisionContextEvidence } : {}) };
                } else {
                    pending = { ...base, strategy: identities[0], newsEnabled: data.getAll("NewsEnabled").includes("true"),
                        allowExtendedHoursTrading: data.getAll("AllowExtendedHoursTrading").includes("true"),
                        entryOrderType: get("EntryOrderType"), timeInForce: get("OrderExpiration"), executionPolicy: "strategy_validated", mode,
                        ...(!preparationKey ? { paperMembershipMode: universe.paperMembership.mode, universeContentSha256: universe.contentSha256 } : {}) };
                    showPaperStockReview(universe);
                    status.textContent = 'Review the stock membership, then start the strategy run.';
                    return;
                }
            }
            if (kind === 'backtest') {
                const manual = String(data.get('ManualCohorts') || '').trim();
                if (manual && String(data.get('GroupSize') || '').trim())
                    throw new Error('Use a group size or manual groups. Clear the other field before continuing.');
                const saving = button?.dataset.backtestAction === 'save';
                // Saving retains the reviewed hash; an explicit preview asks to review the current profile again.
                const payload = { run: saving ? pending : { ...pending, configurationSha256: null },
                    groupSize: manual ? null : Number(data.get('GroupSize')), cohorts: manual ? manualGroups(manual) : null,
                    groupOrder: manual ? null : String(data.get('GroupOrder') || 'alphabetical'),
                    groupSeed: !manual && data.get('GroupOrder') === 'random' ? optionalNumber('GroupSeed') : null,
                    excludedSymbols: String(data.get('ExcludedSymbols') || '').split(',').map(symbol => symbol.trim()).filter(Boolean) };
                const action = saving ? 'backtests/plans' : 'backtests/plans/preview';
                const plan = await api(action, payload);
                pending = plan.run;
                showPlan(plan);
                if (!saving) { previewed = true; if (savePlanButton) savePlanButton.disabled = false; }
                status.textContent = plan.planId ? 'Plan saved. No jobs have started.' : 'Review the groups, then save the plan before starting.';
                if (plan.planId) {
                    previewed = false;
                    savedPlan = plan;
                    await loadPlans();
                    planPicker.value = String(plan.planId);
                    startPlanButton.disabled = false;
                }
                return;
            }
            const result = await api("paper/runs", pending);
            status.textContent = result.runName + ": " + result.status;
            pending = undefined;
            clearPaperStockReview();
            const link = document.createElement("a");
            link.href = kind === "backtest" ? "/Backtests?jobId=" + result.jobId : "/PaperJob/" + result.jobId;
            link.textContent = " Open run";
            status.append(link);
        } catch (error) {
            status.textContent = error.message + " Retry keeps the same command; edit the form to start a new request.";
            form.inert = false;
            if (kind === 'backtest') showValidation([['', status.textContent]]);
        }
        finally { busy = false; form.inert = false; if (button) button.disabled = false; if (savePlanButton) savePlanButton.disabled = !previewed; }
    });
})();
