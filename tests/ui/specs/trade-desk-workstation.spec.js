import { expect, test } from "@playwright/test";

const testTicker = "UI2T";

async function openSeededDesk(page) {
  await page.goto("/Wishlists");
  if (await page.locator('input[name="TargetWishlistId"]').count() === 0) {
    await page.locator("#WishlistCombo").fill("UI2 Verification");
    await page.getByRole("button", { name: "Add New" }).click();
    await page.waitForLoadState("domcontentloaded");
  }

  const targetId = await page.locator('input[name="TargetWishlistId"]').first().getAttribute("value");
  expect(targetId, "The isolated UI database should contain a default wishlist").toBeTruthy();

  await page.locator("#TickerTop").fill(testTicker);
  await page.locator("#DisplayNameTop").fill("UI checkpoint symbol");
  await page.getByRole("button", { name: "Add Ticker" }).click();
  await page.goto(`/TradeDesk?id=${targetId}&ticker=${testTicker}`);
  await expect(page.locator(`[data-symbol="${testTicker}"]`).first()).toBeAttached();
  return targetId;
}

test.describe("UI2 web trading workstation", () => {
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

    await expect(page.locator(".operational-strip")).toBeVisible();
    await expect(page.locator(".operational-strip .operational-item")).toHaveCount(7);
    await expect(page.locator('form[action*="AddTicker"], input[name="Ticker"]:not([type="hidden"]), input[name="FinvizFilter"]')).toHaveCount(0);
    await expect(page.getByRole("link", { name: "Manage Wishlist" })).toHaveAttribute("href", /Wishlists/);

    await page.goto("/Wishlists");
    await expect(page.locator("#TickerTop")).toBeVisible();
    await expect(page.locator("#FinvizFilter")).toBeVisible();
  });

  test("wishlist news remains visible independently of explicit symbol selection", async ({ page }) => {
    const targetId = await openSeededDesk(page);
    await page.goto(`/TradeDesk?id=${targetId}`);

    await expect(page.locator("[data-selected-symbol]")).toHaveCount(0);
    await expect(page.getByRole("heading", { name: "Wishlist News" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Selected Symbol News" })).toHaveCount(0);

    await page.goto(`/TradeDesk?id=${targetId}&ticker=${testTicker}`);
    await expect(page.getByRole("heading", { name: "Selected Symbol News" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Wishlist News" })).toBeVisible();

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
    await expect(page.locator("[data-selected-news-title]")).toHaveText("Shared catalyst");
  });

  test("model intelligence is explicit, separate, and non-blocking when unconfigured", async ({ page }) => {
    const targetId = await openSeededDesk(page);
    await page.goto(`/TradeDesk?id=${targetId}`);

    await expect(page.getByRole("heading", { name: "Model Intelligence" })).toHaveCount(0);
    await expect(page.getByRole("heading", { name: "Wishlist News" })).toBeVisible();

    await page.goto(`/TradeDesk?id=${targetId}&ticker=${testTicker}&predictionMode=invalid-value`);
    await expect(page.getByRole("heading", { name: "TradingFlow decision" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Model Intelligence" })).toBeVisible();
    await expect(page.locator(".model-intelligence .signal-badge")).toHaveText("not_configured");
    await expect(page.locator(".model-intelligence")).toContainText("TradingFlow eligibility remains authoritative");
    await expect(page.locator('.segmented-links a.active')).toHaveText("unified");

    await page.getByRole("link", { name: "swing", exact: true }).click();
    await expect(page).toHaveURL(/predictionMode=swing/);
    await expect(page.locator('.segmented-links a.active')).toHaveText("swing");
    await expect(page.getByRole("heading", { name: "Wishlist News" })).toBeVisible();
  });

  test("desktop renders the dense table and mobile renders only compact symbol rows", async ({ page }) => {
    await page.setViewportSize({ width: 1024, height: 768 });
    await openSeededDesk(page);
    await expect(page.locator(".desk-desktop-table")).toBeVisible();
    await expect(page.locator(".desk-mobile-list")).toBeHidden();

    await page.setViewportSize({ width: 390, height: 844 });
    await expect(page.locator(".desk-desktop-table")).toBeHidden();
    await expect(page.locator(".desk-mobile-list")).toBeVisible();
    await expect(page.locator(`.mobile-market-row[data-symbol="${testTicker}"]`)).toHaveCount(1);
  });

  test("dense operator columns keep symbols and actions on one line", async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await openSeededDesk(page);

    const marketSymbol = page.locator(`.desk-desktop-table [data-symbol="${testTicker}"] .market-symbol`);
    const inspectLink = page.locator(`.desk-desktop-table [data-symbol="${testTicker}"]`).getByRole("link", { name: `Inspect ${testTicker}` });
    await expect(marketSymbol).toHaveCSS("white-space", "nowrap");
    await expect(inspectLink).toHaveCSS("white-space", "nowrap");
    await expect(inspectLink).toHaveText("Inspect");

    const wrapping = await page.locator(`.desk-desktop-table [data-symbol="${testTicker}"]`).evaluate(row => {
      const symbol = row.querySelector(".market-symbol");
      const action = row.querySelector(".action-cell .button");
      return {
        symbolLines: Math.round(symbol.getBoundingClientRect().height / parseFloat(getComputedStyle(symbol).lineHeight)),
        actionOverflows: action.scrollWidth > action.clientWidth + 1
      };
    });
    expect(wrapping).toEqual({ symbolLines: 1, actionOverflows: false });
  });

  test("operator URLs use strategy identities and never expose local config paths", async ({ page }) => {
    await openSeededDesk(page);
    const hrefs = await page.locator('a[href*="/TradeDesk"]').evaluateAll(nodes => nodes.map(node => node.getAttribute("href")));
    expect(hrefs.some(href => href?.includes("strategyId="))).toBeTruthy();
    expect(hrefs.some(href => href?.includes("strategyPath=") || href?.toLowerCase().includes("%5cproject"))).toBeFalsy();
  });

  test("operator links complete their intended navigation", async ({ page }) => {
    const targetId = await openSeededDesk(page);

    await page.getByRole("link", { name: "Manage Wishlist" }).click();
    await expect(page).toHaveURL(new RegExp(`/Wishlists\\?id=${targetId}`));
    await expect(page.getByRole("heading", { name: "Wishlist Management" })).toBeVisible();

    await page.goto(`/TradeDesk?id=${targetId}`);
    const inspectLink = page.locator(`.desk-desktop-table [data-symbol="${testTicker}"]`).getByRole("link", { name: `Inspect ${testTicker}` });
    await expect(inspectLink).toBeVisible();
    await inspectLink.click();
    await expect(page).toHaveURL(new RegExp(`ticker=${testTicker}`));
    await expect(page.locator(`[data-selected-symbol="${testTicker}"]`)).toBeVisible();

    await page.getByRole("link", { name: "Review strategy run" }).click();
    await expect(page).toHaveURL(/\/Paper/);
    await expect(page.getByRole("heading", { name: "Paper Trading Lab" })).toBeVisible();

    await page.goto(`/TradeDesk?id=${targetId}&ticker=${testTicker}`);
    await page.getByRole("link", { name: "Open positions" }).click();
    await expect(page).toHaveURL(/\/RunningTrades/);
    await expect(page.getByRole("heading", { name: "Running Trades" })).toBeVisible();
  });

  test("the detail rail cannot squeeze the market table at tablet width", async ({ page }) => {
    await page.setViewportSize({ width: 1024, height: 768 });
    await openSeededDesk(page);
    const layoutColumns = await page.locator(".trade-desk-layout").evaluate(node => getComputedStyle(node).gridTemplateColumns);
    expect(layoutColumns.trim().split(/\s+/)).toHaveLength(1);
  });

  test("watch rows select without submitting orders", async ({ page }) => {
    await openSeededDesk(page);
    const rows = page.locator("[data-symbol-row]");
    await expect(rows).not.toHaveCount(0);
    await expect(rows.locator("form, button[type=submit], [data-operator-direct-buy]")).toHaveCount(0);
  });

  test("protected buy requires a server-reviewed ticket before confirmation", async ({ page }) => {
    await openSeededDesk(page);
    const reviewLink = page.getByRole("link", { name: "Review protected buy" });
    await expect(reviewLink).toBeVisible();
    await expect(page.getByRole("button", { name: /confirm paper order/i })).toHaveCount(0);

    await reviewLink.click();
    await expect(page).toHaveURL(/OrderTicket/);
    await page.getByLabel("Limit price").fill("10.00");
    await page.getByLabel("Stop price").fill("9.50");
    await page.getByLabel("Take-profit price").fill("11.00");
    await page.getByRole("button", { name: "Review order" }).click();

    await expect(page.getByRole("heading", { name: /UI2T/ })).toBeVisible();
    await expect(page.getByText("Submission blocked")).toBeVisible();
    await expect(page.getByRole("button", { name: /confirm paper order/i })).toHaveCount(0);
    await expect(page.locator(".reviewed-order")).toContainText(/fresh two-sided SIP quote|credentials are not configured/i);
  });

  test("keyed quote patches preserve focused controls", async ({ page }) => {
    await page.setViewportSize({ width: 1024, height: 768 });
    await openSeededDesk(page);
    await page.waitForFunction(() => Boolean(window.TradingFlowDesk));
    const matchingRows = page.locator(`[data-symbol="${testTicker}"]`);
    expect(await matchingRows.count()).toBe(2);
    const view = matchingRows.nth(0).getByRole("link", { name: `Inspect ${testTicker}` });
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
    await expect(page.locator(`[data-symbol="${testTicker}"]`).first().locator('[data-quote-field="bid"]')).toHaveText("10.10");
  });

  test("stream failures expose text states independent of color", async ({ page }) => {
    await openSeededDesk(page);
    await page.waitForFunction(() => Boolean(window.TradingFlowDesk));
    await page.evaluate(() => window.TradingFlowDesk.setConnectionState(
      "disconnected",
      "Live server feed disconnected. Values remain visible but may be stale."));

    await expect(page.locator("#DeskQuoteConnection")).toHaveText("disconnected");
    await expect(page.locator("#DeskConnectionAnnouncement")).toHaveText(/disconnected.*stale/i);
  });
});
