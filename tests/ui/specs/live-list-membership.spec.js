import { expect, test } from "@playwright/test";
import path from "node:path";

const webRoot = path.resolve(process.cwd(), "../../src/TradingFlow.Web/wwwroot/js");

const order = (id, symbol) => ({
  runId: "10000000-0000-0000-0000-000000000001",
  clientOrderId: id,
  brokerOrderId: `broker-${id}`,
  strategyId: "swing-vcp-v4",
  symbol,
  side: "buy",
  orderType: "limit",
  timeInForce: "day",
  requestedQuantity: 10,
  limitPrice: 25.5,
  stopPrice: null,
  state: "Acked",
  createdAtUtc: "2026-09-06T10:00:00Z",
  updatedAtUtc: "2026-09-06T10:01:00Z",
  filledQuantity: 0,
  fillPrice: null,
  eventSource: "test"
});

const trade = (ticker, jobId, unrealizedPl) => ({
  source: "wishlist",
  ticker,
  quantity: 12,
  entryPrice: 20,
  currentPrice: 21,
  unrealizedPl,
  unrealizedPlPct: unrealizedPl / 2.4,
  status: "Open",
  reference: `paper-${ticker}`,
  jobId,
  sessionId: null,
  closeKind: "paper_job",
  strategyName: "Swing VCP V4",
  stopLossPrice: 19,
  takeProfitPrice: 23,
  exitReason: null,
  updatedAtUtc: "2026-09-06T10:01:00Z",
  protectionSummary: "Broker stop active"
});

const ordersDocument = `<!doctype html><html><body>
  <section data-orders-page data-filter="all">
    <div><strong class="metric-value">0</strong></div>
    <div><strong class="metric-value">0</strong></div>
    <div><strong class="metric-value">0</strong></div>
    <div><strong class="metric-value">0</strong></div>
    <div><strong class="metric-value">0</strong></div>
  </section>
  <div class="orders-body">
    <p id="OrdersConnection" data-state="live"></p>
    <p class="orders-footnote">Order journal</p>
  </div>
</body></html>`;

const positionsDocument = `<!doctype html><html><body>
  <section data-running-trades data-source="all">
    <strong class="metric-value" data-total-pl>$0.00</strong>
    <strong class="metric-value" data-trade-count>0</strong>
  </section>
  <div class="positions-body">
    <p class="positions-footnote">Position book</p>
    <p id="TradesConnection" data-state="live"></p>
  </div>
</body></html>`;

async function preserveFocusAndScroll(page, refresh) {
  const region = page.locator(".table-scroll");
  await region.focus();
  await page.evaluate(() => {
    document.body.style.minHeight = "3000px";
    window.scrollTo(0, 400);
    window.__membershipDocument = "same-document";
  });
  const before = await page.evaluate(() => window.scrollY);

  await refresh();

  await expect(region).toBeFocused();
  expect(await page.evaluate(() => window.scrollY)).toBe(before);
  expect(await page.evaluate(() => window.__membershipDocument)).toBe("same-document");
}

test.describe("incremental live-list membership", () => {
  test("orders add and remove rows without a document reload", async ({ page }) => {
    let payload = [order("order-a", "AAPL")];
    await page.route("http://tradingflow.test/**", route => {
      const url = new URL(route.request().url());
      if (url.pathname === "/api/v1/orders") return route.fulfill({ json: payload });
      return route.fulfill({ contentType: "text/html", body: ordersDocument });
    });
    await page.goto("http://tradingflow.test/Orders");
    await page.addScriptTag({ path: path.join(webRoot, "orders.js") });

    await expect(page.locator('[data-client-order-id="order-a"]')).toContainText("AAPL");
    payload = [order("order-b", "MSFT")];

    await preserveFocusAndScroll(page, async () => {
      await page.locator("[data-orders-page]").evaluate(element =>
        element.dispatchEvent(new Event("tradingflow:refresh")));
      await expect(page.locator('[data-client-order-id="order-b"]')).toContainText("MSFT");
    });

    await expect(page.locator('[data-client-order-id="order-a"]')).toHaveCount(0);
  });

  test("positions add and remove rows without a document reload", async ({ page }) => {
    let trades = [trade("AAPL", "20000000-0000-0000-0000-000000000001", 12)];
    await page.route("http://tradingflow.test/**", route => {
      const url = new URL(route.request().url());
      if (url.pathname === "/api/v1/running-trades") {
        return route.fulfill({
          json: {
            trades,
            totalUnrealizedPl: trades.reduce((sum, item) => sum + item.unrealizedPl, 0),
            count: trades.length
          }
        });
      }
      return route.fulfill({ contentType: "text/html", body: positionsDocument });
    });
    await page.goto("http://tradingflow.test/RunningTrades");
    await page.addScriptTag({ path: path.join(webRoot, "running-trades.js") });

    await expect(page.locator('[data-trade-key$=":AAPL"]')).toContainText("AAPL");
    trades = [trade("MSFT", "20000000-0000-0000-0000-000000000002", -8)];

    await preserveFocusAndScroll(page, async () => {
      await page.locator("[data-running-trades]").evaluate(element =>
        element.dispatchEvent(new Event("tradingflow:refresh")));
      await expect(page.locator('[data-trade-key$=":MSFT"]')).toContainText("MSFT");
    });

    await expect(page.locator('[data-trade-key$=":AAPL"]')).toHaveCount(0);
  });
});
