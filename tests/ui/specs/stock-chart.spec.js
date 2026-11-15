import { expect, test } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { signIn } from './sign-in.js';

const separator = '\x1e';
const tradingDay = Date.parse('2026-11-12T14:30:00Z');
const minute = 60_000;
const currentRevision = 'fixture-current-' + 'a'.repeat(96);
const earlierRevision = 'fixture-earlier-' + 'b'.repeat(96);
const earlierObserved = '2026-11-12T13:58:00Z';
const crosshairColor = '#d41bf7';

// Use sub-minute availability and distinct UTC exchange sessions in fixture evidence.
const iso = value => new Date(value).toISOString();

// Produce actual OHLCV candles with varying ranges, closes and volume for renderer checks.
function fixtureBars(key, page = 0) {
    const daily = key.interval === '1d';
    const minutes = daily ? 390 : Number.parseInt(key.interval, 10);
    const count = daily ? 28 : 38;
    const dailyStart = Date.parse('2026-11-12T00:00:00Z');
    const start = (daily ? dailyStart : tradingDay) - (page ? 86400_000 : key.sessionView === 'extended' ? 40 * minute : 0);
    return Array.from({ length: count }, (_, index) => {
        const time = daily ? start - (count - index - 1) * 86400_000 : start + index * minutes * minute;
        const closeAt = time + minutes * minute;
        const bias = key.symbol === 'MSFT' ? 120 : 0;
        const open = Number((190 + bias + index * .16 + Math.sin(index * .55) * 2.2).toFixed(2));
        const close = Number((open + (index % 3 === 0 ? -.87 : .61)).toFixed(2));
        return {
            startUtc: iso(time), endUtc: iso(closeAt), sessionDate: iso(time).slice(0, 10),
            segment: time < tradingDay && !daily && !page ? 'premarket' : 'regular',
            open, high: index === 25 ? 199.35 + bias : open + 1.2,
            low: index === count - 1 ? open - 1.1125 : open - 1.1, close,
            volume: 5000 + (index % 8) * 1400 + index * 277, sourceKind: 'canonical_stream',
            knownAtUtc: iso(closeAt + 950), availableAtUtc: iso(closeAt + 950), completionState: 'completed'
        };
    });
}

// Represent warm-up, explicit sample gaps and causal daily availability using the real compact wire shape.
function fixtureIndicators(key, bars, page) {
    if (!key.indicators) return [];
    return key.indicators.split(',').map(id => {
        const name = id.split(':')[0];
        const sourceInterval = id.endsWith(':1d') ? '1d' : ['rvol', 'vwap'].includes(name) ? '1m' : key.interval;
        const columns = { bb20: ['lower', 'middle', 'upper'], macd: ['macd', 'signal', 'histogram'],
            adx14: ['adx', 'pdi', 'mdi'] }[name] || ['value'];
        const unavailable = name === 'rvol' || !bars.length;
        const samples = unavailable ? [] : columns.map((_, column) => bars.map((bar, index) => {
            if (index < 4 || index === 9) return null;
            const value = /^(sma|ema|bb|vwap)/.test(name) ? bar.close - .48 : name === 'obv' ? index * 10 : 45 + index / 10;
            return value + column;
        }));
        const sameDay = !unavailable && sourceInterval === '1d' && key.interval !== '1d';
        const available = sameDay ? Date.parse(bars[4].startUtc) - 1000 : null;
        return { id, sourceInterval, sessionView: key.sessionView, adjustmentPolicy: 'all', formulaVersion: 'fixture-v1',
            parameters: 'Fixture fixed period', cutoffUtc: bars.at(-1)?.endUtc, anchorUtc: '2026-11-10T21:00:00Z',
            sourceRevision: page ? earlierRevision : currentRevision, reconstructed: sameDay, availableAtUtc: available && iso(available),
            status: unavailable ? 'unavailable' : 'partial', reason: name === 'rvol' ? 'Insufficient baseline' :
                unavailable ? 'Completed candles unavailable' : 'Warm-up or gaps exclude some observations.',
            columns, samples, segmentStarts: unavailable ? [] : [4, 10],
            segmentAnchorsUtc: unavailable ? [] : ['2026-11-10T21:00:00Z', '2026-11-11T21:00:00Z'],
            availabilityRuns: sameDay ? [[4, bars.length - 4, available]] : [], seedObservations: 14, warmupObservations: 114 };
    });
}

// Base all levels, zone geometry and annotation timestamps on the bounded fixture bars.
function fixtureSnapshot(key, { page = 0, empty = false } = {}) {
    const bars = empty ? [] : fixtureBars(key, page);
    const lastCompletedUtc = bars.at(-1)?.endUtc;
    const time = bars[10]?.endUtc;
    const bias = key.symbol === 'MSFT' ? 120 : 0;
    const source = key.context !== 'intraday' ? '1d' : '15m';
    const reconstructed = key.context === 'year';
    return {
        contractVersion: 2, seriesKey: key, asOfUtc: page ? earlierObserved : reconstructed ? '2026-11-13T14:00:00Z' : '2026-11-12T17:41:00Z',
        status: empty ? 'unavailable' : key.interval === '1d' || page ? 'history_only' : 'streaming',
        reason: empty ? 'No verified fixture candles' : null,
        evidence: {
            provider: 'alpaca', feed: 'sip', adjustmentPolicy: 'all', coverage: empty ? 'unavailable' : 'complete',
            coverageThroughUtc: lastCompletedUtc, lastCompletedUtc, lastSourceObservedUtc: bars.at(-1)?.knownAtUtc,
            reconstructed, notes: empty ? [] : ['Completed fixture candles.'], basisBreaks: []
        },
        bars,
        zones: empty || page ? [] : [
            {
                id: 'support', type: 'support', lower: 190.3 + bias, upper: 191.1 + bias, pivotCount: 3,
                sourceTimeframe: source, sourceDates: [bars[0].sessionDate], firstPivotUtc: bars[6].startUtc,
                latestConfirmationUtc: time, effectiveAtUtc: reconstructed ? '2026-11-13T14:00:00Z' : time,
                isPriceInBand: false, currentOnly: true
            },
            {
                id: 'resistance', type: 'resistance', lower: 198.9 + bias, upper: 199.8 + bias, pivotCount: 1,
                sourceTimeframe: source, sourceDates: [bars[25].sessionDate], firstPivotUtc: bars[25].startUtc,
                latestConfirmationUtc: bars[27].endUtc,
                effectiveAtUtc: reconstructed ? '2026-11-13T14:00:00Z' : bars[27].availableAtUtc,
                isPriceInBand: false, currentOnly: true
            }
        ],
        levels: empty || page ? [] : reconstructed ? [{
            key: 'sma50', label: 'Daily SMA50', value: 194.5 + bias, effectiveAtUtc: '2026-11-13T14:00:00Z',
            sourceTimeframe: source, points: bars.slice(5).filter(bar => bar !== bars.at(-2)).map((bar, index) => ({
                startUtc: bar.startUtc, value: 192.3 + bias + index * .1, availableAtUtc: '2026-11-13T14:00:00Z'
            }))
        }] : [
            {
                key: 'vwap', label: 'Session VWAP', value: 193.5 + bias, effectiveAtUtc: bars.at(-1).knownAtUtc,
                distancePct: 1.15, sourceTimeframe: source,
                points: bars.filter(bar => bar.segment === 'regular').map((bar, index) => ({
                    startUtc: bar.startUtc, value: 189.7 + bias + index * .1, availableAtUtc: bar.knownAtUtc
                }))
            }
        ],
        markers: empty || page ? [] : [
            { id: 'inside', kind: 'news', eventAtUtc: bars[17].startUtc, knownAtUtc: bars[17].endUtc,
                title: 'Company update', source: 'Fixture News', detail: 'Company publishes a release.', url: 'https://example.org/article' },
            { id: 'outside', kind: 'earnings', eventAtUtc: iso(Date.parse(lastCompletedUtc) + minute),
                title: 'After close', source: 'Fixture Calendar', isEstimate: true }
        ],
        historyCursor: empty || page ? null : 'previous-fixture-page',
        indicators: fixtureIndicators(key, bars, page)
    };
}

