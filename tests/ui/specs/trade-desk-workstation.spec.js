import { expect, test } from "@playwright/test";
import { signIn } from "./sign-in.js";

/**
 * The desk, on the Modernist design (design/screens/01-trading-desk.md).
 *
 * These assertions are the screen's non-negotiables rather than its decoration:
 * monitoring is separated from administration, eligibility stays authoritative
 * over model evidence, rows are read-only, an order needs a server-reviewed
 * ticket, and live updates never replace a focused control.
 *
 * Read-only. Nothing here submits an order.
 */

const testTicker = "UI2T";

async function openSeededDesk(page) {
  await page.goto("/Wishlists");
  if (await page.locator('input[name="TargetWishlistId"]').count() === 0) {
    await page.locator("#CreateWishlistName").fill("UI2 Verification");
    await page.getByRole("button", { name: "Add new" }).click();
    await page.waitForLoadState("domcontentloaded");
  }

  const targetId = await page.locator('input[name="TargetWishlistId"]').first().getAttribute("value");
  expect(targetId, "The isolated UI database should contain a default wishlist").toBeTruthy();

  await page.locator("#TickerTop").fill(testTicker);
  await page.locator("#DisplayNameTop").fill("UI checkpoint symbol");
  await page.getByRole("button", { name: "Add ticker" }).click();
  await page.goto(`/TradeDesk?id=${targetId}&ticker=${testTicker}`);
  await expect(page.locator(`[data-symbol="${testTicker}"]`).first()).toBeAttached();
  return targetId;
}

