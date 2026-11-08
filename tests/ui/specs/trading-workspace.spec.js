import { expect, test } from '@playwright/test';
import { signIn, signInAsAdmin } from './sign-in.js';

const json = (route, value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
const holding = (ticker = 'MSFT', quantity = 4) => ({ ticker, quantity, entryPrice: 100, currentPrice: 105,
    holding: { symbol: ticker, quantity, marketValue: quantity * 105, costBasis: quantity * 100 }, unrealizedPl: 20,
    protectionKnown: true, hasBrokerStop: true, protectionSummary: 'Working broker stop · 4 shares' });
const snapshot = trades => ({ available: true, detail: 'Isolated broker fixture', readAtUtc: new Date().toISOString(),
    trades, slotsUsed: trades.length, unprotectedCount: 0, unknownProtectionCount: 0,
    totalUnrealizedPl: trades.some(trade => trade.unrealizedPl == null) ? null : trades.reduce((total, trade) => total + trade.unrealizedPl, 0) });

test.describe('unified trading workspace', () => {
    test.beforeEach(async ({ page }) => {
        await signIn(page);
        await page.route('**/api/v1/orders?*', route => json(route, []));
    });

    test('positions, orders and the reviewed ticket share one screen', async ({ page }, testInfo) => {
        await page.setViewportSize({ width: 1440, height: 900 });
        await page.route('**/api/v1/running-trades?source=all', route => json(route, snapshot([holding()])));
        await page.goto('/Orders?env=paper');
        await expect(page.getByRole('heading', { name: 'Trading', exact: true })).toBeVisible();
        await expect(page.locator('#TradingPositionsTitle')).toContainText('Positions');
        await expect(page.locator('.orders-journal-head')).toContainText('Orders');
        await expect(page.locator('[data-position-symbol="MSFT"]')).toContainText('Working broker stop');
        await expect(page.locator('[data-orders-portfolio]')).toContainText('Portfolio · 1 open');
        await expect(page.locator('[data-orders-portfolio] .metric-value')).toHaveText('$420.00');
        await expect(page.locator('[data-position-symbol="MSFT"] td').nth(4)).toHaveText('+$20.00');
        await expect(page.locator('[data-position-symbol="MSFT"] td').nth(4)).toHaveClass(/value-signed up/);
        await expect(page.locator('[data-position-status]')).toContainText('refreshes every 10 s');
        await expect(page.locator('.orders-rail')).toContainText('Choose a symbol');
        await page.locator('#OrdersNewTicker').fill('MSFT');
        await page.getByRole('button', { name: 'Open ticket', exact: true }).click();
        await expect(page.locator('.orders-rail [data-selected-symbol="MSFT"]')).toBeVisible();
        await expect(page.locator('[data-order-status]')).toContainText('authorized paper candidate');
        await expect(page.locator('#OrderWorkflowForm')).toBeHidden();
        await expect(page.locator('[data-order-candidates]')).toBeVisible();
        await expect(page.locator('#TradingPositions')).toBeVisible();
        await expect(page.locator('.orders-journal-head')).toContainText('Orders');
        await page.screenshot({ path: testInfo.outputPath('trading-workspace-1440.png'), fullPage: true });
    });

    test('the portfolio header uses signed holdings from the same snapshot and preserves missing values', async ({ page }) => {
        let value = { ...snapshot([holding(), holding('SHORT', -3)]), grossExposure: 735 };
        await page.route('**/api/v1/running-trades?source=all', route => json(route, value));
        await page.goto('/Orders?env=paper');
        await expect(page.locator('[data-orders-portfolio]')).toContainText('Portfolio · 2 open');
        await expect(page.locator('[data-orders-portfolio] .metric-value')).toHaveText('$105.00');
        value = snapshot([{ ...holding(), holding: { costBasis: 400, marketValue: null } }]);
        await page.locator('[data-trading-positions]').evaluate(root => root.dispatchEvent(new Event('tradingflow:refresh')));
        await expect(page.locator('[data-orders-portfolio]')).toContainText('Portfolio · 1 open');
        await expect(page.locator('[data-orders-portfolio] .metric-value')).toHaveText('Unknown');
        value = snapshot([holding('SHORT', -3)]);
        await page.locator('[data-trading-positions]').evaluate(root => root.dispatchEvent(new Event('tradingflow:refresh')));
        await expect(page.locator('[data-orders-portfolio] .metric-detail')).toContainText('+$20.00 (−6.67%)');
    });

    test('a failed broker read stays unknown until a known empty account is received', async ({ page }) => {
        let value = { available: false, detail: 'Broker read failed', readAtUtc: new Date().toISOString(), trades: [] };
        await page.route('**/api/v1/running-trades?source=all', route => json(route, value));
        await page.goto('/Orders?env=paper');
        await expect(page.locator('[data-position-count]')).toHaveText('Unknown');
        await expect(page.locator('[data-orders-portfolio]')).toContainText('Portfolio · unavailable');
        await expect(page.locator('[data-orders-portfolio]')).toHaveAttribute('data-state', 'unavailable');
        await expect(page.locator('[data-position-empty]')).toContainText('does not mean the account has no positions');
        await expect(page.locator('[data-position-empty]')).not.toContainText('No open positions');
        value = snapshot([]);
        await page.locator('[data-trading-positions]').evaluate(root => root.dispatchEvent(new Event('tradingflow:refresh')));
        await expect(page.locator('[data-position-count]')).toHaveText('0');
        await expect(page.locator('[data-orders-portfolio]')).toContainText('Portfolio · 0 open');
        await expect(page.locator('[data-orders-portfolio] .metric-value')).toHaveText('$0.00');
        await expect(page.locator('[data-position-empty]')).toHaveText('No open positions in the account.');
        await expect(page.locator('[data-position-status]')).toHaveAttribute('data-state', 'live');
    });

    test('empty journal and position polls update only their own table regions', async ({ page }) => {
        let positions = snapshot([holding()]);
        let orders = [{ clientOrderId: 'workspace-fixture', symbol: 'MSFT', side: 'Buy', state: 'Filled', requestedQuantity: 4,
            filledQuantity: 4, orderType: 'Limit', timeInForce: 'Day', fillPrice: 100, strategyId: 'fixture', updatedAtUtc: new Date().toISOString() }];
        await page.route('**/api/v1/running-trades?source=all', route => json(route, positions));
        await page.route('**/api/v1/orders?*', route => json(route, orders));
        await page.goto('/Orders?env=paper');
        await expect(page.locator('[data-orders-region]')).toContainText('workspace-fixture');
        await expect(page.locator('[data-position-symbol="MSFT"]')).toBeVisible();
        orders = [];
        await page.locator('[data-orders-page]').evaluate(root => root.dispatchEvent(new Event('tradingflow:refresh')));
        await expect(page.locator('[data-orders-region]')).toHaveCount(0);
        await expect(page.locator('[data-orders-empty]')).toBeVisible();
        await expect(page.locator('[data-position-symbol="MSFT"]')).toBeVisible();
        orders = [{ clientOrderId: 'workspace-restored', symbol: 'MSFT', side: 'Buy', state: 'Filled', requestedQuantity: 4,
            filledQuantity: 4, orderType: 'Limit', timeInForce: 'Day', fillPrice: 100, strategyId: 'fixture', updatedAtUtc: new Date().toISOString() }];
        await page.locator('[data-orders-page]').evaluate(root => root.dispatchEvent(new Event('tradingflow:refresh')));
        await expect(page.locator('[data-orders-region]')).toContainText('workspace-restored');
        positions = snapshot([]);
        await page.locator('[data-trading-positions]').evaluate(root => root.dispatchEvent(new Event('tradingflow:refresh')));
        await expect(page.locator('[data-position-empty]')).toHaveText('No open positions in the account.');
        await expect(page.locator('[data-orders-region]')).toContainText('workspace-restored');
    });

    test('polling preserves focus and stale holdings cannot open a review', async ({ page }) => {
        let value = snapshot([holding()]);
        let fail = false;
        await page.route('**/api/v1/running-trades?source=all', route => fail ? json(route, { detail: 'offline' }, 503) : json(route, value));
        await page.goto('/Orders?env=paper');
        const review = page.locator('[data-position-review]');
        await expect(review).toHaveAttribute('href', /ticker=MSFT/);
        await review.focus();
        value = snapshot([{ ...holding(), quantity: 6, holding: { marketValue: 630, costBasis: 600 }, unrealizedPl: null, protectionKnown: false, hasBrokerStop: false, protectionSummary: 'Broker protection could not be read' }]);
        await page.locator('[data-trading-positions]').evaluate(root => root.dispatchEvent(new Event('tradingflow:refresh')));
        await expect(page.locator('[data-position-symbol="MSFT"]')).toContainText('Broker protection could not be read');
        await expect(review).toBeFocused();
        await expect(page.locator('[data-position-symbol="MSFT"] td').nth(4)).toHaveText('Unknown');
        await expect(page.locator('[data-orders-portfolio] .metric-value')).toHaveText('$630.00');
        await expect(page.locator('[data-orders-portfolio] .metric-detail')).toContainText('cost $600.00 · Unknown');
        fail = true;
        await page.locator('[data-trading-positions]').evaluate(root => root.dispatchEvent(new Event('tradingflow:refresh')));
        await expect(page.locator('[data-position-status]')).toContainText('Displayed values may be stale');
        await expect(page.locator('[data-position-symbol="MSFT"]')).toBeVisible();
        await expect(review).toHaveAttribute('aria-disabled', 'true');
        await expect(review).not.toHaveAttribute('href', /.+/);
        fail = false;
        await page.locator('[data-trading-positions]').evaluate(root => root.dispatchEvent(new Event('tradingflow:refresh')));
        await expect(review).toHaveAttribute('href', /ticker=MSFT/);
        await expect(review).not.toHaveAttribute('aria-disabled', 'true');
    });

    test('a hung account request withdraws review after its deadline', async ({ page }) => {
        await page.clock.install();
        let hang = false;
        await page.route('**/api/v1/running-trades?source=all', route => hang ? new Promise(() => {}) : json(route, snapshot([holding()])));
        await page.goto('/Orders?env=paper');
        const review = page.locator('[data-position-review]');
        await expect(review).toHaveAttribute('href', /ticker=MSFT/);
        hang = true;
        const request = page.waitForRequest('**/api/v1/running-trades?source=all');
        await page.locator('[data-trading-positions]').evaluate(root => root.dispatchEvent(new Event('tradingflow:refresh')));
        await request;
        await page.clock.fastForward(8001);
        await expect(page.locator('[data-position-status]')).toContainText('timed out after 8 s');
        await expect(review).toHaveAttribute('aria-disabled', 'true');
        await expect(review).not.toHaveAttribute('href', /.+/);
        await expect(page.locator('[data-orders-portfolio]')).toContainText('Portfolio · stale');
    });

    test('a browser-history restore reloads before resuming account reviews', async ({ page }) => {
        let reads = 0;
        await page.route('**/api/v1/running-trades?source=all', route => { reads++; return json(route, snapshot([holding()])); });
        await page.goto('/Orders?env=paper');
        await expect(page.locator('[data-position-review]')).toHaveAttribute('href', /ticker=MSFT/);
        const before = reads;
        const navigation = page.waitForNavigation({ waitUntil: 'domcontentloaded' });
        await page.evaluate(() => {
            window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true }));
            window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }));
        });
        await navigation;
        await expect.poll(() => reads).toBeGreaterThan(before);
        await expect(page.locator('[data-position-status]')).toHaveAttribute('data-state', 'live');
        await expect(page.locator('[data-position-review]')).toHaveAttribute('href', /ticker=MSFT/);
    });

    for (const readAtUtc of [null, '2000-01-01T00:00:00Z']) test('a broker response with ' + (readAtUtc ? 'an old' : 'no') + ' read time cannot enable review', async ({ page }) => {
        let value = snapshot([holding()]);
        await page.route('**/api/v1/running-trades?source=all', route => json(route, value));
        await page.goto('/Orders?env=paper');
        await expect(page.locator('[data-position-review]')).toHaveAttribute('href', /ticker=MSFT/);
        value = { ...snapshot([holding()]), readAtUtc };
        await page.locator('[data-trading-positions]').evaluate(root => root.dispatchEvent(new Event('tradingflow:refresh')));
        await expect(page.locator('[data-position-status]')).toContainText('a read within 30 s is required');
        await expect(page.locator('[data-position-review]')).toHaveAttribute('aria-disabled', 'true');
        await expect(page.locator('[data-position-status]')).toHaveAttribute('data-state', 'stale');
    });

    test('position review encodes its symbol and retains the current environment and journal filters', async ({ page }) => {
        // The unusual broker symbol exercises URL encoding without navigating or submitting a ticket.
        await page.route('**/api/v1/running-trades?source=all', route => json(route, snapshot([holding('A&B')])));
        await page.goto('/Orders?env=paper&filter=working&sort=symbol&dir=desc&quantity=8&limitPrice=10');
        const review = page.locator('[data-position-review]');
        await expect(review).toHaveAttribute('href', /ticker=A%26B/);
        const url = new URL(await review.getAttribute('href'), 'http://fixture');
        expect(Object.fromEntries(url.searchParams)).toEqual({ env: 'paper', filter: 'working', sort: 'symbol', dir: 'desc', ticker: 'A&B', side: 'sell' });
    });

    test('short positions do not offer a sell review and a locked live screen does not read paper holdings', async ({ page }) => {
        let reads = 0;
        await page.route('**/api/v1/running-trades?source=all', route => { reads++; return json(route, snapshot([holding('SHORT', -3)])); });
        await page.goto('/Orders?env=paper');
        await expect(page.locator('[data-position-symbol="SHORT"]')).toContainText('Short position · close review unavailable');
        await expect(page.locator('[data-position-symbol="SHORT"] [data-position-review]')).toHaveCount(0);
        const before = reads;
        await page.goto('/Orders?env=live');
        await expect(page.locator('[data-trading-positions]')).toHaveCount(0);
        await expect(page.locator('#OrderWorkflowForm')).toHaveCount(0);
        expect(reads).toBe(before);
    });
});


