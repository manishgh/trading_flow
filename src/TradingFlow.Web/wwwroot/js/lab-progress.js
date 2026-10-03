/* Running-phase progress for the Strategy Lab.

   Polls the run snapshot and patches the header, the bar and the activity list
   in place. It replaces the result panel after the first transition to a terminal
   status, because the results phase is server-rendered and there is nothing
   useful to patch into it. */
(() => {
    const script = document.currentScript ?? document.querySelector("script[data-job-id]");
    const jobId = script?.dataset.jobId;
    const root = document.querySelector("[data-lab-job]");
    if (!jobId || !root) return;

    const statusNode = root.querySelector("[data-lab-status]");
    const stageNode = root.querySelector("[data-lab-stage]");
    const elapsedNode = root.querySelector("[data-lab-elapsed]");
    const completedNode = root.querySelector("[data-lab-completed]");
    const totalNode = root.querySelector("[data-lab-total]");
    const percentNode = root.querySelector("[data-lab-percent]");
    const progressNode = root.querySelector("[data-lab-progress]");
    const progressBar = progressNode?.parentElement;
    const eventsNode = root.querySelector("[data-lab-events]");

    let reloaded = false;
    let busy = false;

    function setText(node, value) {
        if (node && value !== undefined && value !== null && node.textContent !== String(value)) {
            node.textContent = String(value);
        }
    }

    async function poll() {
        if (busy || reloaded) return;
        busy = true;
        try {
            const response = await window.tradingFlowFetch(`/Backtests?handler=Snapshot&id=${encodeURIComponent(jobId)}`, {
                headers: { Accept: "application/json" },
                cache: "no-store"
            });
            if (!response.ok) return;
            const snapshot = await response.json();

            setText(statusNode, snapshot.status);
            setText(stageNode, String(snapshot.currentStage ?? "").replaceAll("_", " "));
            setText(elapsedNode, snapshot.elapsed);
            setText(completedNode, snapshot.completedTickerCount);
            setText(totalNode, snapshot.totalTickerCount);
            setText(percentNode, snapshot.progressPercent);
            if (progressNode) progressNode.style.width = `${snapshot.progressPercent}%`;
            progressBar?.setAttribute("aria-valuenow", String(Math.round(snapshot.progressPercent)));

            if (eventsNode && Array.isArray(snapshot.events)) {
                const latest = [...snapshot.events].reverse().slice(0, 12);
                eventsNode.replaceChildren(...latest.map(entry => {
                    const item = document.createElement("li");
                    item.textContent = entry;
                    return item;
                }));
            }

            // Fetch the server-rendered results without replacing the form or page.
            const terminal = ["completed", "failed", "cancelled"].includes(String(snapshot.status));
            if (terminal && !reloaded) {


                const rendered = await window.tradingFlowFetch(location.href, { cache: "no-store" });
                if (!rendered.ok) throw new Error("Result view unavailable");
                const documentView = new DOMParser().parseFromString(await rendered.text(), "text/html");
                const current = document.querySelector(".lab-main");
                const replacement = documentView.querySelector(".lab-main");
                if (!current || !replacement) throw new Error("Result panel unavailable");
                current.replaceChildren(...replacement.childNodes);
                reloaded = true;
                window.clearInterval(timer);
            }
        } catch {
            /* A dropped poll leaves the last known state on screen rather than
               blanking a run that is still going. */
        } finally { busy = false; }
    }

    const timer = window.setInterval(poll, 1500);
    poll();
})();
