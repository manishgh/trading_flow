(async () => {
    const form = document.querySelector("#OrderWorkflowForm"); if (!form) return;
    const status = document.querySelector("[data-order-status]");
    const review = document.querySelector("[data-order-review]");
    const summary = document.querySelector("[data-order-summary]");
    const confirm = document.querySelector("[data-order-confirm]");
    const query = new URLSearchParams(location.search);
    const exit = query.get("side") === "sell";
    let context, run, candidate, preview, command, draftRequest, busy = false;
    let key = crypto.randomUUID();
    async function api(path, body) {
        const response = await window.tradingFlowFetch("/api/v1/" + path, body ? { method:"POST", headers:{"Content-Type":"application/json"}, body:JSON.stringify(body) } : { cache:"no-store" });
        let value; try { value = await response.json(); } catch {}
        if (!response.ok) throw new Error(value?.message || "Request failed (" + response.status + ")");
        return value;
    }
    function reset() { preview = command = draftRequest = undefined; key = crypto.randomUUID(); review.hidden = true; form.hidden = false; }
    document.querySelector("[data-order-edit]").onclick = reset;
    form.addEventListener("input", () => { if (!busy) { draftRequest = undefined; key = crypto.randomUUID(); } });
    form.elements.symbol.value = query.get("ticker") || "";
    form.elements.limit.value = query.get("limitPrice") || "";
    if (exit) {
        document.querySelectorAll("[data-entry-only],[data-order-strategy]").forEach(node => { node.hidden = true; });
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
    form.addEventListener("submit", async event => {
        event.preventDefault(); if (busy) return; busy = true; form.inert = true;
        try {
            if (!context?.accountId) throw new Error("A bound broker account is required.");
            const value = name => Number(form.elements[name].value);
            if (exit) {
                command = { contractVersion:"1.0", accountId:context.accountId, symbol:form.elements.symbol.value.trim().toUpperCase(),
                    quantity:value("quantity"), limitPrice:value("limit") || null, orderType:form.elements.orderType.value, timeInForce:form.elements.timeInForce.value, triggerPrice:value("trigger") || null, reason:"Operator reviewed close", allowExtendedHoursTrading:form.elements.extended.checked, idempotencyKey:key };
                summary.textContent = "Close " + command.quantity + " " + command.symbol + " · " + command.orderType + " · " + command.timeInForce + " · limit " + (command.limitPrice ?? "none") + " · trigger " + (command.triggerPrice ?? "none") + ". Position availability is rechecked at confirmation.";
            } else {
                if (!candidate && !context.operatorOverrideAllowed) throw new Error("An authorized candidate or Administrator override is required.");
                if (!draftRequest) {
                let universeId = run?.provenance.universeSnapshotId;
                if (!universeId) {
                    const wishlistId = query.get("wishlistId");
                    if (!wishlistId) throw new Error("Open an operator ticket from a wishlist, or select an exact candidate.");
                    const universe = await api("universes/preview", {contractVersion:"1.0",horizon:"swing",sources:[{sourceKind:"wishlist",sourceKey:wishlistId,wishlistId}]});
                    universeId = universe.universeSnapshotId;
                }
                const request = {contractVersion:"1.0",accountId:context.accountId,candidateId:candidate?.candidateId,candidateVersion:candidate?.version,
                    candidateRunId:run?.candidateRunId,universeSnapshotId:universeId,strategy:candidate?.strategy || JSON.parse(form.elements.strategy.value),
                    symbol:form.elements.symbol.value.trim().toUpperCase(),side:"buy",quantity:value("quantity"),orderType:"limit",timeInForce:form.elements.timeInForce.value,
                    limitPrice:value("limit"),stopLossPrice:value("stop"),takeProfitPrice:value("target"),allowExtendedHoursTrading:form.elements.extended.checked,
                    executionPolicy:candidate ? "strategy_validated" : "operator_override",idempotencyKey:key};
                draftRequest = request;
                }
                preview = await api("orders/preview",draftRequest);
                summary.textContent = preview.quantity + " " + preview.symbol + " at " + preview.limitPrice + "\nNotional " + preview.estimatedNotional +
                    " · maximum loss " + preview.maximumLoss + "\nQuote " + preview.quoteTimestampUtc + "\nExpires " + preview.expiresAtUtc +
                    "\nPolicy " + preview.executionPolicy + "\n" + preview.warnings.join("\n");
            }
            form.hidden = true; review.hidden = false; confirm.disabled = false; status.textContent = "Review the bound command before confirming.";
        } catch (error) { status.textContent = error.message; }
        finally { busy = false; form.inert = false; }
    });
    confirm.onclick = async () => {
        if (busy) return; busy = true; confirm.disabled = true;
        try {
            const result = await api(exit ? "positions/close" : "orders/confirm",
                exit ? command : {contractVersion:"1.0",previewToken:preview.previewToken,idempotencyKey:preview.idempotencyKey});
            status.textContent = result.state + " · " + (result.rejectionReason || result.brokerOrderId || result.clientOrderId || "");
            if (result.state === "confirmation_pending") { confirm.disabled = false; confirm.textContent = "Check same command"; }
        } catch (error) { status.textContent = error.message + " Retry checks the same command."; confirm.disabled = false; }
        finally { busy = false; form.inert = false; }
    };
    try {
        context = await api("orders/context");
        if (query.get("candidateId") && query.get("runId")) {
            run = await api("candidate-runs/" + query.get("runId"));
            candidate = run.candidates.find(item => item.candidateId === query.get("candidateId"));
            if (!candidate) throw new Error("Candidate does not belong to this run.");
            form.elements.symbol.value = candidate.symbol; form.elements.symbol.readOnly = true;
            form.elements.strategy.add(new Option(candidate.strategy.strategyId + " · " + candidate.strategy.semanticVersion, JSON.stringify(candidate.strategy)));
            form.elements.strategy.disabled = true;
        } else if (!exit) {
            const catalog = await api("strategies?mode=backtest");
            catalog.strategies.forEach(item => form.elements.strategy.add(new Option(item.name + " · " + item.identity.semanticVersion,JSON.stringify(item.identity))));
        }
        status.textContent = exit ? "Close an existing position." : candidate ? "Strategy candidate " + candidate.readiness : "Administrator operator override; normal risk checks still apply.";
    } catch (error) { status.textContent = error.message; }
})();
