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
    form.addEventListener("input", () => { pending = undefined; });
    form.addEventListener("change", () => { pending = undefined; });
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
        busy = true; form.inert = true;
        const button = event.submitter;
        if (button) button.disabled = true;
        status.textContent = "Capturing universe and checking strategy authorization…";
        try {
            if (!pending) {
                const data = new FormData(form);
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
                    contractVersion: "1.0", runName: get("RunName") || kind + "_" + Date.now(),
                    universeSnapshotId: universe.universeSnapshotId, idempotencyKey: crypto.randomUUID(),
                    configurationKey: (get("SelectedConfigPath") || get("BaseConfigPath")).split(/[\\/]/).pop()
                };
                if (kind === "backtest") {
                    const end = new Date(); const start = new Date(end.getTime() - Number(get("LookbackDays") || 180) * 86400000);
                    pending = { ...base, strategies: identities, testWindowStartUtc: start.toISOString(), testWindowEndUtc: end.toISOString(),
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
        } catch (error) { status.textContent = error.message + " Retry keeps the same command; edit the form to start a new request."; }
        finally { busy = false; form.inert = false; if (button) button.disabled = false; }
    });
})();