// Translate only Auto to the canonical server context; retain every other requested field.
function canonical(key) {
    return { ...key, context: key.context === 'auto' ? (key.interval === '1d' ? 'swing' : 'intraday') : key.context };
}

// Use SignalR's real JSON WebSocket framing with an independent epoch per Watch invocation.
async function chartWire(page, historyStatus = 200) {
    const watches = [];
    const history = [];
    await page.routeWebSocket(/\/api\/v1\/charts\/hub(?:\?.*)?$/, ws => {
        let watched;
        ws.onClose(() => { if (watched) watched.closed = true; });
        ws.onMessage(message => {
            for (const wire of message.toString().split(separator).filter(Boolean)) {
                const input = JSON.parse(wire);
                if (input.protocol === 'json') ws.send('{}' + separator);
                if (input.type !== 4 || input.target !== 'Watch') continue;
                watched = { ws, invocationId: input.invocationId, key: canonical(input.arguments[0]),
                    epoch: 'fixture-' + watches.length, sequence: 0, closed: false };
                watches.push(watched);
            }
        });
    });
    await page.route('**/api/v1/charts/history?**', route => {
        const query = new URL(route.request().url()).searchParams;
        const key = canonical({ symbol: query.get('symbol'), interval: query.get('interval'),
            sessionView: query.get('sessionView'), context: query.get('chartContext'), indicators: query.get('indicators') || '' });
        const older = query.has('cursor');
        history.push({ key, older });
        const snapshot = fixtureSnapshot(key, { page: older ? 1 : 0 });
        if (typeof historyStatus === 'function') return route.fulfill(historyStatus(snapshot, query));
        return route.fulfill({ status: historyStatus, json: snapshot });
    });
    return { watches, history };
}

// Create ordered multipart fixture frames that remain below the wire's per-part bound.
function frames(watch, supplied = fixtureSnapshot(watch.key)) {
    const snapshot = { ...supplied, sequence: ++watch.sequence, epoch: watch.epoch };
    const parts = JSON.stringify(snapshot).match(/[\s\S]{1,7000}/g);
    return parts.map((payload, part) => ({
        type: 2, invocationId: watch.invocationId,
        item: { key: snapshot.seriesKey, kind: 'snapshot', epoch: snapshot.epoch,
            sequence: snapshot.sequence, part, parts: parts.length, payload }
    }));
}

// Deliver an atomic baseline or individually staged chunks through the active fixture socket.
function send(watch, messages = frames(watch)) {
    for (const message of messages) watch.ws.send(JSON.stringify(message) + separator);
}

// Inspect actual canvas pixels so an empty canvas or fake renderer cannot pass the plot test.
function coloredPanes(plot) {
    return plot.locator('canvas').evaluateAll(canvases => {
        if (!canvases.length) return 0;
        const css = getComputedStyle(canvases[0].closest('[data-stock-chart]'));
        const tokens = ['--stock-chart-up', '--stock-chart-down'].map(token => {
            const hex = css.getPropertyValue(token).trim();
            return [1, 3, 5].map(index => Number.parseInt(hex.slice(index, index + 2), 16));
        });
        return canvases.filter(canvas => {
            if (!canvas.width || !canvas.height) return false;
            const data = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
            const matches = tokens.map(() => 0);
            for (let offset = 0; offset < data.length; offset += 16) {
                if (data[offset + 3] !== 255) continue;
                for (const [index, components] of tokens.entries()) {
                    if (components.every((value, channel) => data[offset + channel] === value)) matches[index]++;
                }
                if (matches.every(count => count >= 8)) return true;
            }
            return false;
        }).length;
    });
}

// Give the real renderer a distinct test-only crosshair color for canvas assertions.
function colorCrosshair(page) {
    return page.addInitScript(color => {
        let api;
        Object.defineProperty(window, 'LightweightCharts', {
            get: () => api,
            set(library) {
                const line = { color, width: 2, style: library.LineStyle.Solid };
                api = { ...library, createChart: (element, options) => library.createChart(element,
                    { ...options, crosshair: { vertLine: line, horzLine: line } }) };
            }
        });
    }, crosshairColor);
}

// Read the actual canvas overlay rather than treating a method call as proof of cleared pixels.
function crosshairVisible(plot) {
    const channels = crosshairColor.slice(1).match(/../g).map(hex => Number.parseInt(hex, 16));
    return plot.locator('canvas').evaluateAll((canvases, [red, green, blue]) => canvases.some(canvas => {
        if (!canvas.width || !canvas.height) return false;
        const pixels = canvas.getContext('2d').getImageData(0, Math.floor(canvas.height / 2), canvas.width, 1).data;
        for (let offset = 0; offset < pixels.length; offset += 4)
            if (pixels[offset] === red && pixels[offset + 1] === green && pixels[offset + 2] === blue && pixels[offset + 3] === 255) return true;
        return false;
    }), channels);
}

