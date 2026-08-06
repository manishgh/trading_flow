/* Running-phase progress for the Strategy Lab.

   Polls the run snapshot and patches the header, the bar and the activity list
   in place. It reloads exactly once, on the first transition to a terminal
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

    function setText(node, value) {
        if (node && value !== undefined && value !== null && node.textContent !== String(value)) {
            node.textContent = String(value);
        }
    }

    async function poll() {
        try {
            const response = await fetch(`/Backtests?handler=Snapshot&id=${encodeURIComponent(jobId)}`, {
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

            // The results phase is server-rendered, so the one reload is on the
            // first terminal status and never again.
            const terminal = ["completed", "failed", "cancelled"].includes(String(snapshot.status));
            if (terminal && !reloaded) {
                reloaded = true;
                window.clearInterval(timer);
                window.location.reload();
            }
        } catch {
            /* A dropped poll leaves the last known state on screen rather than
               blanking a run that is still going. */
        }
    }

    const timer = window.setInterval(poll, 1500);
    poll();
})();
