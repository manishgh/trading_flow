// Retain one server-reviewed workflow while the native dialog opens and closes.
(() => {
    const dialog = document.getElementById('OrderPopup');
    if (!dialog) return;
    const frame = document.getElementById('OrderPopupFrame');
    const close = document.getElementById('OrderPopupClose');
    const resume = document.getElementById('OrderPopupResume');
    const title = document.getElementById('OrderPopupTitle');
    const notice = document.getElementById('OrderPopupNotice');
    let identity, opener;

    // Ignore changing quotes and quantity when reopening the same exact trade context.
    function ticketIdentity(url) {
        const keys = ['ticker', 'side', 'candidateId', 'runId', 'wishlistId', 'env'];
        return JSON.stringify(keys.map(key => url.searchParams.get(key) || (key === 'env' ? 'paper' : key === 'side' ? 'buy' : '')));
    }

    // Only editable tickets may be replaced; uncertain responses retain their original command.
    function canSwitchTicket() {
        try { return ['editable','resolved'].includes(frame.contentWindow.tradingFlowOrderState?.()); }
        catch { return false; }
    }

    // Opening only loads context; server preview and confirmation remain explicit user actions.
    function open(href, label, trigger = document.activeElement) {
        const url = new URL(href, location.origin);
        if (url.origin !== location.origin || url.pathname !== '/OrderTicket') return;
        frame.hidden = false;
        const nextIdentity = ticketIdentity(url);
        const blocked = identity && identity !== nextIdentity && !canSwitchTicket();
        notice.hidden = !blocked;
        notice.textContent = blocked ? 'Your current ticket has a review or unresolved request. Edit it or check the same command before starting another ticket.' : '';
        const resolved = frame.contentWindow.tradingFlowOrderState?.() === 'resolved';
        if (!blocked && (identity !== nextIdentity || resolved && trigger !== resume)) {
            identity = nextIdentity;
            url.searchParams.set('embedded', '1');
            frame.src = url.pathname + url.search;
            title.textContent = label || (url.searchParams.get('side') === 'sell' ? 'Sell ' : 'Buy ') + (url.searchParams.get('ticker') || 'strategy candidate');
        }
        if (!dialog.open) {
            opener = trigger;
            dialog.showModal();
            document.documentElement.classList.add('order-popup-open');
        }
        resume.hidden = false;
        close.focus({preventScroll:true});
    }

    // Explain a refused Desk action without navigating or replacing any retained reviewed/uncertain ticket.
    function unavailable(message, label, trigger = document.activeElement) {
        notice.hidden = false;
        notice.textContent = label + ': ' + message + (identity ? ' Your existing ticket is retained below.' : '');
        frame.hidden = !identity;
        if (!identity) title.textContent = label + ' unavailable';
        if (!dialog.open) {
            opener = trigger;
            dialog.showModal();
            document.documentElement.classList.add('order-popup-open');
        }
        resume.hidden = !identity;
        close.focus({preventScroll:true});
    }

    // Native Escape and the close button hide the ticket without aborting or resetting it.
    function closeTicket() { if (dialog.open) dialog.close(); }

    // Restore the exact invoking control when it still exists after a live row refresh.
    function restoreFocus() {
        document.documentElement.classList.remove('order-popup-open');
        if (opener?.isConnected) opener.focus({preventScroll:true});
        else resume.focus({preventScroll:true});
    }

    // Bridge keyboard and title events only from the retained same-origin ticket.
    function embeddedKey(event) {
        if (event.origin !== location.origin || event.source !== frame.contentWindow) return;
        if (event.data?.type === 'tradingflow:order-escape') closeTicket();
        if (dialog.open && event.data?.type === 'tradingflow:order-focus-close') close.focus({preventScroll:true});
        if (event.data?.type === 'tradingflow:order-title') title.textContent = event.data.title;
    }

    // Explicitly traverse the iframe boundary in both directions; keep Close focused during loading.
    close.addEventListener('keydown', event => {
        if (event.key !== 'Tab') return;
        event.preventDefault();
        if (!frame.hidden) frame.contentWindow.tradingFlowOrderFocus?.(event.shiftKey ? 'last' : 'first');
    });
    close.addEventListener('click', closeTicket);
    dialog.addEventListener('close', restoreFocus);
    window.addEventListener('message', embeddedKey);
    // Resume never navigates or reloads the retained ticket.
    resume.addEventListener('click', () => open(frame.src, title.textContent, resume));
    window.tradingFlowOrderPopup = {open, unavailable};

    // Trading links keep journal filters in place while opening their ticket in the popup.
    document.addEventListener('click', event => {
        const link = event.target.closest('a.order-symbol, a[data-position-review], a[data-order-new-ticket]');
        if (!link?.getAttribute('href') || event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        const url = new URL(link.href);
        if (url.origin !== location.origin || url.pathname !== '/Orders') return;
        event.preventDefault();
        url.pathname = '/OrderTicket';
        open(url.href, null, link);
    });

    // The compact symbol box opens locally and leaves the journal's URL and sorting intact.
    document.querySelector('.orders-new-order')?.addEventListener('submit', event => {
        event.preventDefault();
        const form = event.currentTarget;
        if (!form.reportValidity()) return;
        const url = new URL('/OrderTicket', location.origin);
        url.search = new URLSearchParams(new FormData(form)).toString();
        url.searchParams.set('ticker', form.elements.ticker.value.trim().toUpperCase());
        open(url.href, null, form.querySelector('button[type=submit]'));
    });

    // Existing deep links still open an intentional ticket, after server symbol validation.
    const start = document.querySelector('[data-order-popup-start]');
    if (start?.dataset.orderPopupStart) {
        const url = new URL('/OrderTicket' + location.search, location.origin);
        url.searchParams.set('ticker', start.dataset.orderPopupStart);
        open(url.href, null, document.getElementById('OrdersNewTicker'));
    }
})();