// Hash the native price pane's left and right regions separately to catch misplaced or missing overlays.
function overlayPixels(plot) {
    return plot.locator('canvas').evaluateAll(canvases => {
        const canvas = canvases.sort((a, b) => b.width * b.height - a.width * a.height)[0];
        const { width, height } = canvas;
        const data = canvas.getContext('2d').getImageData(0, 0, width, height).data;
        let history = 0;
        let rightEdge = 0;
        for (let y = 0; y < height; y++) {
            for (let x = 0; x < width; x++) {
                const index = (y * width + x) * 4;
                const color = data[index] + data[index + 1] * 256 + data[index + 2] * 65_536;
                if (x < width * .8) history = (history * 31 + color) >>> 0;
                else rightEdge = (rightEdge * 31 + color) >>> 0;
            }
        }
        return { history, rightEdge };
    });
}

// Capture opt-in visual review artifacts outside the test runner's disposable path.
async function capture(chart, name) {
    if (process.env.TRADINGFLOW_CHART_SCREENSHOTS !== 'true') return;
    const directory = new URL('../../../.tmp/chart-verification-20261115/screenshots/', import.meta.url);
    await mkdir(directory, { recursive: true });
    await chart.screenshot({ path: fileURLToPath(new URL(name + '.png', directory)), animations: 'disabled' });
}

// Seed only the stock list; no provider or broker data accompanies the Desk fixture.
async function deskSource(page) {
    const source = { key: 'all', name: 'All followed stocks', group: '', description: 'Fixture stocks' };
    const value = {
        selection: { source, totalCount: 2, page: 0, pageCount: 1 },
        rows: ['AAPL', 'MSFT'].map(symbol => ({
            membership: { symbol, name: symbol, wishlists: [] }, quote: null, holding: null, holdingsKnown: true, news: [],
            observation: { state: 'unavailable', detail: 'No strategy fixture' }
        })),
        account: { available: true, detail: 'Fixture account', readAtUtc: new Date().toISOString() },
        news: [], newsCoverage: 'No fixture news', session: 'Regular'
    };
    await page.route('**/api/desk/sources', route => route.fulfill({ json: [source] }));
    await page.route('**/api/desk/stream?**', route => route.fulfill({
        contentType: 'text/event-stream', body: 'event: snapshot\ndata: ' + JSON.stringify(value) + '\n\n'
    }));
}

test.beforeEach(async ({ page }) => { await signIn(page); });

test('plots completed candles and volume with keyboard, event evidence and a responsive viewport', async ({ page }) => {
    const wire = await chartWire(page);
    await colorCrosshair(page);
    await page.emulateMedia({ colorScheme: 'light' });
    await page.clock.install({ time: new Date('2026-11-12T17:41:00Z') });
    await page.setViewportSize({ width: 1440, height: 1050 });
    await page.goto('/Chart?symbol=AAPL');
    await expect.poll(() => wire.watches.length).toBe(1);
    const watch = wire.watches[0];
    expect(watch.key).toEqual({ symbol: 'AAPL', interval: '5m', sessionView: 'regular', context: 'intraday', indicators: '' });
    const chunked = frames(watch);
    expect(chunked.length).toBeGreaterThan(1);
    send(watch, [chunked[0]]);
    const chart = page.getByRole('region', { name: 'Stock price chart' });
    const plot = chart.locator('[data-chart-plot]');
    const reading = chart.locator('[data-chart-readout]');
    const data = chart.getByText('Data evidence', { exact: true });
    const candle = chart.locator('[data-chart-candle-evidence]');
    await expect(reading).toBeHidden();
    send(watch, chunked.slice(1));
    await expect(chart.locator('[data-chart-status]')).toContainText('Streaming completed candles');
    await expect(chart.locator('[data-chart-source]')).toContainText('SIP');
    await expect(reading.locator('[data-chart-values]')).toContainText('Open');
    await expect(reading.locator('[data-chart-values]')).toContainText('Volume');
    const zoneKey = chart.getByRole('list', { name: 'Current support and resistance zones' });
    await expect(zoneKey).toContainText('Support · $190.30 – $191.10');
    await expect(zoneKey).toContainText('Resistance · $198.90 – $199.80');
    await expect(zoneKey).toContainText('Current zone · 15m pivots');
    await expect.poll(() => coloredPanes(plot)).toBeGreaterThanOrEqual(2);
    await capture(chart, 'desktop');

    const requestCount = [wire.watches.length, wire.history.length];
    const caption = reading.locator('[data-chart-candle-time]');
    const values = reading.locator('[data-chart-values]');
    const latest = await caption.textContent();
    await plot.hover({ position: { x: 145, y: 110 } });
    await expect(caption).not.toHaveText(latest);
    const firstHovered = await caption.textContent();
    const firstValues = await values.textContent();
    await plot.hover({ position: { x: 320, y: 110 } });
    await expect(caption).not.toHaveText(firstHovered);
    await expect(values).not.toHaveText(firstValues);
    await expect.poll(() => crosshairVisible(plot)).toBe(true);
    expect([wire.watches.length, wire.history.length]).toEqual(requestCount);

    await plot.focus();
    await page.keyboard.press('Home');
    await expect(reading).toContainText('09:30:00 NY');
    await expect(reading).not.toContainText('14:30:00.000Z');
    await expect(reading).toContainText('$190.00');
    await expect(reading.getByRole('button', { name: 'Previous candle' })).toBeDisabled();
    await expect(candle).toBeHidden();
    await data.focus();
    await data.press('Space');
    await expect(data).toBeFocused();
    await expect(candle).toBeVisible();
    await expect(candle).toContainText('2026-11-12T14:30:00.000Z');
    await expect(candle).toContainText('2026-11-12T14:35:00.000Z');
    await expect(candle).toContainText('Original receipt');
    await expect(candle).toContainText('09:35:00.950 NY');
    await plot.focus();
    await page.keyboard.press('ArrowRight');
    await expect(reading.locator('[data-chart-candle-time]')).toContainText('09:35:00 NY');
    await expect(reading.locator('[data-chart-candle-time]')).toContainText('15:35:00 Berlin');
    await expect(candle).not.toContainText('2026-11-12T14:30:00.000Z');
    await expect(plot).toBeFocused();
    await expect.poll(() => crosshairVisible(plot)).toBe(true);
    const selectedBeforeUpdate = await caption.textContent();
    send(watch, frames(watch, { ...fixtureSnapshot(watch.key), status: 'delayed', reason: 'Waiting for the next completed candle.' }));
    await expect(chart.locator('[data-chart-status]')).toContainText('Delayed candles');
    await expect(caption).toHaveText(selectedBeforeUpdate);
    await expect.poll(() => crosshairVisible(plot)).toBe(false);
    await plot.press('ArrowRight');
    await expect.poll(() => crosshairVisible(plot)).toBe(true);
    await chart.getByRole('button', { name: 'Go to latest', exact: true }).press('Enter');
    await expect(caption).toHaveText(latest);
    await expect.poll(() => crosshairVisible(plot)).toBe(false);
    await plot.focus();
    await page.keyboard.press('End');
    await expect(reading.getByRole('button', { name: 'Next candle' })).toBeDisabled();

    await chart.getByText('Layers & price levels', { exact: true }).click();
    const volume = chart.getByRole('checkbox', { name: 'Volume', exact: true });
    const paneCanvases = await plot.locator('canvas').count();
    await volume.uncheck();
    await expect.poll(() => plot.locator('canvas').count()).toBeLessThan(paneCanvases);
    await volume.check();
    await expect(plot.locator('canvas')).toHaveCount(paneCanvases);
    await expect(chart.locator('[data-chart-zones]')).toContainText('3 confirmed pivots');
    const overlay = await overlayPixels(plot);
    const support = chart.getByRole('checkbox', { name: /^Support ·/ });
    await support.uncheck();
    await expect(zoneKey).not.toContainText('Support');
    await expect.poll(async () => (await overlayPixels(plot)).rightEdge).not.toBe(overlay.rightEdge);
    expect((await overlayPixels(plot)).history).toBe(overlay.history);
    await support.check();
    await expect(zoneKey).toContainText('Support');
    await expect.poll(async () => (await overlayPixels(plot)).rightEdge).toBe(overlay.rightEdge);
    await chart.getByRole('checkbox', { name: /Session VWAP/ }).uncheck();
    await expect.poll(async () => (await overlayPixels(plot)).history).not.toBe(overlay.history);
    await chart.getByRole('checkbox', { name: /Session VWAP/ }).check();
    await chart.getByText(/^News & earnings/).click();
    await expect(chart.locator('[data-chart-events]')).toContainText('Earnings estimate');
    await expect(chart.locator('[data-chart-events]')).toContainText('Outside loaded candle intervals');
    expect([wire.watches.length, wire.history.length]).toEqual(requestCount);

    await plot.press('End');
    await expect.poll(() => crosshairVisible(plot)).toBe(true);
    const selectedBeforeResize = await caption.textContent();
    await page.setViewportSize({ width: 390, height: 830 });
    await expect.poll(() => plot.locator('canvas').first().evaluate(canvas => canvas.width)).toBeLessThan(390);
    await expect(caption).toHaveText(selectedBeforeResize);
    await expect.poll(() => crosshairVisible(plot)).toBe(false);
    await data.focus();
    await data.press('Enter');
    await expect(candle).toBeHidden();
    await chart.getByText('Layers & price levels', { exact: true }).click();
    await chart.getByText(/^News & earnings/).click();
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
    await expect.poll(() => reading.locator('[data-chart-values] dd').evaluateAll(values =>
        values.every(value => getComputedStyle(value).whiteSpace === 'nowrap' && value.scrollWidth <= value.clientWidth))).toBe(true);
    await expect.poll(() => plot.locator('canvas').first().evaluate(canvas => canvas.width)).toBeLessThan(390);
    await expect(chart.getByRole('combobox', { name: 'Candle interval' })).toBeVisible();
    await expect(chart.locator('[data-chart-completed-note]')).toHaveText('Completed candles only');
    expect((await data.boundingBox()).height).toBeGreaterThanOrEqual(44);
    expect((await reading.getByRole('button', { name: 'Previous candle' }).boundingBox()).width).toBeGreaterThanOrEqual(44);
    await capture(chart, 'mobile');
});

