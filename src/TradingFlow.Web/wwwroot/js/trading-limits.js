/* One account-wide mode profile; saving changes no strategy authorization and starts no run. */
(() => {
    const panel = document.querySelector('[data-trading-limits]');
    if (!panel) return;
    const form = panel.querySelector('form');
    const status = panel.querySelector('[data-trading-limits-status]');
    const rule = panel.querySelector('[data-trading-limits-rule]');
    let profile;
    // Loads server values and preserves a revision for simultaneous-editor protection.
    async function read() {
        const response = await window.tradingFlowFetch('/api/v1/trading-limits/' + encodeURIComponent(panel.dataset.mode));
        profile = await result(response);
        form.elements.maxNewStocksPerDay.value = profile.maxNewStocksPerDay;
        form.elements.maxOpenPositions.value = profile.maxOpenPositions;
        rule.textContent = profile.countingRule;
        status.textContent = 'Loaded account limits. Saving does not start a run.';
    }
    // Shows the concrete API error rather than silently selecting default values.
    async function result(response) {
        const body = await response.json();
        if (!response.ok) throw new Error(body.message || 'Unable to read or save trading limits.');
        return body;
    }
    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (!profile || !form.reportValidity()) return;
        const button = form.querySelector('button'); button.disabled = true;
        try {
            profile = await result(await window.tradingFlowFetch('/api/v1/trading-limits', {
                method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
                    contractVersion: '1.0', mode: profile.mode, expectedRevision: profile.revision,
                    maxNewStocksPerDay: Number(form.elements.maxNewStocksPerDay.value),
                    maxOpenPositions: Number(form.elements.maxOpenPositions.value)
                })
            }));
            rule.textContent = profile.countingRule;
            status.textContent = 'Account limits saved. No run has started.';
        } catch (error) { status.textContent = error.message; }
        finally { button.disabled = false; }
    });
    read().catch(error => { status.textContent = error.message; });
})();
