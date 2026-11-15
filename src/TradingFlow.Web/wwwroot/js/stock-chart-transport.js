(() => {
    'use strict';

    const maxPayloadBytes = 256 * 1024;
    const maxParts = 40;
    const maxRetries = 5;
    const encoder = new TextEncoder();

    // Compare the normalized server identity with the exact active chart selection.
    function sameKey(actual, selected) {
        const context = selected.context === 'auto' ? (selected.interval === '1d' ? 'swing' : 'intraday') : selected.context;
        return actual?.symbol === selected.symbol && actual.interval === selected.interval &&
            actual.sessionView === selected.sessionView && actual.context === context;
    }

    // Validate the version and bounded page shape before any plot is replaced.
    function readSnapshot(text, key) {
        if (encoder.encode(text).length > maxPayloadBytes) throw new Error('Chart response exceeded its size limit.');
        const snapshot = JSON.parse(text);
        const { bars, zones, levels, markers } = snapshot;
        if (snapshot.contractVersion !== 1 || !sameKey(snapshot.seriesKey, key) || !snapshot.evidence ||
            !Array.isArray(bars) || bars.length > 500 || !Array.isArray(zones) ||
            !Array.isArray(levels) || !Array.isArray(markers) || markers.length > 100) {
            throw new Error('Chart data did not match the selected view.');
        }
        return snapshot;
    }

    // Report actionable history failures without exposing authentication or provider internals.
    function historyError(status) {
        if (status === 401) return 'Sign in again to open charts.';
        if (status === 403) return 'This stock is not in the recorded workspace. Open it from an authorized stock list.';
        if (status === 429) return 'Too many chart requests. Try again shortly.';
        if (status === 400) return 'The chart or history link expired. Retry the chart.';
        return 'Chart history is temporarily unavailable. Try again shortly.';
    }

    // Fetch a bounded baseline or older page with the active selection's abort signal.
    async function history(path, key, cursor, signal) {
        const query = new URLSearchParams({
            symbol: key.symbol, interval: key.interval, sessionView: key.sessionView, chartContext: key.context
        });
        if (cursor) query.set('cursor', cursor);
        const response = await fetch(path + '?' + query, { cache: 'no-store', credentials: 'same-origin', signal });
        if (!response.ok) {
            const error = new Error(historyError(response.status));
            error.status = response.status;
            error.permanent = response.status === 401 || response.status === 403;
            throw error;
        }
        return readSnapshot(await response.text(), key);
    }

    // Reassemble a bounded series epoch atomically, ignoring duplicates and retired epochs.
    function assembler(key) {
        let epoch;
        let sequence = 0;
        let pending = null;
        const retired = new Set();

        // Accept only complete snapshots whose inner and outer identities agree.
        return function accept(frame) {
            if (!sameKey(frame.key, key) || typeof frame.epoch !== 'string' || retired.has(frame.epoch) ||
                !Number.isSafeInteger(frame.sequence) || frame.sequence <= 0) return null;
            if (frame.kind === 'heartbeat') return null;
            if (frame.kind !== 'snapshot' || !Number.isInteger(frame.part) || !Number.isInteger(frame.parts) ||
                frame.parts < 1 || frame.parts > maxParts || frame.part < 0 || frame.part >= frame.parts ||
                typeof frame.payload !== 'string' || frame.payload.length > 8000) {
                throw new Error('Invalid chart update.');
            }
            if (epoch !== frame.epoch) {
                if (frame.part !== 0) return null;
                if (epoch) retired.add(epoch);
                if (retired.size > maxParts) throw new Error('Too many chart resets.');
                epoch = frame.epoch;
                sequence = 0;
                pending = null;
            }
            if (frame.sequence <= sequence || (pending && frame.sequence < pending.sequence)) return null;
            if (!pending || frame.sequence > pending.sequence) {
                if (frame.part !== 0) return null;
                pending = { sequence: frame.sequence, parts: frame.parts, chunks: [] };
            }
            if (frame.part < pending.chunks.length) return null;
            if (frame.parts !== pending.parts || frame.part !== pending.chunks.length) {
                throw new Error('Incomplete chart update.');
            }
            pending.chunks.push(frame.payload);
            if (pending.chunks.length !== pending.parts) return null;
            const snapshot = readSnapshot(pending.chunks.join(''), key);
            if (snapshot.epoch !== epoch || snapshot.sequence !== pending.sequence) throw new Error('Invalid chart baseline.');
            sequence = pending.sequence;
            pending = null;
            return snapshot;
        };
    }

    // Isolate one connection and retry loop for the selected key, cancelling all work on teardown.
    function watch(paths, key, abort, callbacks, afterClose) {
        let connection;
        let subscription;
        let retry;
        let attempt = 0;
        let stopped = false;
        let received = false;
        let triedHistory = false;
        let stopping = Promise.resolve();

        // Serialize disposal with the next connection so rapid changes release the shared connection budget.
        function closeSocket() {
            const active = connection;
            connection = null;
            subscription?.dispose();
            subscription = null;
            if (active) stopping = active.stop().catch(() => {});
            return stopping;
        }

        // Preserve the last completed view during disconnect, and seed a read-only fallback once if needed.
        async function fallback() {
            if (received || triedHistory || stopped) return;
            triedHistory = true;
            try {
                const snapshot = await history(paths.history, key, null, abort.signal);
                if (!received && !stopped) callbacks.snapshot(snapshot, false);
            } catch (error) {
                if (received || stopped) return;
                callbacks.status(error.message, false);
                if (error.permanent) stop();
            }
        }

        // Bound retries with jitter and start every reconnect from a fresh baseline.
        function reconnect(active) {
            if (stopped || connection !== active) return;
            closeSocket();
            if (attempt >= maxRetries) {
                callbacks.status('Chart stream unavailable. Retry to reconnect.', false);
                void fallback();
                return;
            }
            callbacks.status('Reconnecting chart stream…', true);
            void fallback();
            const delay = Math.min(8000, 500 * 2 ** attempt++) + Math.random() * 250;
            retry = setTimeout(() => { void start(); }, delay);
        }

        // Use cookie authentication on a same-origin WebSocket and subscribe only after connecting.
        async function start() {
            await afterClose;
            await stopping;
            if (stopped) return;
            if (!window.signalR) {
                callbacks.status('Chart stream unavailable. Retry to reconnect.', false);
                void fallback();
                return;
            }
            const active = new window.signalR.HubConnectionBuilder()
                .withUrl(paths.hub, { transport: window.signalR.HttpTransportType.WebSockets, skipNegotiation: true })
                .configureLogging(window.signalR.LogLevel.None)
                .build();
            connection = active;
            active.serverTimeoutInMilliseconds = 45_000;
            const accept = assembler(key);
            // Ended streams and closed sockets share one guarded reconnect path.
            active.onclose(() => reconnect(active));
            try {
                await active.start();
                if (stopped || connection !== active) return;
                subscription = active.stream('Watch', key).subscribe({
                    // Keep a partial or superseded frame out of the displayed series.
                    next(frame) {
                        if (stopped || connection !== active) return;
                        try {
                            const snapshot = accept(frame);
                            if (!snapshot) return;
                            received = true;
                            attempt = 0;
                            callbacks.snapshot(snapshot, true);
                        } catch {
                            reconnect(active);
                        }
                    },
                    // Re-authenticate and read a new baseline after the watch expires.
                    complete() { reconnect(active); },
                    // Recover from server errors while keeping an existing historical view visible.
                    error() { reconnect(active); }
                });
            } catch {
                reconnect(active);
            }
        }

        // Cancel older-page requests, retry timers and the live chart connection together.
        function stop() {
            stopped = true;
            abort.abort();
            clearTimeout(retry);
            return Promise.all([afterClose, closeSocket()]);
        }

        void start();
        return stop;
    }

    window.TradingFlowStockChartTransport = { watch, history };
})();