test('prepends a bounded earlier page without moving the viewport and updates a same-time candle', async ({ page }) => {
    const wire = await chartWire(page);
    await page.goto('/Chart?symbol=AAPL');
    await expect.poll(() => wire.watches.length).toBe(1);
    const watch = wire.watches[0];
    const snapshot = fixtureSnapshot(watch.key);
    send(watch, frames(watch, snapshot));
    const plot = page.locator('[data-chart-plot]');
    const readout = page.locator('[data-chart-readout]');
    await expect(readout).toBeVisible();
    await plot.hover({ position: { x: 145, y: 110 } });
    const inspected = await readout.locator('[data-chart-candle-time]').textContent();
    await page.getByRole('button', { name: 'Load earlier' }).click();
    await expect(page.locator('[data-chart-history-status]')).toHaveText('38 earlier candles loaded');
    expect(wire.history).toEqual([{ key: watch.key, older: true }]);
    await plot.hover({ position: { x: 145, y: 110 } });
    await expect(readout.locator('[data-chart-candle-time]')).toHaveText(inspected);
    await page.getByRole('button', { name: 'Go to latest' }).click();
    const data = page.getByText('Data evidence', { exact: true });
    const selected = page.locator('[data-chart-candle-evidence]');
    await data.press('Enter');
    await expect(selected).toContainText(snapshot.bars.at(-1).knownAtUtc);
    const retrieved = '2026-11-12T17:41:09.1234567Z';
    const available = '2026-11-12T17:41:10.5678901Z';
    const amended = { ...snapshot, asOfUtc: '2026-11-12T17:41:11Z', bars: snapshot.bars.map((bar, index) =>
        index === snapshot.bars.length - 1 ? { ...bar, close: 198.77, high: 199.15,
            knownAtUtc: null, retrievedAtUtc: retrieved, availableAtUtc: available } : bar) };
    send(watch, frames(watch, amended));
    await expect(readout.locator('[data-chart-values]')).toContainText('$198.77');
    await expect(data).toBeFocused();
    await expect(selected).toBeVisible();
    await expect(selected).toContainText('Original receipt unavailable');
    await expect(selected).toContainText(retrieved);
    await expect(selected).toContainText(available);
    await expect(selected).toContainText('12:41:09.1234567 NY');
    await expect(selected).toContainText('18:41:09.1234567 Berlin');
    await expect(readout).not.toContainText(retrieved);
    await expect(page.getByRole('button', { name: 'Load earlier' })).toBeDisabled();
});

