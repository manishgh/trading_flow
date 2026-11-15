import { expect, test } from '@playwright/test';
import { fileURLToPath } from 'node:url';

const script = fileURLToPath(new URL('../../../src/TradingFlow.Web/wwwroot/js/stock-chart-private.js', import.meta.url));
const evidenceAt = '2026-11-13T14:31:01.1234567Z';

// Exercise the private module in a page-local fixture without broker state, operating grants, or provider requests.
test('private chart evidence is time-only and a context switch drops the older reply', async ({ page }) => {
    await page.setContent(`
        <section id="chart">
            <dialog data-chart-details>
                <div class="stock-chart-toggles"></div>
            </dialog>
        </section>`);
    await page.addScriptTag({ path: script });

    await page.evaluate(() => {
        window.requests = [];
        window.renderedPrivateMarkers = [];
        window.tradingFlowFetch = (path, init) => new Promise(resolve => window.requests.push({
            path, body: JSON.parse(init.body), signal: init.signal, resolve
        }));
        window.context = {
            token: 'fixture-private-capability', symbol: 'AAPL', endpoint: '/api/v1/charts/paper-evidence',
            issuedAtUtc: '2026-11-13T14:30:00Z', expiresAtUtc: '2026-11-13T14:35:00Z'
        };
        window.privateLayer = window.TradingFlowPrivateChart.mount(document.getElementById('chart'), window.context,
            markers => { window.renderedPrivateMarkers = markers; }, text => text);
        document.querySelector('dialog').showModal();
        window.range = {
            fromMs: Date.parse('2026-11-13T14:30:00Z'),
            toMs: Date.parse('2026-11-13T14:35:00Z'),
            asOfMs: Date.parse('2026-11-13T14:35:00Z')
        };
        window.privateLayer.setWindow({ ...window.range, fromMs: Date.parse('2026-09-01T14:30:00Z') });
    });
    await expect(page.getByText(/window of 31 days or less/)).toBeVisible();
    expect(await page.evaluate(() => window.requests.length)).toBe(0);
    await page.evaluate(() => window.privateLayer.setWindow(window.range));
    await expect.poll(() => page.evaluate(() => window.requests.length)).toBe(1);
    await page.evaluate((at) => window.requests[0].resolve(new Response(JSON.stringify({
        readAtUtc: at, events: [
            { id: 'fill:exec-1', label: 'Entry execution', eventAtUtc: at, clientOrderId: 'picked-order',
                rawPrice: '101.12345678901234567', quantity: '1.5', executionId: 'exec-1' },
            { id: 'fill:unknown-clock', label: 'Exit execution', eventAtUtc: null,
                clientOrderId: 'picked-order', executionId: 'unknown-clock' }
        ],
        activeLevels: [], truncated: false, incomplete: false
    }))), evidenceAt);
    await expect(page.getByText('Raw price $101.12345678901234567 · Price overlay unavailable')).toBeVisible();
    await expect(page.getByText('Recorded time unavailable')).toBeVisible();
    await expect(page.getByText(evidenceAt, { exact: true })).toBeVisible();
    expect(await page.evaluate(() => window.renderedPrivateMarkers)).toEqual([{ id: 'fill:exec-1', eventAtUtc: evidenceAt }]);
    expect(await page.evaluate(() => window.context.token)).toBeNull();

    await page.evaluate(() => window.privateLayer.setWindow({ ...window.range, fromMs: window.range.fromMs + 60_000 }));
    await expect.poll(() => page.evaluate(() => window.requests.length)).toBe(2);
    await page.evaluate(() => {
        window.privateLayer.reset();
        window.requests[1].resolve(new Response(JSON.stringify({
            readAtUtc: '2026-11-13T14:35:00Z', events: [{ id: 'private-old', label: 'Outdated evidence' }], activeLevels: []
        })));
    });
    expect(await page.evaluate(() => window.requests[1].signal.aborted)).toBe(true);
    await expect(page.getByText('Outdated evidence')).toHaveCount(0);
    await expect(page.getByText(/Raw price/)).toHaveCount(0);
    expect(await page.evaluate(() => window.renderedPrivateMarkers)).toEqual([]);

    await page.evaluate(() => window.privateLayer.setWindow(window.range));
    await expect.poll(() => page.evaluate(() => window.requests.length)).toBe(3);
    await page.evaluate(() => window.requests[2].resolve(new Response('', { status: 404 })));
    await expect(page.getByText(/Open the selection again/)).toBeVisible();
    await expect(page.getByLabel('Paper order evidence')).toBeDisabled();
});
