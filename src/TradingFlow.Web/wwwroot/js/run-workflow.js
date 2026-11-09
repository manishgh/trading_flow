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
    let previewed = false;
    if (savePlanButton) savePlanButton.disabled = true;
    const evidencePicker = form.querySelector('[data-evidence-picker]');
    const evidenceSummary = form.querySelector('[data-evidence-summary]');
    const evidenceAvailability = form.querySelector('[data-evidence-availability]');
    let evidenceChoices = [];
    const lifetime = new AbortController();
    // Discards backend-bound choices on departure, including documents retained by browser history.
    function leavePage() {
        lifetime.abort(); pending = undefined; savedPlan = undefined; savedPlans = []; evidenceChoices = []; previewed = false;
        startKeys.clear();
        if (evidencePicker) evidencePicker.value = '';
        if (startPlanButton) startPlanButton.disabled = true;
        if (savePlanButton) savePlanButton.disabled = true;
        planPreview?.replaceChildren(); evidenceSummary?.replaceChildren();
    }
    window.addEventListener('pagehide', leavePage);
    // Browser-history restoration reloads the authenticated server catalog instead of reviving old-account choices.
    window.addEventListener('pageshow', event => { if (event.persisted) location.reload(); });
    const preparationKey=kind==='paper'?new URLSearchParams(location.search).get('preparationKey'):null;
    if(preparationKey){
        api('preparation/accepted?key='+encodeURIComponent(preparationKey)).then(review=>{
            const label=document.createElement('p');label.textContent='Accepted preparation snapshot '+review.universeSnapshotId+' · '+review.symbols.join(', ')+' · expires '+new Date(review.expiresAtUtc).toLocaleString()+'. This bound membership replaces the source selectors below.';form.prepend(label);
            for(const key of ['WishlistId','ScreenerFilter','UniverseSource'])form.querySelectorAll('[name="'+key+'"]').forEach(x=>x.disabled=true);
        }).catch(error=>{status.textContent=error.message;});
    }
    let busy = false;
    const status = document.createElement("p");
    status.setAttribute("role", "status");
    status.dataset.runWorkflowStatus = "";
    form.append(status);
    const validation = form.querySelector('[data-run-validation]');
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
        if (planPicker) planPicker.value = '';
        planPreview?.replaceChildren();
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
    }
    if (kind === 'backtest') form.querySelector('#SelectedConfigPath')?.addEventListener('change', applyBacktestProfile);
    function utcInput(value) {
        const parts = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?$/.exec(value);
        if (!parts) return null;
        const [year, month, day, hour, minute, second] = parts.slice(1).map(part => Number(part || 0));
        const result = new Date(Date.UTC(year, month - 1, day, hour, minute, second));
        return result.getUTCFullYear() === year && result.getUTCMonth() === month - 1 && result.getUTCDate() === day && result.getUTCHours() === hour && result.getUTCMinutes() === minute && result.getUTCSeconds() === second ? result : null;
    }
    function backtestWindow(data) {
        const get = name => String(data.get(name) || '').trim();
        const errors = [];
        if (get('RunName') && !/^[A-Za-z0-9_-]{1,100}$/.test(get('RunName')))
            errors.push(['RunName', 'Run name must use at most 100 letters, digits, hyphens or underscores.']);
        const from = get('TestWindowStartUtc'), to = get('TestWindowEndUtc');
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
        planPreview.append(description, exclusions, selection, list);
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
            atrPeriod: optionalNumber('EligibilityAtrPeriod'), minAtr: optionalNumber('EligibilityMinAtr') };
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
        if (savedPlan) showPlan(savedPlan);
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
                } else if (source !== "screener" && get("WishlistId")) sources.push({ sourceKind: "wishlist", sourceKey: get("WishlistId"), wishlistId: get("WishlistId") });
                if (kind === "paper" && source !== "wishlist" && get("ScreenerFilter")) sources.push({ sourceKind: "finviz", sourceKey: get("ScreenerFilter"), query: get("ScreenerFilter") });
                const universe = preparationKey ? await api("preparation/accepted?key="+encodeURIComponent(preparationKey)) : await api("universes/preview", { contractVersion: "1.0", horizon: horizons[0], sources });
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
                        entryOrderType: get("EntryOrderType"), timeInForce: get("OrderExpiration"), executionPolicy: "strategy_validated", mode };
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
