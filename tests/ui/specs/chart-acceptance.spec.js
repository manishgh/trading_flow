import { expect, test } from '@playwright/test';
import { fileURLToPath } from 'node:url';

const script = name => fileURLToPath(new URL('../../../src/TradingFlow.Web/wwwroot/js/' + name, import.meta.url));
const token = 'fixture-one-use-watch';

async function panel(page, failStart = false) {
    await page.setContent(`<section data-chart-acceptance>
        <button data-acceptance-start disabled>Start five-minute check</button>
        <button data-acceptance-stop disabled>Stop check</button>
        <button data-acceptance-refresh>Refresh check status</button>
        <p role="status" data-acceptance-status></p><p data-acceptance-evidence></p>
        <div data-acceptance-chart hidden><section data-stock-chart></section></div></section>`);
    await page.evaluate(({ token, failStart }) => {
        window.calls = []; window.mounts = []; window.disposals = 0;
        window.serverState = { state: 'idle' };
        window.tradingFlowFetch = async (path, init) => {
            window.calls.push({ path, ...init });
            if (path.endsWith('/start')) {
                window.serverState = { sessionId: 'test-session', state: 'observing', deadlineUtc: new Date(Date.now() + 300000).toISOString() };
                if (failStart) throw new Error('Response lost');
                return new Response(JSON.stringify({ ...window.serverState, correlation: token }));
            }
            if (path.endsWith('/stop')) {
                window.disposalsAtStopRequest = window.disposals;
                window.serverState = { ...window.serverState, state: 'stopped', scopeRemovalRequested: true };
            }
            return new Response(JSON.stringify(window.serverState));
        };
        window.TradingFlowStockChart = { mount(root, options) {
            window.mounts.push(options.acceptanceCorrelation);
            window.ended = options.onAcceptanceEnded;
            return { dispose() { window.disposals++; return Promise.resolve(); } };
        } };
    }, { token, failStart });
    await page.addScriptTag({ path: script('chart-acceptance.js') });
    await expect(page.getByRole('button', { name: 'Start five-minute check' })).toBeEnabled();
}

test('acceptance capability stays in the initiating watch and stop confirms server state', async ({ page }) => {
    await panel(page);
    await page.getByRole('button', { name: 'Start five-minute check' }).click();
    await expect(page.getByRole('status')).toContainText('Observing AAPL');
    expect(await page.evaluate(() => window.mounts)).toEqual([token]);
    expect(await page.content()).not.toContain(token);
    expect(page.url()).not.toContain(token);
    expect(await page.evaluate(() => JSON.stringify(window.calls))).not.toContain(token);
    await page.evaluate(() => window.ended('Socket disconnected'));
    await expect(page.getByRole('status')).toContainText('Observation stopped');
    await expect(page.locator('[data-acceptance-chart]')).toBeHidden();
    await expect(page.getByRole('button', { name: 'Start five-minute check' })).toBeDisabled();
    expect(await page.evaluate(() => window.calls.filter(x => x.path.endsWith('/stop')).length)).toBe(1);
    expect(await page.evaluate(() => window.disposalsAtStopRequest)).toBe(0);
});

test('ambiguous Start never retries and status permits stopping the already accepted session', async ({ page }) => {
    await panel(page, true);
    await page.getByRole('button', { name: 'Start five-minute check' }).click();
    await expect(page.getByRole('status')).toContainText('Start will not be retried');
    await page.getByRole('button', { name: 'Refresh check status' }).click();
    await expect(page.getByRole('status')).toContainText('no attached acceptance watch');
    await expect(page.getByRole('button', { name: 'Start five-minute check' })).toBeDisabled();
    await page.getByRole('button', { name: 'Stop check', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('Observation stopped');
    expect(await page.evaluate(() => window.calls.filter(x => x.path.endsWith('/start')).length)).toBe(1);
    expect(await page.evaluate(() => window.mounts)).toEqual([]);
});

test('a lost watch during a pending status read latches stop until that read finishes', async ({ page }) => {
    await panel(page);
    await page.getByRole('button', { name: 'Start five-minute check' }).click();
    await expect(page.getByRole('status')).toContainText('Observing AAPL');
    await page.evaluate(() => {
        const original = window.tradingFlowFetch;
        window.tradingFlowFetch = (path, init) => init.method === 'GET'
            ? new Promise(resolve => { window.finishStatus = () => resolve(new Response(JSON.stringify(window.serverState))); })
            : original(path, init);
    });
    await page.getByRole('button', { name: 'Refresh check status' }).click();
    await expect.poll(() => page.evaluate(() => typeof window.finishStatus)).toBe('function');
    await page.evaluate(() => window.ended('Connection lost during status poll'));
    expect(await page.evaluate(() => window.calls.filter(x => x.path.endsWith('/stop')).length)).toBe(0);
    await page.evaluate(() => window.finishStatus());
    await expect(page.getByRole('status')).toContainText('Observation stopped');
    expect(await page.evaluate(() => window.calls.filter(x => x.path.endsWith('/stop')).length)).toBe(1);
    expect(await page.evaluate(() => window.disposalsAtStopRequest)).toBe(0);
});

test('acceptance transport never reconnects or substitutes history after a socket closes', async ({ page }) => {
    await page.setContent('<p>Transport fixture</p>');
    await page.evaluate(() => {
        window.starts = 0; window.invocations = []; window.states = []; window.fetches = 0;
        window.fetch = async () => { window.fetches++; throw new Error('No history fallback allowed'); };
        class Builder {
            withUrl() { return this; } configureLogging() { return this; }
            build() { return {
                onclose(callback) { window.fixtureSocketClosed = callback; },
                start() { window.starts++; return Promise.resolve(); }, stop() { return Promise.resolve(); },
                stream(...args) { window.invocations.push(structuredClone(args)); return { subscribe() { return { dispose() {} }; } }; }
            }; }
        }
        window.signalR = { HubConnectionBuilder: Builder, HttpTransportType: { WebSockets: 1 }, LogLevel: { None: 0 } };
    });
    await page.addScriptTag({ path: script('stock-chart-transport.js') });
    await page.evaluate(token => {
        window.options = { formingPreview: true, acceptanceCorrelation: token };
        window.dispose = window.TradingFlowStockChartTransport.watch({ hub: '/test', history: '/history' },
            { symbol: 'AAPL', interval: '1m', sessionView: 'regular', context: 'auto', indicators: '' },
            window.options, new AbortController(), { status: text => window.states.push(text) }, Promise.resolve());
    }, token);
    await expect.poll(() => page.evaluate(() => window.invocations.length)).toBe(1);
    expect(await page.evaluate(() => window.options.acceptanceCorrelation)).toBeNull();
    await page.evaluate(() => window.fixtureSocketClosed());
    await expect.poll(() => page.evaluate(() => window.states.join(' '))).toContain('cannot reconnect');
    await page.waitForTimeout(1500);
    expect(await page.evaluate(() => ({ starts: window.starts, fetches: window.fetches }))).toEqual({ starts: 1, fetches: 0 });
    expect(await page.evaluate(() => window.invocations[0][2].acceptanceCorrelation)).toBe(token);
});
