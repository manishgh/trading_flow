// Run the existing server-reviewed entry/exit workflow within either ticket host.
(async () => {
    const form = document.querySelector("#OrderWorkflowForm"); if (!form) return;
    const status = document.querySelector("[data-order-status]");
    const review = document.querySelector("[data-order-review]");
    const summary = document.querySelector("[data-order-summary]");
    const technicalSummary = document.querySelector("[data-order-technical-summary]");
    const confirm = document.querySelector("[data-order-confirm]");
    const planButton = document.querySelector("[data-order-plan]");
    const riskButton = document.querySelector("[data-order-risk]");
    const query = new URLSearchParams(location.search);
    const exit = query.get("side") === "sell";
    const lifetime = new AbortController();
    window.addEventListener("pagehide", () => lifetime.abort(), {once:true});
    let confirmationStarted = false;
    let context, run, candidate, preview, command, draftRequest, plan, busy = false;
    let key = crypto.randomUUID();
    let completed = false;
    // The popup can replace only an editable or resolved ticket, never a reviewed or uncertain command.
    window.tradingFlowOrderState = () => busy || !completed && (confirmationStarted || !review.hidden) ? 'retained' : completed ? 'resolved' : 'editable';
    // Only visible, enabled controls participate in the retained ticket's keyboard order.
    function ticketFocusables() {
        return [...document.querySelectorAll('a[href],button,input,select,textarea,summary,[tabindex]')]
            .filter(node => node.tabIndex >= 0 && !node.matches(':disabled') && !node.closest('[inert]') &&
                node.getClientRects().length > 0 && getComputedStyle(node).visibility !== 'hidden');
    }
    // The same-origin modal host uses these boundaries instead of losing Tab focus to browser chrome.
    window.tradingFlowOrderFocus = boundary => {
        const controls = ticketFocusables();
        const target = boundary === 'last' ? controls.at(-1) : controls[0];
        target?.focus({preventScroll:true});
    };
    // Cross-document Escape and boundary Tab return to the host's always-visible close control.
    document.addEventListener('keydown', event => {
        if (parent === window) return;
        if (event.key === 'Escape') {
            event.preventDefault();
            parent.postMessage({type:'tradingflow:order-escape'}, location.origin);
        }
        if (event.key === 'Tab') {
            const controls = ticketFocusables();
            const boundary = event.shiftKey ? controls[0] : controls.at(-1);
            if (event.target === boundary) {
                event.preventDefault();
                parent.postMessage({type:'tradingflow:order-focus-close'}, location.origin);
            }
        }
    });
    // Reveal the containing options before the browser focuses an invalid required field.
    form.addEventListener('invalid', event => {
        const options = event.target.closest('details');
        if (options) options.open = true;
    }, true);
    // Keep the existing authenticated API contract and abort only on page disposal.
    async function api(path, body) {
        const response = await window.tradingFlowFetch("/api/v1/" + path, body ? { method:"POST", headers:{"Content-Type":"application/json"}, body:JSON.stringify(body), signal:lifetime.signal } : { cache:"no-store", signal:lifetime.signal });
        let value; try { value = await response.json(); } catch {}
        if (!response.ok) throw new Error(value?.message || "Request failed (" + response.status + ")");
        return value;
    }
    // Editing creates a new draft identity, while closing the popup never calls this.
    function invalidate() { draftRequest = undefined; key = crypto.randomUUID(); plan = undefined; riskButton.disabled = true; }
    // Explicit Edit releases a reviewed preview only before confirmation has started.
    function reset() { if (busy || confirmationStarted) return; preview = command = undefined; completed = false; invalidate(); review.hidden = true; form.hidden = false; }
    document.querySelector("[data-order-edit]").onclick = reset;
    // Symbol edits update the popup heading without changing the retained command lifecycle.
    form.addEventListener("input", () => {
        if (!busy) invalidate();
        if (parent !== window) parent.postMessage({type:'tradingflow:order-title',title:(exit ? 'Sell ' : 'Buy ') + form.elements.symbol.value.trim().toUpperCase()},location.origin);
    });
    form.addEventListener("change", () => { if (!busy) invalidate(); });
    form.elements.symbol.value = query.get("ticker") || document.querySelector("[data-selected-symbol]")?.dataset.selectedSymbol || "";
    form.elements.quantity.value = query.get("quantity") || "1";
    form.elements.limit.value = query.get("limitPrice") || "";
    if (exit) {
        form.elements.side.value = "Sell";
        document.querySelectorAll("[data-entry-only],[data-order-strategy],[data-order-source]").forEach(node => { node.hidden = true; });
        form.elements.strategy.required = false;
        form.elements.timeInForce.append(new Option("Good till cancelled","gtc"), new Option("Immediate or cancel","ioc"), new Option("Fill or kill","fok"));
        form.elements.orderType.replaceChildren(...["market","limit","stop","stop_limit"].map(value => new Option(value.replaceAll("_"," "),value)));
        form.elements.orderType.value = "limit";
        form.elements.orderType.onchange = () => {
            form.elements.limit.required = ["limit","stop_limit"].includes(form.elements.orderType.value);
            const trigger = ["stop","stop_limit"].includes(form.elements.orderType.value);
            document.querySelector("[data-exit-trigger]").hidden = !trigger;
            form.elements.trigger.required = trigger;
        };
    }
    // Candidate identity always wins over a manually selected strategy.
    const identity = () => candidate?.strategy || (form.elements.strategy.value ? JSON.parse(form.elements.strategy.value) : null);
    // Request advisory prices through the shared planner without submitting an order.
    planButton.onclick = async () => {
        if (busy) return; busy = true; form.inert = true;
        try {
            plan = await api("orders/plan", {contractVersion:"1.0",symbol:form.elements.symbol.value.trim().toUpperCase(),strategy:identity(),limitPrice:Number(form.elements.limit.value)||null});
            if (lifetime.signal.aborted) return;
            form.elements.limit.value = plan.limit ?? "";
            form.elements.stop.value = plan.stop ?? "";
            form.elements.target.value = plan.target ?? "";
            draftRequest = undefined; key = crypto.randomUUID();
            document.querySelector("[data-order-stop-note]").textContent = plan.stopNote;
            document.querySelector("[data-order-target-note]").textContent = plan.targetNote;
            riskButton.disabled = !(plan.riskBudget > 0 && plan.limit > plan.stop && plan.stop > 0);
            status.textContent = "Strategy prices loaded. Review before confirming.";
        } catch (error) { if (error.name !== "AbortError") status.textContent = error.message; }
        finally { busy = false; form.inert = false; }
    };
    // Size shares only from the last returned server plan and its risk budget.
    riskButton.onclick = () => {
        if (!plan || riskButton.disabled || busy) return;
        const quantity = Math.floor(plan.riskBudget / (plan.limit - plan.stop));
        if (quantity < 1) { status.textContent = "The account risk budget does not allow one share at this stop distance."; return; }
        form.elements.quantity.value = quantity; draftRequest = undefined; key = crypto.randomUUID();
    };
    // Review binds a draft once; uncertain preview retries keep the same request and key.
    form.addEventListener("submit", async event => {
        event.preventDefault(); if (busy) return; busy = true; completed = false; form.inert = true;
        try {
            if (!context?.accountId) throw new Error("A bound broker account is required.");
            const value = name => Number(form.elements[name].value);
            if (exit) {
                command = { contractVersion:"1.0", accountId:context.accountId, symbol:form.elements.symbol.value.trim().toUpperCase(),
                    quantity:value("quantity"), limitPrice:value("limit") || null, orderType:form.elements.orderType.value, timeInForce:form.elements.timeInForce.value, triggerPrice:value("trigger") || null, reason:"Operator reviewed close", allowExtendedHoursTrading:form.elements.extended.checked, idempotencyKey:key };
                technicalSummary.textContent = "Position availability is rechecked at confirmation.";
                summary.textContent = "Close " + command.quantity + " " + command.symbol + " · " + command.orderType + " · " + command.timeInForce + " · limit " + (command.limitPrice ?? "none") + " · trigger " + (command.triggerPrice ?? "none") + " · extended hours " + command.allowExtendedHoursTrading + ". Position availability is rechecked at confirmation.";
            } else {
                if (!candidate && !context.operatorOverrideAllowed) throw new Error("An authorized paper candidate or Administrator override is required.");
                if (!draftRequest) {
                    let universeId = run?.provenance.universeSnapshotId;
                    if (!universeId) {
                        const wishlistId = query.get("wishlistId") || form.elements.wishlist.value;
                        if (!wishlistId) throw new Error("Choose the discovery stock group containing this stock, or open an exact paper candidate.");
                        const universe = await api("universes/preview", {contractVersion:"1.0",horizon:"swing",sources:[{sourceKind:"wishlist",sourceKey:wishlistId,wishlistId}]});
                        universeId = universe.universeSnapshotId;
                    }
                    draftRequest = {contractVersion:"1.0",accountId:context.accountId,candidateId:candidate?.candidateId,candidateVersion:candidate?.version,
                        candidateRunId:run?.candidateRunId,universeSnapshotId:universeId,strategy:identity(),
                        symbol:form.elements.symbol.value.trim().toUpperCase(),side:"buy",quantity:value("quantity"),orderType:"limit",timeInForce:form.elements.timeInForce.value,
                        limitPrice:value("limit"),stopLossPrice:value("stop"),takeProfitPrice:value("target"),allowExtendedHoursTrading:form.elements.extended.checked,
                        executionPolicy:candidate ? "strategy_validated" : "operator_override",idempotencyKey:key};
                }
                preview = await api("orders/preview",draftRequest);
                summary.textContent = "Buy " + preview.quantity + " " + preview.symbol + " at " + preview.limitPrice +
                    "\nStop " + preview.stopLossPrice + " · target " + preview.takeProfitPrice +
                    "\nEstimated cost " + preview.estimatedNotional + " · maximum loss " + preview.maximumLoss +
                    "\nExpires " + preview.expiresAtUtc +
                    "\n" + preview.warnings.join("\n");
                technicalSummary.textContent = preview.orderType + " / " + preview.timeInForce + " · extended hours " + draftRequest.allowExtendedHoursTrading +
                    "\nStrategy " + preview.strategy.strategyId + " @ " + preview.strategy.semanticVersion + " · " + preview.strategy.contentSha256 +
                    "\nQuote " + preview.quoteTimestampUtc + "\nExpires " + preview.expiresAtUtc + "\nPolicy " + preview.executionPolicy;
            }
            form.hidden = true; review.hidden = false; confirm.disabled = false; confirm.textContent = "Confirm paper order"; status.textContent = "Review the bound command before confirming.";
        } catch (error) { if (error.name !== "AbortError") status.textContent = error.message; }
        finally { busy = false; form.inert = false; }
    });
    // Confirmation and all uncertain-response recovery use the original bound command.
    confirm.onclick = async () => {
        if (busy) return; busy = true; confirmationStarted = true; confirm.disabled = true;
        document.querySelector("[data-order-edit]").disabled = true;
        try {
            const result = await api(exit ? "positions/close" : "orders/confirm",
                exit ? command : {contractVersion:"1.0",previewToken:preview.previewToken,idempotencyKey:preview.idempotencyKey});
            status.textContent = result.state + " · " + (result.rejectionReason || result.brokerOrderId || result.clientOrderId || "");
            completed = result.state !== "confirmation_pending";
            if (result.state === "rejected") { confirmationStarted = false; document.querySelector("[data-order-edit]").disabled = false; }
            if (result.state === "confirmation_pending") { confirm.disabled = false; confirm.textContent = "Check same command"; }
        } catch (error) { if (error.name !== "AbortError") { status.textContent = error.message + " Retry checks the same command."; confirm.disabled = false; } }
        finally { busy = false; }
    };
    form.inert = true;
    busy = true;
    try {
        context = await api("orders/context");
        if (query.get("candidateId") && query.get("runId")) {
            run = await api("candidate-runs/" + query.get("runId"));
            candidate = run.candidates.find(item => item.candidateId === query.get("candidateId"));
            if (!candidate || !["paper","paper_shadow","paper_experiment"].includes(run.provenance.mode)) throw new Error("Open an authorized paper candidate; research candidates are for review only.");
            form.elements.symbol.value = candidate.symbol; form.elements.symbol.readOnly = true;
            form.elements.strategy.add(new Option(candidate.strategy.strategyId + " · " + candidate.strategy.semanticVersion, JSON.stringify(candidate.strategy)));
            form.elements.strategy.disabled = true;
            document.querySelector("[data-order-source]").hidden = true;
        } else if (!exit) {
            if (!context.operatorOverrideAllowed) throw new Error("Open an authorized paper candidate to review an entry. Manual override requires Administrator.");
            const catalog = await api("strategies?mode=backtest");
            catalog.strategies.forEach(item => form.elements.strategy.add(new Option(item.name + " · " + item.identity.semanticVersion,JSON.stringify(item.identity))));
            if (query.get("wishlistId")) document.querySelector("[data-order-source]").hidden = true;
            else {
                const response = await window.tradingFlowFetch("/api/desk/sources", {signal:lifetime.signal});
                if (!response.ok) throw new Error("Discovery sources unavailable; open an exact candidate or a stock group ticket.");
                for (const source of await response.json()) if (source.key.startsWith("wishlist:")) form.elements.wishlist.add(new Option(source.name,source.key.slice(9)));
            }
        }
        status.textContent = exit ? "Review an exit from an existing position." : candidate ? "Paper strategy candidate " + candidate.readiness : "Administrator operator override; normal risk checks still apply.";
        form.inert = false;
    } catch (error) { form.hidden = true; if (error.name !== "AbortError") status.textContent = error.message; }
    finally { busy = false; }
})();