test('keeps daily structure and daily overlay availability on intraday candles across paging', async ({ page }) => {
    const wire = await chartWire(page);
    await page.goto('/Chart?symbol=AAPL&indicators=sma50%3A1d%2Crvol');
    await expect.poll(() => wire.watches.length).toBe(1);
    send(wire.watches[0]);
    const chart = page.getByRole('region', { name: 'Stock price chart' });
    await chart.getByRole('combobox', { name: 'Structure source' }).selectOption('daily');
    await expect.poll(() => wire.watches.length).toBe(2);
    send(wire.watches[1]);
    await chart.getByRole('combobox', { name: 'Daily lookback' }).selectOption('year');
    await expect.poll(() => wire.watches.length).toBe(3);
    const watch = wire.watches[2];
    const baseline = fixtureSnapshot(watch.key);
    send(watch, frames(watch, baseline));
    expect(watch.key).toMatchObject({ interval: '5m', context: 'year', indicators: 'rvol,sma50:1d' });
    await expect(chart.locator('[data-chart-context-label]')).toContainText('1d structure · year lookback');
    const zoneKey = chart.getByRole('list', { name: 'Current support and resistance zones' });
    await expect(zoneKey).toContainText('Current zone · 1d pivots');
    const study = chart.locator('[data-chart-study-row="sma50:1d"]');
    await expect(study).toContainText('SMA 50 · 1d');
    await expect(study).toContainText('Partial history · Warm-up or gaps exclude some observations.');
    await expect(chart.locator('[data-chart-study-row="rvol"]')).toContainText('Unavailable · Insufficient baseline');
    await study.getByText('SMA 50 · 1d', { exact: true }).click();
    await expect(study).toContainText('Reconstructed from retrieved history');
    await expect(study).toContainText('Plot after 114');

    const plot = chart.locator('[data-chart-plot]');
    const readings = chart.locator('[data-chart-study-values]');
    const data = chart.getByText('Data evidence', { exact: true });
    const selected = chart.locator('[data-chart-candle-evidence]');
    const point = chart.locator('[data-chart-indicator-evidence]');
    const availability = iso(baseline.indicators[1].availabilityRuns[0][2]);
    // Focus scrolling can move the canvas under a pointer left over the study details.
    await page.mouse.move(1, 1);
    await plot.scrollIntoViewIfNeeded();
    await plot.focus();
    await expect(plot).toBeFocused();
    await plot.press('Home');
    await expect(chart.locator('[data-chart-candle-time]')).toContainText('09:30:00 NY');
    await expect(readings).toContainText('SMA 50 · 1dUnavailable for this candle');
    for (let index = 0; index < 4; index++) await plot.press('ArrowRight');
    await expect(chart.locator('[data-chart-candle-time]')).toContainText('09:50:00 NY');
    await expect(readings).toContainText('USD');
    await expect(readings).toContainText('Same-time RVOL · 1mUnavailable for this candle');
    await expect(readings).not.toContainText(availability);
    await expect(readings).not.toContainText(currentRevision);
    await expect(chart.locator('[data-chart-study-history]')).toHaveText('Study history includes reconstructed values');
    await expect(point).toBeHidden();
    const requestsBeforeEvidence = [wire.watches.length, wire.history.length];
    await data.press('Enter');
    await expect(point).toBeVisible();
    await expect(point).toContainText(availability);
    await expect(point).toContainText('2026-11-10T21:00:00.000Z');
    await expect(point).toContainText('2026-11-13T14:00:00.000Z');
    await expect(point).toContainText(currentRevision);
    await expect(point).not.toContainText('RVOL');
    expect([wire.watches.length, wire.history.length]).toEqual(requestsBeforeEvidence);
    const selectedTime = await chart.locator('[data-chart-candle-time]').textContent();
    const revision = point.getByText(currentRevision, { exact: true });
    await revision.evaluate(node => node.dataset.retained = 'true');
    const repairing = { ...baseline, asOfUtc: '2026-11-13T14:01:00Z', status: 'repairing', reason: 'Waiting for the missing interval.',
        evidence: { ...baseline.evidence, coverage: 'partial' },
        indicators: baseline.indicators.map(item => item.id === 'rvol'
            ? { ...item, status: 'retry', reason: 'Source retry was requested.' } : item) };
    send(watch, frames(watch, repairing));
    await expect(chart.locator('[data-chart-status]')).toContainText('Repairing data · Waiting for the missing interval.');
    await expect(chart.locator('[data-chart-source]')).toContainText('Partial candle coverage');
    await expect(chart.locator('[data-chart-study-row="rvol"]')).toContainText('Retry available · Source retry was requested.');
    await expect(point).toContainText('2026-11-13T14:01:00.000Z');
    await expect(chart.locator('[data-chart-candle-time]')).toHaveText(selectedTime);
    await expect(revision).toHaveAttribute('data-retained', 'true');
    await expect(data).toBeFocused();
    await page.setViewportSize({ width: 390, height: 830 });
    await expect.poll(() => revision.evaluate(node => node.scrollWidth <= node.clientWidth)).toBe(true);
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
    await chart.getByRole('button', { name: 'Load earlier', exact: true }).click();
    await expect(chart.locator('[data-chart-history-status]')).toHaveText('38 earlier candles loaded');
    expect(wire.history).toEqual([{ key: watch.key, older: true }]);
    await expect(zoneKey).toContainText('Current zone · 1d pivots');
    await expect(chart.locator('[data-chart-candle-time]')).toHaveText(selectedTime);
    await expect(point).toContainText(availability);
    await expect(revision).toHaveAttribute('data-retained', 'true');
    await page.mouse.move(1, 1);
    await plot.press('Home');
    for (let index = 0; index < 4; index++) await plot.press('ArrowRight');
    const earlier = fixtureSnapshot(watch.key, { page: 1 });
    const earlierAvailability = iso(earlier.indicators[1].availabilityRuns[0][2]);
    await expect(chart.locator('[data-chart-study-history]')).toContainText('Separate earlier study segment');
    await expect(point).toContainText(earlierAvailability);
    await expect(point).toContainText('2026-11-12T13:58:00.000Z');
    await expect(point).toContainText(earlierRevision);
    await expect(point).not.toContainText(currentRevision);
    const earlierStart = earlier.bars[4].startUtc;
    await expect(selected).toContainText(earlierStart);
    send(watch, frames(watch, fixtureSnapshot(watch.key, { empty: true })));
    await expect(chart.locator('[data-chart-status]')).toContainText('Candles unavailable');
    await expect(selected).toContainText(earlierStart);
    await expect(point).toContainText(earlierRevision);
    for (let index = 0; index < 5; index++) await plot.press('ArrowRight');
    await expect(readings).toContainText('SMA 50 · 1dUnavailable for this candle');
    await expect(point).toBeEmpty();
    const incompatible = fixtureSnapshot(watch.key, { empty: true });
    incompatible.evidence.adjustmentPolicy = 'raw';
    send(watch, frames(watch, incompatible));
    await expect(chart.locator('[data-chart-readout]')).toBeHidden();
    await expect(selected).toBeEmpty();
    await expect(point).toBeEmpty();
    await expect(chart.locator('[data-chart-evidence]')).toContainText('raw');
    expect(wire.history).toHaveLength(1);
});

