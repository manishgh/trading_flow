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

  test("watch rows select without submitting orders", async ({ page }) => {
    await openSeededDesk(page);
    const rows = page.locator("[data-symbol-row]");
    await expect(rows).not.toHaveCount(0);
    await expect(rows.locator("form, button[type=submit], [data-operator-direct-buy]")).toHaveCount(0);
  });

  test("keyed quote patches preserve focused controls", async ({ page }) => {
    await page.setViewportSize({ width: 1024, height: 768 });
    await openSeededDesk(page);
    await page.waitForFunction(() => Boolean(window.TradingFlowDesk));
    const view = page.locator(`[data-symbol="${testTicker}"]`).first().getByRole("link", { name: "View" });
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
