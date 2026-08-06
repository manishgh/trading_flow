/* Shared chrome behaviour: the header clocks, the alerts toggle, and the one
   toast every screen announces through.

   Nothing here fetches or decides. It renders wall-clock time, remembers a
   device preference, and shows a message another module hands it. */
(() => {
    /* ---------------------------------------------------------------------
       Clocks
       Seconds are shown because the screens they sit above are time-critical:
       a quote age, a countdown to a print, and a 30-second ticket token are all
       read against this. Ticking every second keeps them honest; the previous
       30-second cadence could be half a minute stale at a glance.
       --------------------------------------------------------------------- */
    const clocks = Array.from(document.querySelectorAll("[data-clock]"));

    function zoneFor(node) {
        const zone = node.dataset.clock;
        return zone === "local" ? Intl.DateTimeFormat().resolvedOptions().timeZone : zone;
    }

    function formatTime(now, timeZone) {
        try {
            return new Intl.DateTimeFormat("en-GB", {
                timeZone,
                hour: "2-digit",
                minute: "2-digit",
                second: "2-digit",
                hour12: false
            }).format(now);
        } catch {
            // An unreadable zone renders as unknown rather than falling back to
            // the browser's zone, which would silently show the wrong market.
            return "--:--:--";
        }
    }

    function updateClocks() {
        const now = new Date();
        const iso = now.toISOString();
        for (const node of clocks) {
            const time = node.querySelector("time");
            if (!time) continue;
            time.textContent = formatTime(now, zoneFor(node));
            time.dateTime = iso;
        }
    }

    if (clocks.length) {
        updateClocks();
        window.setInterval(updateClocks, 1000);
    }

    /* ---------------------------------------------------------------------
       Toast
       One region for the whole app, announced politely. Screens call
       TradingFlow.toast(); they do not build their own.
       --------------------------------------------------------------------- */
    const toastNode = document.getElementById("SignalToast");
    let toastTimer = null;

    function toast(title, body) {
        if (!toastNode) return;
        toastNode.replaceChildren();
        if (title) {
            const heading = document.createElement("strong");
            heading.className = "toast-title";
            heading.textContent = title;
            toastNode.append(heading);
        }
        if (body) {
            const text = document.createElement("span");
            text.className = "toast-body";
            text.textContent = body;
            toastNode.append(text);
        }
        toastNode.hidden = false;
        window.clearTimeout(toastTimer);
        toastTimer = window.setTimeout(() => { toastNode.hidden = true; }, 5000);
    }

    /* ---------------------------------------------------------------------
       Alerts
       A device preference, not an account setting: whether this browser may
       raise a desktop notification and whether the toast speaks at all. Muting
       is per device because the operator who mutes is at this screen.
       --------------------------------------------------------------------- */
    const ALERTS_KEY = "tradingflow.alerts";
    const alertButton = document.getElementById("AlertToggle");

    function alertsEnabled() {
        try {
            return window.localStorage.getItem(ALERTS_KEY) !== "off";
        } catch {
            return true;
        }
    }

    function renderAlertState() {
        if (!alertButton) return;
        const on = alertsEnabled();
        alertButton.setAttribute("aria-pressed", on ? "true" : "false");
        const label = alertButton.querySelector("[data-alert-label]");
        if (label) label.textContent = on ? "Alerts on" : "Alerts off";
    }

    alertButton?.addEventListener("click", async () => {
        const next = alertsEnabled() ? "off" : "on";
        try {
            window.localStorage.setItem(ALERTS_KEY, next);
        } catch {
            /* Private-mode or storage-disabled: apply for this document only. */
        }
        renderAlertState();
        // Permission is requested only on the way on, and only from this click,
        // because a permission prompt raised without a gesture is dismissed by
        // the browser and then cannot be asked for again.
        if (next === "on" && "Notification" in window && Notification.permission === "default") {
            try {
                await Notification.requestPermission();
            } catch {
                /* Refused or unsupported: the toast still carries the message. */
            }
        }
        toast("Alerts", next === "on" ? "Signal alerts are on for this device." : "Signal alerts are muted on this device.");
    });

    renderAlertState();

    window.TradingFlow = Object.assign(window.TradingFlow || {}, {
        toast,
        alertsEnabled,
        /* Raises a desktop notification only when alerts are on, permission was
           granted, and the tab is not the one being looked at. */
        notify(title, body) {
            if (!alertsEnabled()) return;
            if (!("Notification" in window) || Notification.permission !== "granted") return;
            if (!document.hidden) return;
            new Notification(title, { body });
        }
    });
})();