test('limits four price studies and two lower panes and switches them with a mobile keyboard', async ({ page }) => {
    const wire = await chartWire(page);
    await page.setViewportSize({ width: 390, height: 830 });
    await page.goto('/Chart?symbol=AAPL&indicators=sma20%2Csma50%2Cema9%2Cema20%2Catr14%2Cobv');
    await expect.poll(() => wire.watches.length).toBe(1);
    send(wire.watches[0]);
    const chart = page.getByRole('region', { name: 'Stock price chart' });
    const plot = chart.locator('[data-chart-plot]');
    const pane = chart.getByRole('combobox', { name: 'Visible indicator pane' });
    await expect(pane).toBeVisible();
    await expect(pane).toHaveValue('atr14');
    await expect(plot).toHaveCSS('--stock-chart-panes', '1');
    await pane.focus();
    await page.keyboard.press('ArrowDown');
    await expect(pane).toHaveValue('obv');
    await expect(plot).toHaveCSS('--stock-chart-panes', '1');
    expect(wire.watches).toHaveLength(1);

    await chart.getByText('Indicator library', { exact: true }).click();
    const indicator = chart.getByRole('combobox', { name: 'Indicator', exact: true });
    const replacement = chart.getByRole('combobox', { name: 'Replace indicator' });
    await indicator.selectOption('bb20');
    await expect(replacement.locator('option')).toHaveCount(4);
    await replacement.selectOption('sma20');
    await chart.getByRole('button', { name: 'Replace indicator', exact: true }).click();
    await expect.poll(() => wire.watches.length).toBe(2);
    send(wire.watches[1]);
    expect(wire.watches[1].key.indicators).toBe('atr14,bb20,ema20,ema9,obv,sma50');
    await indicator.selectOption('rsi14');
    await expect(replacement.locator('option')).toHaveCount(2);
    await replacement.selectOption('atr14');
    await chart.getByRole('button', { name: 'Replace indicator', exact: true }).click();
    await expect.poll(() => wire.watches.length).toBe(3);
    send(wire.watches[2]);
    expect(wire.watches[2].key.indicators).toBe('bb20,ema20,ema9,obv,rsi14,sma50');
    await expect(chart.getByRole('button', { name: 'Remove ATR 14 · 5m' })).toHaveCount(0);
    await expect(chart.getByRole('button', { name: 'Remove Bollinger Bands 20/2 · 5m' })).toBeVisible();
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);

    await page.setViewportSize({ width: 1440, height: 1050 });
    await expect(pane).toBeHidden();
    await expect(plot).toHaveCSS('--stock-chart-panes', '2');
    await capture(chart, 'indicator-panes');
});

test('applies named presets, saves browser preferences and resets to the base chart', async ({ page }) => {
    const wire = await chartWire(page);
    await page.goto('/Chart?symbol=AAPL');
    await expect.poll(() => wire.watches.length).toBe(1);
    send(wire.watches[0]);
    const chart = page.getByRole('region', { name: 'Stock price chart' });
    const preset = chart.getByRole('combobox', { name: 'Indicator preset' });
    await preset.selectOption('swing');
    await expect.poll(() => wire.watches.length).toBe(2);
    expect(wire.watches[1].key).toMatchObject({ interval: '1d', indicators: 'atr14:readout,rsi14,sma200:1d,sma50:1d' });
    send(wire.watches[1]);
    await expect(chart.getByRole('button', { name: 'Remove RSI 14 · 1d' })).toBeVisible();
    await page.reload();
    await expect.poll(() => wire.watches.length).toBe(3);
    expect(wire.watches[2].key).toEqual(wire.watches[1].key);
    send(wire.watches[2]);
    await expect(preset).toHaveValue('swing');
    await preset.selectOption('intraday');
    await expect.poll(() => wire.watches.length).toBe(4);
    expect(wire.watches[3].key).toMatchObject({ interval: '5m', indicators: 'atr14:readout,ema20,vwap' });
    send(wire.watches[3]);
    await expect(chart.getByRole('button', { name: 'Remove RTH VWAP · 1m' })).toBeVisible();
    await chart.getByRole('button', { name: 'Reset chart settings' }).click();
    await expect.poll(() => wire.watches.length).toBe(5);
    send(wire.watches[4]);
    await expect(preset).toHaveValue('custom');
    await expect(chart.getByRole('list', { name: 'Selected indicators' })).toBeHidden();
    expect(wire.watches[4].key.indicators).toBe('');
    expect(await page.evaluate(() => localStorage.getItem('tradingflow.stock-chart.v2'))).toBeNull();
});

test('reloads an expired earlier-page cursor without hiding current candles', async ({ page }) => {
    const expired = 'expired-next-page';
    const fresh = 'fresh-previous-page';
    const cursors = [];
    const wire = await chartWire(page, (snapshot, query) => {
        const cursor = query.get('cursor');
        cursors.push(cursor);
        if (cursor === expired) return { status: 400, json: { message: 'Expired chart history cursor.' } };
        return { json: { ...snapshot, historyCursor: cursor === fresh ? null : expired } };
    });
    await page.goto('/Chart?symbol=AAPL');
    await expect.poll(() => wire.watches.length).toBe(1);
    const initial = wire.watches[0];
    send(initial);

    const earlier = page.getByRole('button', { name: 'Load earlier', exact: true });
    const reload = page.getByRole('button', { name: 'Reload earlier history', exact: true });
    const reading = page.locator('[data-chart-readout]');
    const status = page.locator('[data-chart-history-status]');
    await earlier.click();
    await expect(status).toHaveText('38 earlier candles loaded');
    await earlier.click();
    await expect(status).toContainText('This history link expired.');
    await expect(earlier).toBeDisabled();
    await expect(reading).toBeVisible();

    await reload.click();
    await expect(status).toContainText('Reloading earlier history');
    await expect(reading).toBeVisible();
    await expect.poll(() => wire.watches.length).toBe(2);
    const refreshed = wire.watches[1];
    expect(refreshed.key).toEqual(initial.key);
    send(refreshed, frames(refreshed, { ...fixtureSnapshot(refreshed.key), historyCursor: fresh }));
    await expect(status).toHaveText('Earlier history links refreshed.');
    await expect(reload).toBeHidden();
    await earlier.click();
    await expect(status).toHaveText('38 earlier candles loaded');
    expect(cursors).toEqual(['previous-fixture-page', expired, fresh]);
});