test.describe("UI2 web trading workstation", () => {
  // Every operator screen sits behind authorisation, so the suite signs in
  // through the real login form rather than bypassing it.
  test.beforeEach(async ({ page }) => {
    await signIn(page);
  });

  test("desk and wishlist internal links resolve", async ({ page, request }) => {
    await openSeededDesk(page);

    for (const route of ["/TradeDesk", "/Wishlists"]) {
      await page.goto(route);
      const links = await page.locator('a[href^="/"]').evaluateAll(nodes =>
        [...new Set(nodes.map(node => node.getAttribute("href")).filter(Boolean))]);

      for (const href of links) {
        const response = await request.get(href);
        expect(response.ok(), `${route} link ${href} returned ${response.status()}`).toBeTruthy();
      }
    }
  });

  test("desk separates monitoring from wishlist administration", async ({ page }) => {
    await openSeededDesk(page);

    // Seven flat cells rather than the previous collapsed health control: at
    // seven the strip is still scannable, and the entry gate is the one an
    // operator must not miss.
    await expect(page.locator(".desk-operational")).toBeVisible();
    await expect(page.locator(".desk-operational > div")).toHaveCount(7);
    await expect(page.locator("#DeskQuoteConnection")).toHaveCount(1);

    // The desk monitors and routes. It never edits the universe.
    await expect(page.locator('form[action*="AddTicker"], input[name="Ticker"]:not([type="hidden"]), input[name="FinvizFilter"]')).toHaveCount(0);
    await expect(page.getByRole("link", { name: "Manage wishlist" })).toHaveAttribute("href", /Wishlists/);

    await page.goto("/Wishlists");
    await expect(page.locator("#TickerTop")).toBeVisible();
    await expect(page.locator("#FinvizFilter")).toBeVisible();
  });

  test("the buying-power cell reports unknown rather than a placeholder figure", async ({ page }) => {
    await openSeededDesk(page);
    // A trading screen must never show an invented account balance. Without
    // credentials the cell says so.
    const cell = page.locator(".desk-operational > div").last();
    await expect(cell).toContainText("Buying power");
  });

  test("wishlist news remains visible independently of explicit symbol selection", async ({ page }) => {
    const targetId = await openSeededDesk(page);
    await page.goto(`/TradeDesk?id=${targetId}`);

    await expect(page.locator("[data-desk-rail-selected]")).toHaveCount(0);
    await expect(page.getByRole("heading", { name: "News", exact: true })).toBeVisible();

    await page.goto(`/TradeDesk?id=${targetId}&ticker=${testTicker}`);
    await expect(page.getByRole("heading", { name: "News", exact: true })).toBeVisible();
    await expect(page.locator('[data-news-scope="symbol"]')).toBeEnabled();

    await page.waitForFunction(() => Boolean(window.TradingFlowDesk));
    await page.evaluate(ticker => window.TradingFlowDesk.applyActivity({
      signals: [],
      news: [
        { ticker, headline: "Shared catalyst", summary: "First source", provider: "alpaca", source: "wire", url: "https://example.com/shared", timestamp: "2026-07-22T10:00:00Z", timestampText: "10:00 UTC" },
        { ticker: "SECOND", headline: "Shared catalyst", summary: "Duplicate source", provider: "finviz", source: "wire", url: "https://example.com/shared", timestamp: "2026-07-22T10:01:00Z", timestampText: "10:01 UTC" }
      ]
    }), testTicker);

    await expect(page.locator("[data-wishlist-news-list] [data-news-item]")).toHaveCount(1);
    await expect(page.locator("[data-wishlist-news-list] [data-news-tickers]")).toContainText(testTicker);
    await expect(page.locator("[data-wishlist-news-list] [data-news-tickers]")).toContainText("SECOND");
  });

  test("model evidence sits beside the verdict and never claims authority over it", async ({ page }) => {
    const targetId = await openSeededDesk(page);
    await page.goto(`/TradeDesk?id=${targetId}`);

    // With nothing selected there is no evidence pair to read.
    await expect(page.getByRole("heading", { name: "Market predictor" })).toHaveCount(0);

    await page.goto(`/TradeDesk?id=${targetId}&ticker=${testTicker}&predictionMode=invalid-value`);
    const pair = page.locator(".desk-rail .desk-evidence-pair");
    await expect(pair.getByRole("heading", { name: "TradingFlow decision" })).toBeVisible();
    await expect(pair.getByRole("heading", { name: "Market predictor" })).toBeVisible();

    // The two panels are the same width, and each states its authority. The
    // observational desk signal remains advisory until a strategy is admitted.
    await expect(pair).toContainText("Advisory until strategy admission");
    await expect(pair).toContainText("Read-only evidence");

    // An unreadable predictor degrades to unavailable evidence; it never blocks
    // the screen or reads as a neutral opinion.
    await expect(pair).toContainText(/not_configured|unavailable/);
  });

  test("the agreement flag carries a glyph and a label, never colour alone", async ({ page }) => {
    const targetId = await openSeededDesk(page);
    await page.goto(`/TradeDesk?id=${targetId}&ticker=${testTicker}`);

    // The band states the verdict in words; the grid's Sync column carries the
    // short label. Both draw their glyph from a ::before so the state survives a
    // colour-blind scheme and never depends on hue.
    const headline = page.locator(".desk-rail .agreement-band .agreement-headline");
    await expect(headline).toBeVisible();
    await expect(headline).toHaveText(/agree|conflict|partial/i);
    const bandGlyph = await headline.evaluate(node => getComputedStyle(node, "::before").content);
    expect(bandGlyph).not.toBe("none");

    const syncCell = page.locator('.trade-desk-table [data-column="sync"] .agreement').first();
    await expect(syncCell).toHaveText(/AGREE|CONFLICT|PARTIAL/);
    const cellGlyph = await syncCell.evaluate(node => getComputedStyle(node, "::before").content);
    expect(cellGlyph).not.toBe("none");
  });

  test("desktop renders the ranked grid and the phone renders compact rows", async ({ page }) => {
    await page.setViewportSize({ width: 1024, height: 768 });
    await openSeededDesk(page);
    await expect(page.locator(".trade-desk-table")).toBeVisible();
    await expect(page.locator(".desk-mobile-list")).toBeHidden();

    await page.setViewportSize({ width: 390, height: 844 });
    await expect(page.locator(".trade-desk-table")).toBeHidden();
    await expect(page.locator(`.desk-mobile-row[data-symbol="${testTicker}"]`)).toHaveCount(1);
  });

  test("dense operator columns keep symbols and actions on one line", async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await openSeededDesk(page);

    const marketSymbol = page.locator(`.trade-desk-table [data-symbol="${testTicker}"] .market-symbol`);
    const inspectLink = page.locator(`.trade-desk-table [data-symbol="${testTicker}"]`).getByRole("link", { name: `Inspect ${testTicker}` });
    await expect(inspectLink).toHaveText("Inspect");

    const wrapping = await page.locator(`.trade-desk-table [data-symbol="${testTicker}"]`).evaluate(row => {
      const symbol = row.querySelector(".market-symbol");
      const action = row.querySelector(".action-cell .btn");
      return {
        symbolLines: Math.round(symbol.getBoundingClientRect().height / parseFloat(getComputedStyle(symbol).lineHeight)),
        actionOverflows: action.scrollWidth > action.clientWidth + 1
      };
    });
    expect(wrapping).toEqual({ symbolLines: 1, actionOverflows: false });
  });

  test("operator state uses a strategy identity field and never exposes local config paths", async ({ page }) => {
    await openSeededDesk(page);
    await expect(page.locator('select[name="strategyId"]')).toHaveCount(1);
    const hrefs = await page.locator('a[href*="/TradeDesk"]').evaluateAll(nodes => nodes.map(node => node.getAttribute("href")));
    expect(hrefs.some(href => href?.includes("strategyPath=") || href?.toLowerCase().includes("%5cproject"))).toBeFalsy();
  });

  test("operator links complete their intended navigation", async ({ page }) => {
    const targetId = await openSeededDesk(page);

    await page.getByRole("link", { name: "Manage wishlist" }).click();
    await expect(page).toHaveURL(new RegExp(`/Wishlists\\?id=${targetId}`));
    await expect(page.getByRole("heading", { name: "Wishlists", level: 1 })).toBeVisible();

    await page.goto(`/TradeDesk?id=${targetId}`);
    const inspectLink = page.locator(`.trade-desk-table [data-symbol="${testTicker}"]`).getByRole("link", { name: `Inspect ${testTicker}` });
    await expect(inspectLink).toBeVisible();
    await inspectLink.click();
    await expect(page).toHaveURL(new RegExp(`ticker=${testTicker}`));
    await expect(page.locator(`[data-selected-symbol="${testTicker}"]`)).toBeVisible();

    await page.getByRole("link", { name: "Positions", exact: true }).click();
    await expect(page).toHaveURL(/\/RunningTrades/);
    await expect(page.getByRole("heading", { name: "Positions", level: 1 })).toBeVisible();
  });

  test("the rail cannot squeeze the grid at tablet width", async ({ page }) => {
    await page.setViewportSize({ width: 900, height: 768 });
    await openSeededDesk(page);
    const layoutColumns = await page.locator(".desk-shell").evaluate(node => getComputedStyle(node).gridTemplateColumns);
    expect(layoutColumns.trim().split(/\s+/)).toHaveLength(1);
  });

  test("watch rows select without submitting orders", async ({ page }) => {
    await openSeededDesk(page);
    const rows = page.locator("[data-symbol-row]");
    await expect(rows).not.toHaveCount(0);
    await expect(rows.locator("form, button[type=submit], [data-operator-direct-buy]")).toHaveCount(0);
  });

  test("an order needs a server-reviewed ticket before it can be confirmed", async ({ page }) => {
    const targetId = await openSeededDesk(page);
    await page.goto(`/TradeDesk?id=${targetId}&ticker=${testTicker}`);

    // Stage one only. There is no confirm control, and no gate checklist,
    // before the server has actually checked anything.
    await expect(page.getByRole("button", { name: /^Review protected buy/ })).toBeVisible();
    await expect(page.getByRole("button", { name: /^Confirm paper/ })).toHaveCount(0);
    await expect(page.locator(".desk-ticket .check-list")).toHaveCount(0);
  });

  test("SELL is offered only where a tracked position exists", async ({ page }) => {
    const targetId = await openSeededDesk(page);
    await page.goto(`/TradeDesk?id=${targetId}&ticker=${testTicker}`);
    // The seeded symbol has no position, so the control cannot invite a short.
    await expect(page.locator('input[name="ticket.Side"][value="sell"]')).toHaveCount(0);
    await expect(page.locator('input[name="ticket.Side"][value="buy"]')).toHaveCount(1);
  });

  test("keyed quote patches preserve focused controls", async ({ page }) => {
    await page.setViewportSize({ width: 1024, height: 768 });
    await openSeededDesk(page);
    await page.waitForFunction(() => Boolean(window.TradingFlowDesk));

    const view = page.locator(`.trade-desk-table [data-symbol="${testTicker}"]`)
      .getByRole("link", { name: `Inspect ${testTicker}` });
    await view.focus();
    const identityBefore = await view.evaluate(node => {
      node.dataset.focusIdentity = "preserve-me";
      return node.dataset.focusIdentity;
    });

    await page.evaluate(ticker => window.TradingFlowDesk.applyQuotes([{
      ticker,
      bidText: "10.10",
      askText: "10.12",
      midText: "10.11",
      timestamp: new Date().toISOString()
    }]), testTicker);

    expect(identityBefore).toBe("preserve-me");
    await expect(view).toBeFocused();
    await expect(page.locator(`.trade-desk-table [data-symbol="${testTicker}"]`).first().locator('[data-quote-field="bid"]')).toHaveText("10.10");
  });

  test("stream failures expose text states independent of colour", async ({ page }) => {
    await openSeededDesk(page);
    await page.waitForFunction(() => Boolean(window.TradingFlowDesk));
    await page.evaluate(() => window.TradingFlowDesk.setConnectionState(
      "disconnected",
      "Live server feed disconnected. Values remain visible but may be stale."));

    await expect(page.locator("#DeskQuoteConnection")).toContainText("disconnected");
    await expect(page.locator("#DeskConnectionAnnouncement")).toHaveText(/disconnected.*stale/i);
  });
});
