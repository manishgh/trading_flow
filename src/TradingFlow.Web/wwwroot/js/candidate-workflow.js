(() => {
    const root = document.querySelector("[data-candidate-workflow]");
    if (!root) return;
    const create = root.querySelector("[data-candidate-create]");
    const open = root.querySelector("[data-candidate-open]");
    const status = root.querySelector("[data-candidate-status]");
    const rows = root.querySelector("[data-candidate-rows]");
    let runId, runMode, cursor = 0, stream, poll, generation = 0, pending, busy = false;
    const nodes = new Map();
    async function api(path, body) {
        const response = await window.tradingFlowFetch("/api/v1/" + path, body ? { method: "POST",
            headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) } : { cache: "no-store" });
        if (!response.ok) { let error; try { error = await response.json(); } catch {} throw Object.assign(new Error(error?.message || "Request failed (" + response.status + ")"), { status: response.status }); }
        return response.json();
    }
    function row(candidate, profile) {
        let card = nodes.get(candidate.candidateId);
        if (!card) {
            card = document.createElement("article"); card.className = "candidate-row"; card.dataset.candidateId = candidate.candidateId;
            card.innerHTML = "<strong></strong><p></p><small></small><p><a data-audit>Audit and attached news</a><a data-order>Review order</a></p>";
            nodes.set(candidate.candidateId, card); rows.append(card);
        }
        const assign = (selector, text) => { const node = card.querySelector(selector); if (node.textContent !== text) node.textContent = text; };
        assign("strong", candidate.symbol + " · " + candidate.readiness.replaceAll("_", " "));
        assign("p", candidate.rejections.map(item => item.message).join("; ") || "No rejection recorded");
        assign("small", candidate.strategy.strategyId + " · " + candidate.strategy.semanticVersion + " · " + candidate.strategy.contentSha256.slice(0,12));
        card.querySelector("[data-audit]").href = "/CandidateAudit?candidateId=" + candidate.candidateId + "&runId=" + runId;
        const order = card.querySelector("[data-order]");

        // Exact readiness label varies only on the server; state Triggered remains the entry prerequisite.
        order.hidden = !["paper", "paper_shadow", "paper_experiment"].includes(profile) || !["triggered", "Triggered"].includes(candidate.state);
        order.href = "/OrderTicket?candidateId=" + candidate.candidateId + "&runId=" + runId;
    }
    function stop() { stream?.close(); stream = undefined; clearInterval(poll); }
    async function snapshot(expected) {
        let value;
        try { value = await api("candidate-runs/" + runId); }
        catch (error) {
            if (error.status !== 404) throw error;
            if (generation === expected) status.textContent = "Candidate run is not available yet. Queued paper runs appear when execution starts.";
            return;
        }
        if (generation !== expected || value.latestEventSequence < cursor) return;
        const wanted = new Set(value.candidates.map(item => item.candidateId));
        for (const [id, node] of nodes) if (!wanted.has(id)) { node.remove(); nodes.delete(id); }
        runMode = value.provenance.mode;
        value.candidates.forEach(candidate => row(candidate, runMode)); cursor = value.latestEventSequence;
        status.textContent = value.candidates.length ? value.status + " · " + value.candidates.length + " candidates" : "This run has no candidates.";
    }
    function connect(expected) {
        if (generation !== expected) return;
        stream?.close();
        stream = new EventSource("/api/v1/candidate-runs/" + runId + "/events?after=" + cursor);
        stream.addEventListener("candidate.changed", event => {
            if (generation !== expected) return;
            const value = JSON.parse(event.data);
            if (value.eventSequence <= cursor) return;
            row(value.payload.candidate, runMode); cursor = value.eventSequence;
        });
        stream.addEventListener("resync", async () => { stream?.close(); try { await snapshot(expected); connect(expected); } catch (error) { status.textContent = error.message; } });
        stream.onerror = () => { if (generation === expected) status.textContent = "Connection interrupted; refreshing from durable state."; };
    }
    async function select(id) {
        stop(); generation++; const expected = generation; cursor = 0; runMode = undefined; nodes.clear(); rows.replaceChildren();
        runId = id; open.elements.run.value = id; status.textContent = "Loading candidate state…";
        try {
            await snapshot(expected); if (generation !== expected) return;
            localStorage.setItem("candidateRun", id);
            connect(expected);
            // Polling uses the same projection and also recovers expired cursors after a network outage.
            poll = setInterval(async () => { try { await snapshot(expected); if (generation === expected && stream?.readyState === EventSource.CLOSED) connect(expected); } catch (error) { status.textContent = error.message; } }, 10000);
        } catch (error) { status.textContent = error.message; }
    }
    async function catalog() {
        const value = await api("strategies?mode=" + create.elements.mode.value);
        create.elements.strategy.replaceChildren(...value.strategies.map(item => {
            const option = new Option(item.name + " · " + item.identity.semanticVersion, JSON.stringify(item.identity)); return option;
        }));
        status.textContent = value.strategies.length ? "Select a source and capture immutable membership, or open a run." : "No exact strategy is authorized for this mode.";
    }
    create.addEventListener("change", () => { pending = undefined; });
    create.elements.mode.addEventListener("change", () => catalog().catch(error => { status.textContent = error.message; }));
    create.addEventListener("submit", async event => {
        event.preventDefault(); if (busy) return; busy = true;
        try {
            if (!pending) {
                const id = create.elements.wishlist.value;
                const universe = await api("universes/preview", { contractVersion: "1.0", horizon: "swing", sources: [{ sourceKind: "wishlist", sourceKey: id, wishlistId: id }] });
                pending = { contractVersion: "1.0", idempotencyKey: crypto.randomUUID(), mode: create.elements.mode.value,
                    universeSnapshotId: universe.universeSnapshotId, strategies: [JSON.parse(create.elements.strategy.value)], asOfUtc: universe.decisionCutoffUtc };
            }
            const result = await api("candidate-runs", pending); pending = undefined; await select(result.candidateRunId);
        } catch (error) { status.textContent = error.message; }
        finally { busy = false; }
    });
    open.addEventListener("submit", event => { event.preventDefault(); select(open.elements.run.value.trim()); });
    window.addEventListener("pagehide", () => { generation++; stop(); }, { once: true });
    (async () => {
        try {
            const lists = await api("wishlists");
            create.elements.wishlist.replaceChildren(...lists.map(item => new Option(item.name, item.id)));
            await catalog();
            const query = new URLSearchParams(location.search);
            const requestedWishlist = query.get("wishlistId");
            if (requestedWishlist && lists.some(item => item.id === requestedWishlist)) {
                create.elements.wishlist.value = requestedWishlist;
                root.querySelector("[data-candidate-controls]").open = true;
            }
            const id = query.get("runId") || (!requestedWishlist && localStorage.getItem("candidateRun"));
            if (id) await select(id);
        } catch (error) { status.textContent = error.message; }
    })();
})();