test('keeps Desk on demand and rejects queued or wrong-context frames on selection changes', async ({ page }) => {
    const wire = await chartWire(page);
    await deskSource(page);
    await page.goto('/Desk');
    await expect(page.getByRole('button', { name: 'AAPL', exact: true })).toBeVisible();
    expect(wire.watches).toHaveLength(0);
    expect(wire.history).toHaveLength(0);

    await page.getByRole('button', { name: 'AAPL', exact: true }).click();
    await expect.poll(() => wire.watches.length).toBe(1);
    const chart = page.getByRole('region', { name: 'Stock price chart' });
    const data = chart.getByText('Data evidence', { exact: true });
    const selected = chart.locator('[data-chart-candle-evidence]');
    const aapl = fixtureSnapshot(wire.watches[0].key);
    const aaplReceipt = '2026-11-12T17:40:02.1234567Z';
    aapl.bars.at(-1).knownAtUtc = aaplReceipt;
    aapl.bars.at(-1).availableAtUtc = aaplReceipt;
    send(wire.watches[0], frames(wire.watches[0], aapl));
    await expect(page.locator('[data-chart-readout]')).toBeVisible();
    await data.press('Enter');
    await expect(selected).toContainText(aaplReceipt);
    await expect(page.getByRole('link', { name: 'Open full chart for AAPL' })).toHaveAttribute('href', /\/Chart\?.*symbol=AAPL/);

    await page.getByRole('button', { name: 'MSFT', exact: true }).click();
    await expect.poll(() => wire.watches.length).toBe(2);
    await expect.poll(() => wire.watches[0].closed).toBe(true);
    send(wire.watches[1], frames(wire.watches[1], fixtureSnapshot(wire.watches[0].key)));
    await expect(page.locator('[data-chart-readout]')).toBeHidden();
    await expect(selected).toBeEmpty();
    send(wire.watches[1]);
    await expect(page.locator('[data-chart-readout]')).toContainText(/\$31[0-9]/);
    await expect(selected).toBeVisible();
    await expect(selected).not.toContainText(aaplReceipt);

    await chart.getByRole('combobox', { name: 'Session' }).selectOption('extended');
    await expect.poll(() => wire.watches.length).toBe(3);
    send(wire.watches[2]);
    await expect(chart.locator('[data-chart-source]')).toContainText('Premarket / Postmarket');
    await chart.getByRole('combobox', { name: 'Candle interval' }).selectOption('1d');
    await expect.poll(() => wire.watches.length).toBe(4);
    expect(wire.watches[3].key).toMatchObject({ interval: '1d', sessionView: 'regular', context: 'swing' });
    await expect(selected).toBeEmpty();
    await expect(chart.getByRole('combobox', { name: 'Session' })).toBeDisabled();
    await chart.getByRole('combobox', { name: 'Daily lookback' }).selectOption('year');
    await expect.poll(() => wire.watches.length).toBe(5);
    const daily = fixtureSnapshot(wire.watches[4].key);
    daily.levels.push({
        key: 'previous-close', label: 'Previous close', value: 313, effectiveAtUtc: '2026-11-12T00:00:00Z',
        distancePct: 1.25, sourceTimeframe: '1d', points: []
    });
    send(wire.watches[4], frames(wire.watches[4], daily));
    await expect(chart.locator('[data-chart-context-label]')).toContainText('1d structure · year lookback');
    await expect(chart.locator('[data-chart-source]')).toContainText('Reconstructed from retrieved history');
    await expect(chart.locator('[data-chart-source]')).toContainText('Calculations shown at source candle close. Original availability may be later.');
    await expect(chart.locator('[data-chart-status]')).toContainText('History only');
    const caption = chart.locator('[data-chart-candle-time]');
    await expect(caption).toHaveText('Nov 12, 2026 · Daily');
    await expect(selected).toContainText('Nov 11, 2026, 19:00:00.000 NY');
    await expect(selected).toContainText('2026-11-12T00:00:00.000Z');
    await expect(chart.locator('[data-chart-readout]')).not.toContainText('2026-11-13T14:00:00.000Z');
    const layers = chart.getByText('Layers & price levels', { exact: true });
    const comparisons = chart.locator('[data-chart-distances]');
    await expect(comparisons).toBeHidden();
    const requestCount = [wire.watches.length, wire.history.length];
    await layers.press('Enter');
    await chart.locator('[data-chart-plot]').focus();
    await page.keyboard.press('End');
    await expect(comparisons).toBeVisible();
    await expect(comparisons.getByText('Daily SMA50', { exact: true })).toBeVisible();
    await expect(comparisons).toContainText('Reconstructed for this close');
    await expect(comparisons).toContainText('2026-11-13T14:00:00.000Z');
    await expect(comparisons).toContainText('Latest comparison +1.25%');
    await page.keyboard.press('ArrowLeft');
    await expect(comparisons).toBeEmpty();
    await expect(comparisons).toBeHidden();
    expect([wire.watches.length, wire.history.length]).toEqual(requestCount);
    await expect(chart.locator('[data-chart-zones]')).toContainText('shown at the right edge');
    const fullChart = page.getByRole('link', { name: 'Open full chart for MSFT' });
    await fullChart.focus();
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/\/Chart\?.*symbol=MSFT/);
    await expect.poll(() => wire.watches.length).toBe(6);
    expect(wire.watches[5].key).toMatchObject({ symbol: 'MSFT', interval: '1d', context: 'year' });
    await page.goBack();
    await expect(page).toHaveURL(/\/Desk/);
});

test('retains the last candles during reconnect and renders explicit empty or unauthorized reads', async ({ page }) => {
    const wire = await chartWire(page, 403);
    await page.goto('/Chart?symbol=AAPL');
    await expect.poll(() => wire.watches.length).toBe(1);
    const watch = wire.watches[0];
    const baseline = fixtureSnapshot(watch.key);
    send(watch, frames(watch, { ...baseline, status: 'closed' }));
    const status = page.locator('[data-chart-status]');
    const reading = page.locator('[data-chart-readout]');
    const selected = page.locator('[data-chart-candle-evidence]');
    await expect(status).toHaveText('Market closed');
    send(watch, frames(watch, { ...baseline, status: 'delayed', reason: 'Latest minute is overdue.' }));
    await page.getByText('Data evidence', { exact: true }).press('Enter');
    await expect(selected).toContainText('Original receipt');
    const acceptedEvidence = await selected.textContent();
    await expect(status).toContainText('Delayed candles');
    send(watch, [{ type: 3, invocationId: watch.invocationId }]);
    await expect(status).toContainText('Reconnecting chart stream');
    await expect(status).toContainText('Showing last available candles');
    await expect(reading).toBeVisible();
    await expect(selected).toHaveText(acceptedEvidence);
    await expect.poll(() => wire.watches.length).toBe(2);
    send(wire.watches[1], frames(wire.watches[1], fixtureSnapshot(wire.watches[1].key, { empty: true })));
    await expect(status).toContainText('Candles unavailable');
    await expect(page.locator('[data-chart-empty]')).toContainText('No completed candles');
    await expect(reading).toBeHidden();
    await expect(selected).toBeEmpty();

    await page.getByRole('combobox', { name: 'Candle interval' }).selectOption('15m');
    await expect.poll(() => wire.watches.length).toBe(3);
    wire.watches[2].ws.close();
    await expect(status).toContainText('This stock is not in the recorded workspace');
    await expect(page.getByRole('button', { name: 'Retry chart' })).toBeVisible();
    await expect(reading).toBeHidden();
});

