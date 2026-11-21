// Connect the Desk card to the same observation used by the workspace status strip.
(() => {
    const root = document.querySelector('[data-preparation-summary]');
    if (!root) return;
    const find = key => root.querySelector('[data-preparation-' + key + ']');
    const title = root.querySelector('#preparation-summary-title');
    const action = find('action'), actionUrl = action.href;
    const steps = Array.from(find('steps').children);
    const twelveHoursMs = 12 * 60 * 60 * 1000;

    // Identifies an active or failed preparation step that must never be hidden behind compact mode.
    function hasActiveOrFailedStep(stepText) {
        const text = String(stepText || '').toLowerCase();
        return text.includes('in progress') || text.includes('failed') || text.includes('cancelled') || text.includes('unavailable');
    }

    // Decides whether the preparation banner should collapse to a calm 1-line bar without hiding unconfigured workflows, active/failed steps, or worker problems.
    function describeCompactMode(summary, worker, nowMs) {
        if (!summary) return { compact: false, heading: 'Today\u2019s preparation' };
        if (!summary.workflowId) return { compact: false, heading: 'Preparation not configured' };
        if (hasActiveOrFailedStep(summary.eveningState) || hasActiveOrFailedStep(summary.morningState)) {
            return { compact: false, heading: 'Today\u2019s preparation' };
        }
        if (summary.scheduleSavedOn && worker?.state !== 'available') {
            return { compact: false, heading: 'Today\u2019s preparation' };
        }
        const expiryMs = summary.expiresAtUtc ? Date.parse(summary.expiresAtUtc) : NaN;
        const expiredOver12h = Number.isFinite(expiryMs) && (nowMs - expiryMs) > twelveHoursMs;
        if (!summary.scheduleSavedOn) return { compact: true, heading: 'Saved preparation (paused)' };
        if (expiredOver12h) return { compact: true, heading: 'Previous preparation' };
        return { compact: false, heading: 'Today\u2019s preparation' };
    }

    // Link only to the exact saved revision or accepted review returned for this operator.
    function render(status) {
        if (!status) return;
        find('worker').textContent = status.preparationWorker.detail;
        const summary = status.preparation;
        action.href = actionUrl;
        action.textContent = 'Open preparation';
        if (!summary) {
            delete root.dataset.compact;
            if (title) title.textContent = 'Today\u2019s preparation';
            find('target').textContent = status.preparationReadError || 'Saved preparation has not been read.';
            for (const [index, text] of ['Evening collection', 'Morning collection', 'Review', 'Open on Desk'].entries())
                steps[index].textContent = text;
            return;
        }
        const mode = describeCompactMode(summary, status.preparationWorker, Date.now());
        if (mode.compact) root.dataset.compact = 'true';
        else delete root.dataset.compact;
        if (title) title.textContent = mode.heading;
        const expiry = summary.expiresAtUtc ? new Date(summary.expiresAtUtc) : null;
        const expiryLabel = expiry
            ? ' · evidence ' + (expiry <= new Date() ? 'expired ' : 'valid until ') +
                expiry.toLocaleString(undefined, { timeZone: 'America/New_York', dateStyle: 'medium', timeStyle: 'short' }) + ' NY'
            : '';
        const target = summary.targetDate ? 'Target session ' + summary.targetDate + ' (New York)' : 'Target session not yet observed';
        const schedule = summary.workflowId ? (summary.scheduleSavedOn ? ' · saved schedule on' : ' · saved schedule paused') : '';
        find('target').textContent = target + expiryLabel + schedule;
        steps[0].textContent = summary.eveningState;
        steps[1].textContent = summary.morningState;
        steps[2].textContent = summary.reviewKey ? 'Review saved' : summary.morningReviewable ? 'Review morning list' : 'Review';
        steps[3].textContent = summary.reviewKey ? 'Open reviewed list on Desk' : 'Open on Desk';
        if (summary.reviewKey) {
            action.href = '/Desk?preparationKey=' + encodeURIComponent(summary.reviewKey);
            action.textContent = 'Open reviewed list';
        } else if (summary.workflowId) {
            const query = new URLSearchParams({ workflow: summary.workflowId });
            if (summary.revisionId && summary.occurrenceId) {
                query.set('occurrence', summary.occurrenceId);
                query.set('revision', summary.revisionId);
            }
            action.href = actionUrl + '?' + query;
            action.textContent = summary.morningReviewable ? 'Review morning list'
                : summary.revisionId ? 'Review saved evidence' : 'Open saved schedule';
        } else {
            action.textContent = 'Set up preparation';
        }
    }
    document.addEventListener('workspace-status-changed', event => render(event.detail));
    render(window.tradingFlowWorkspaceStatus);
})();