for (const width of [1440, 412]) test('the complete admin ticket is reachable with the mouse wheel at ' + width, async ({page}, testInfo) => {
    const height = width === 1440 ? 900 : 915;
    await page.setViewportSize({width, height});
    await signInAsAdmin(page);
    await page.route('**/api/v1/orders/context', route => json(route, {accountId:'scroll-fixture', operatorOverrideAllowed:true}));
    await page.route('**/api/v1/strategies?*', route => json(route, {strategies:[{identity:{strategyId:'scroll-fixture',semanticVersion:'1.0.0',contentSha256:'a'.repeat(64)},name:'Ticket scroll fixture'}]}));
    await page.route('**/api/v1/orders?*', route => json(route, []));
    await page.route('**/api/v1/running-trades?source=all', route => json(route, snapshot([])));
    await page.goto('/Orders?env=paper&ticker=MSFT');
    await expect(page.locator('#OrderWorkflowForm')).toBeVisible();
    const review = page.locator('#OrderWorkflowForm button[type="submit"]');
    // Hit-testing catches ancestor clipping that a visibility assertion or locator auto-scroll can conceal.
    const isReachable = () => review.evaluate(button => {
        const rect = button.getBoundingClientRect();
        const x = rect.left + rect.width / 2, y = rect.top + rect.height / 2;
        const hit = document.elementFromPoint(x, y);
        return rect.top >= 0 && rect.bottom <= innerHeight && rect.left >= 0 && rect.right <= innerWidth && (hit === button || button.contains(hit));
    });
    expect(await isReachable()).toBe(false);
    const rail = await page.locator('.orders-rail').boundingBox();
    await page.mouse.move(rail.x + rail.width / 2, Math.max(80, Math.min(height - 80, rail.y + 60)));
    let reachable = false;
    for (let attempt = 0; attempt < 12 && !reachable; attempt++) {
        await page.mouse.wheel(0, 400);
        await page.waitForTimeout(80); // Let the real wheel scroll settle without invoking locator scrolling.
        reachable = await isReachable();
    }
    expect(reachable).toBe(true);
    expect(await page.evaluate(() => window.scrollY)).toBeGreaterThan(0);
    await expect(page.locator('.orders-rail')).toHaveCSS('max-height','none');
    await page.screenshot({path:testInfo.outputPath('trading-ticket-wheel-' + width + '.png')});
    await page.screenshot({path:testInfo.outputPath('trading-ticket-full-' + width + '.png'),fullPage:true});
});