test('explains the snapshot ceiling over HTTP and WebSocket while keeping valid candles', async ({ page }) => {
    const wire = await chartWire(page, 413);
    await page.goto('/Chart?symbol=AAPL&indicators=ema20');
    await expect.poll(() => wire.watches.length).toBe(1);
    const watch = wire.watches[0];
    send(watch);
    const chart = page.getByRole('region', { name: 'Stock price chart' });
    const reading = chart.locator('[data-chart-readout]');
    await expect(reading).toBeVisible();
    const lastCandle = await reading.locator('[data-chart-candle-time]').textContent();
    const ceiling = 'Chart exceeds the bounded snapshot size. Choose fewer indicators or a smaller view.';
    await chart.getByRole('button', { name: 'Load earlier' }).click();
    await expect(chart.locator('[data-chart-history-status]')).toHaveText(ceiling);
    await expect(reading.locator('[data-chart-candle-time]')).toHaveText(lastCandle);
    expect(wire.history).toEqual([{ key: watch.key, older: true }]);

    // Match SignalR's completion envelope for a streamed HubException after a good baseline.
    send(watch, [{ type: 3, invocationId: watch.invocationId,
        error: "An unexpected error occurred invoking 'Watch' on the server. HubException: " + ceiling }]);
    await expect(chart.locator('[data-chart-status]')).toContainText(ceiling);
    await expect(reading).toBeVisible();
    await expect(reading.locator('[data-chart-candle-time]')).toHaveText(lastCandle);
    const retry = chart.getByRole('button', { name: 'Retry chart' });
    await expect(retry).toBeVisible();
    await retry.click();
    await expect.poll(() => wire.watches.length).toBe(2);
    expect(wire.watches[1].key).toEqual(watch.key);
    send(wire.watches[1]);
    await expect(chart.locator('[data-chart-status]')).toHaveText('Streaming completed candles');
    await expect(retry).toBeHidden();
});

// Assert the actual navigation target without relying on relative versus absolute HTML serialization.
async function expectChartLink(link, symbol) {
    await expect(link).toBeVisible();
    const target = await link.evaluate(anchor => new URL(anchor.href).pathname + '|' + new URL(anchor.href).searchParams.get('symbol'));
    expect(target).toBe('/Chart|' + symbol);
}

test('reuses the stock chart route from stock groups, News, Trading, Earnings and candidate audit', async ({ page }) => {
    const wire = await chartWire(page);
    const symbol = 'MSFT';
    const listId = await page.evaluate(async ticker => {
        const settings = { method: 'POST', headers: { 'Content-Type': 'application/json' } };
        const listResponse = await window.tradingFlowFetch('/api/v1/wishlists', {
            ...settings, body: JSON.stringify({ name: 'Chart navigation fixture ' + crypto.randomUUID() })
        });
        const list = await listResponse.json();
        const populated = await window.tradingFlowFetch('/api/v1/wishlists/bulk', {
            ...settings, body: JSON.stringify({
                commandId: crypto.randomUUID(), operation: 'add', targetId: list.id, tickers: [ticker]
            })
        });
        if (!populated.ok) throw new Error('Could not add the isolated chart-link fixture stock.');
        return list.id;
    }, symbol);

    try {
        await page.goto('/Wishlists?id=' + listId);
        await expectChartLink(page.locator('[data-bulk-form]').getByRole('link', { name: 'Open chart for MSFT' }), symbol);
        await page.goto('/News?stockSource=wishlist:' + listId);
        await expectChartLink(page.getByRole('complementary', { name: 'Stocks in this source' })
            .getByRole('link', { name: 'Open chart for MSFT' }), symbol);

        await page.route('**/api/v1/running-trades?source=all', route => route.fulfill({ json: {
            available: true, readAtUtc: new Date().toISOString(),
            trades: [{ ticker: symbol, quantity: 10, entryPrice: 200, holding: { marketValue: 2400, costBasis: 2000 } }]
        } }));
        await page.goto('/Orders');
        const holding = page.locator('[data-position-symbol=MSFT]');
        await expectChartLink(holding.getByRole('link', { name: 'Open chart for MSFT' }), symbol);
        await expect(holding.getByRole('link', { name: 'Review' })).toHaveAttribute('href', /\/Orders\?.*ticker=MSFT/);

        await page.route('**/api/earnings/today-next-business-day?*', route => route.fulfill({ json: {
            generatedAtUtc: iso(tradingDay), monitoringActive: false, monitoringIntervalSeconds: 60,
            nextBusinessDateLabel: 'Friday', newsWindowLabel: 'Stored evidence', filters: { session: 'all', marketCap: 'all' },
            news: [], items: [{
                id: 'fixture-earnings', ticker: symbol, companyName: 'Fixture Company', marketCapMillions: 100_000,
                scheduledAtUtc: iso(tradingDay), providerReceivedAtUtc: iso(tradingDay - minute),
                provider: 'calendar', releaseWindowLabel: 'Regular market · estimated', isScheduleEstimate: true,
                epsOutcome: 'Pending', breakoutAssessment: 'AwaitingEarningsRelease', analysisReason: 'Awaiting results'
            }], totalMatching: 1
        } }));
        await page.goto('/Earnings');
        await expectChartLink(page.locator('#earnings-feed').getByRole('link', { name: 'Open chart for MSFT' }), symbol);

        await page.route('**/api/v1/candidates/*/audit', route => route.fulfill({ json: {
            candidate: {
                symbol, readiness: 'recorded', strategy: { strategyId: 'fixture', semanticVersion: '1.0.0', contentSha256: 'a'.repeat(64) }
            }, transitions: [], gateEvaluations: []
        } }));
        await page.goto('/CandidateAudit?candidateId=11111111-1111-1111-1111-111111111111');
        await expectChartLink(page.locator('[data-audit-chart]').getByRole('link', { name: 'Open chart for MSFT' }), symbol);
        expect(wire.watches).toHaveLength(0);
        expect(wire.history).toHaveLength(0);
    } finally {
        await page.evaluate(id => window.tradingFlowFetch('/api/v1/wishlists/' + id, { method: 'DELETE' }), listId);
    }
});
