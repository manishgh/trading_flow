/* Shared run commands; the request survives transport retries until the operator edits the form. */
(() => {
    const kind = location.pathname.toLowerCase() === "/paper" ? "paper" : "backtest";
    const form = kind === "backtest" ? document.querySelector('form.lab-rail') : document.querySelector('form[data-run-workflow]');
    if (!form) return;
    let pending;
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
    function edited() { pending = undefined; clearValidation(); }
    form.addEventListener("input", edited);
    form.addEventListener("change", edited);
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
        if (!get('WishlistId')) errors.push(['WishlistId', 'Select a wishlist universe.']);
        if (errors.length) { showValidation(errors); return null; }
        clearValidation();
        return { start: start.toISOString(), end: end.toISOString() };
    }
    async function api(path, body) {
        const response = await window.tradingFlowFetch("/api/v1/" + path, body ? {
            method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body)
        } : { cache: "no-store" });
        if (!response.ok) {
            let message = "Request failed (" + response.status + ")";
            try { const error = await response.json(); message = error.message || message; } catch {}
            throw new Error(message);
        }
        return response.json();
    }
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
                const identities = selected.map(item => ({ strategyId: item.artifactStrategyId, semanticVersion: item.semanticVersion, contentSha256: item.contentSha256 }));
                const sources = [];
                const source = get("UniverseSource") || "wishlist";
                if ((kind === "backtest" || source !== "screener") && get("WishlistId")) sources.push({ sourceKind: "wishlist", sourceKey: get("WishlistId"), wishlistId: get("WishlistId") });
                if (kind === "paper" && source !== "wishlist" && get("ScreenerFilter")) sources.push({ sourceKind: "finviz", sourceKey: get("ScreenerFilter"), query: get("ScreenerFilter") });
                const universe = preparationKey ? await api("preparation/accepted?key="+encodeURIComponent(preparationKey)) : await api("universes/preview", { contractVersion: "1.0", horizon: "swing", sources });
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
                        cachePolicy: get("CachePolicy") || "use_cache", retentionPolicy: "full" };
                } else {
                    pending = { ...base, strategy: identities[0], newsEnabled: data.getAll("NewsEnabled").includes("true"),
                        allowExtendedHoursTrading: data.getAll("AllowExtendedHoursTrading").includes("true"),
                        entryOrderType: get("EntryOrderType"), timeInForce: get("OrderExpiration"), executionPolicy: "strategy_validated", mode };
                }
            }
            const result = await api(kind === "backtest" ? "backtests/runs" : "paper/runs", pending);
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
        finally { busy = false; form.inert = false; if (button) button.disabled = false; }
    });
})();
