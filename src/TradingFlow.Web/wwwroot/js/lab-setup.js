/* Strategy Lab: helpers that fill the backtest form (profile values, strategy and window presets, held-back periods). */
window.createLabSetup = ({ form }) => {
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
        syncProfileWindowPreset(option);
    }
    // Shows the "Profile window" preset only when the selected profile has a window, labelled with its dates.
    function syncProfileWindowPreset(option) {
        const button = form.querySelector('[data-window-preset="profile"]');
        if (!button) return;
        const start = option?.dataset.windowStart || '', end = option?.dataset.windowEnd || '';
        button.hidden = !(start && end);
        button.dataset.windowStart = start;
        button.dataset.windowEnd = end;
        button.textContent = start && end ? 'Profile window ' + start.slice(0, 10) + ' – ' + end.slice(0, 10) : 'Profile window';
        button.title = 'Test window of the selected backtest profile: its own dates, or its rolling number of New York calendar days up to today.';
    }
    // Lists the registered held-back test periods of the selected strategies and warns when the chosen dates overlap one.
    // A warning only: it does not stop the run.
    function syncHeldBackPeriods() {
        const block = form.querySelector('[data-held-back]');
        const warning = block?.querySelector('[data-held-back-warning]');
        if (!warning) return;
        const selected = new Set([...form.querySelectorAll('input[name="StrategyPaths"]:checked')].map(box => box.dataset.strategyId));
        const { start, end } = chosenTestWindow(new FormData(form));
        const overlapping = [];
        let shown = 0;
        for (const period of block.querySelectorAll('[data-held-back-period]')) {
            period.hidden = !selected.has(period.dataset.strategyId);
            if (period.hidden) continue;
            shown++;
            if (start && end && start < new Date(period.dataset.end) && end > new Date(period.dataset.start)) overlapping.push(period.dataset.strategyId);
        }
        block.querySelector('[data-held-back-none]').hidden = shown > 0 || selected.size === 0;
        warning.hidden = overlapping.length === 0;
        const message = overlapping.length
            ? 'The chosen dates overlap the held-back test period of ' + [...new Set(overlapping)].join(', ') + '. Results seen there can no longer serve as an untouched test.'
            : '';
        // Rewriting an unchanged alert would make screen readers repeat it on every keystroke.
        if (warning.textContent !== message) warning.textContent = message;
    }
    // Wires 1-click Long / Short / Clear strategy selection buttons and notifies the form change handler.
    function wireStrategyDirectionHelpers() {
        for (const button of form.querySelectorAll('[data-strategy-select]')) {
            button.addEventListener('click', () => {
                const mode = button.dataset.strategySelect;
                let lastChanged = null;
                for (const option of form.querySelectorAll('.lab-strategy-option')) {
                    const checkbox = option.querySelector('input[type="checkbox"][name="StrategyPaths"]');
                    if (!checkbox) continue;
                    const direction = String(option.dataset.direction || '').toLowerCase();
                    const nextChecked = mode === 'clear' ? false : direction === mode;
                    if (checkbox.checked !== nextChecked) {
                        checkbox.checked = nextChecked;
                        lastChanged = checkbox;
                    }
                }
                (lastChanged || form).dispatchEvent(new Event('change', { bubbles: true }));
            });
        }
    }

    // Wires the quick UTC test-window buttons (the selected profile's own window and Clear dates) and notifies validation/form state.
    function wireWindowPresetHelpers() {
        const startInput = form.elements.namedItem('TestWindowStartUtc');
        const endInput = form.elements.namedItem('TestWindowEndUtc');
        if (!(startInput instanceof HTMLInputElement) || !(endInput instanceof HTMLInputElement)) return;
        for (const button of form.querySelectorAll('[data-window-preset]')) {
            button.addEventListener('click', () => {
                const preset = button.dataset.windowPreset;
                startInput.value = preset === 'clear' ? '' : (button.dataset.windowStart || '');
                endInput.value = preset === 'clear' ? '' : (button.dataset.windowEnd || '');
                startInput.dispatchEvent(new Event('input', { bubbles: true }));
                endInput.dispatchEvent(new Event('change', { bubbles: true }));
            });
        }
    }

    // Pre-fills 1000 stocks per group, more than any published research screen holds, so a research screen runs as one
    // group unless the operator types a size or manual groups. A "whole screen" marker, not a tuning number.
    function applyResearchScreenGroupDefault() {
        const sourceSelect = form.querySelector('#StockSource');
        const groupSize = form.elements.namedItem('GroupSize');
        const manualCohorts = form.elements.namedItem('ManualCohorts');
        if (!(sourceSelect instanceof HTMLSelectElement) || !(groupSize instanceof HTMLInputElement)) return;
        const selected = sourceSelect.selectedOptions[0];
        const isResearchScreen = selected?.dataset.sourceKind === 'research_screen' || String(selected?.value || '').startsWith('research_screen:');
        const hasGroupSize = String(groupSize.value || '').trim().length > 0;
        const hasManual = manualCohorts instanceof HTMLTextAreaElement && String(manualCohorts.value || '').trim().length > 0;
        if (isResearchScreen && !hasGroupSize && !hasManual) {
            groupSize.value = '1000';
        }
    }

    // Reads a browser UTC date-time text exactly; null when it is not a real date and time.
    function utcInput(value) {
        const parts = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?$/.exec(value);
        if (!parts) return null;
        const [year, month, day, hour, minute, second] = parts.slice(1).map(part => Number(part || 0));
        const result = new Date(Date.UTC(year, month - 1, day, hour, minute, second));
        return result.getUTCFullYear() === year && result.getUTCMonth() === month - 1 && result.getUTCDate() === day && result.getUTCHours() === hour && result.getUTCMinutes() === minute && result.getUTCSeconds() === second ? result : null;
    }
    // Reads the chosen test window without showing anything: both dates, or the lookback ending now, with any entry errors.
    function chosenTestWindow(data) {
        const get = name => String(data.get(name) || '').trim();
        const errors = [], from = get('TestWindowStartUtc'), to = get('TestWindowEndUtc');
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
        return { start, end, errors };
    }

    // Wires the helpers to the backtest form; called once, only on the Backtests page.
    function start() {
        form.querySelector('#SelectedConfigPath')?.addEventListener('change', applyBacktestProfile);
        wireStrategyDirectionHelpers();
        wireWindowPresetHelpers();
        syncProfileWindowPreset(form.querySelector('#SelectedConfigPath')?.selectedOptions[0]);
        form.addEventListener('input', syncHeldBackPeriods);
        form.addEventListener('change', syncHeldBackPeriods);
        syncHeldBackPeriods();
        form.querySelector('#StockSource')?.addEventListener('change', applyResearchScreenGroupDefault);
        applyResearchScreenGroupDefault();
    }

    return { start, chosenTestWindow };
};
