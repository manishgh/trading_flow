import { expect, test } from "@playwright/test";
import { signIn } from "./sign-in.js";

/**
 * The desk universe (docs/ui-desk-universe-plan.md): every wishlist by default,
 * one wishlist on request, or a screener's hits; an account-wide portfolio cell;
 * preparation on the desk; and live streams keyed to the rendered symbols.
 *
 * Read-only. Nothing here submits an order or queues preparation.
 */

const sharedTicker = "UNIV";
const firstOnly = "UNIA";

async function seedList(page, name, tickers) {
  await page.goto("/Wishlists");
  await page.locator("#CreateWishlistName").fill(name);
  await page.getByRole("button", { name: "Add new" }).click();
  await page.waitForLoadState("domcontentloaded");
  const id = await page.locator('input[name="TargetWishlistId"]').first().getAttribute("value");
  for (const ticker of tickers) {
    await page.locator("#TickerTop").fill(ticker);
    await page.getByRole("button", { name: "Add ticker" }).click();
    await page.waitForLoadState("domcontentloaded");
  }
  return id;
}

async function seedTwoLists(page) {
  await page.goto("/TradeDesk?view=market&search=UNI");
  const seeded = await page.locator(`.trade-desk-table [data-symbol="${sharedTicker}"]`).count();
  if (seeded > 0) {
    return;
  }
  await seedList(page, "Universe One", [sharedTicker, firstOnly]);
  await seedList(page, "Universe Two", [sharedTicker]);
}

test.describe("desk universe", () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page);
    await page.setViewportSize({ width: 1440, height: 900 });
  });

  test("the desk defaults to every wishlist, one row per symbol, naming its lists", async ({ page }) => {
    await seedTwoLists(page);
    await page.goto("/TradeDesk?view=market&search=UNI");

    await expect(page.locator("#WishlistSelect")).toHaveValue("");
    await expect(page.locator("#WishlistSelect option").first()).toContainText("All wishlists");
    await expect(page.locator(`.trade-desk-table [data-symbol="${sharedTicker}"]`)).toHaveCount(1);
    const lists = page.locator(`.trade-desk-table [data-symbol="${sharedTicker}"] .market-lists`);
    await expect(lists).toContainText("Universe One");
    await expect(lists).toContainText("Universe Two");
    await expect(page.locator(`.trade-desk-table [data-symbol="${firstOnly}"]`)).toHaveCount(1);
  });

  test("choosing one wishlist narrows the rows to it", async ({ page }) => {
    await seedTwoLists(page);
    await page.goto("/TradeDesk?view=market");
    const two = await page.locator("#WishlistSelect option", { hasText: "Universe Two" }).first().getAttribute("value");

    await page.goto(`/TradeDesk?id=${two}`);
    await expect(page.locator(`.trade-desk-table [data-symbol="${sharedTicker}"]`)).toHaveCount(1);
    await expect(page.locator(`.trade-desk-table [data-symbol="${firstOnly}"]`)).toHaveCount(0);
  });

  test("the screener scope offers saved screens and catalogued Finviz signals", async ({ page }) => {
    await page.goto("/TradeDesk?view=market");
    await page.getByRole("link", { name: "Screener", exact: true }).click();
    await expect(page).toHaveURL(/scope=screener/);

    const picker = page.locator("#ScreenerPick");
    await expect(picker.locator('optgroup[label="Finviz · Swing patterns · bullish"]')).toHaveCount(1);
    await expect(picker.locator('option[value="signal:ta_p_channelup"]')).toHaveText("Channel Up");
    await expect(picker.locator('option[value="signal:ta_p_doublebottom"]')).toHaveText("Double Bottom");

    // Without a Finviz key the read fails closed with its reason; the desk still renders.
    await page.goto("/TradeDesk?scope=screener&screenerQuery=signal:ta_p_channelup");
    await expect(picker).toHaveValue("signal:ta_p_channelup");
    await expect(page.locator(".desk-screener-band")).toContainText("Finviz · Channel Up");
  });

  test("preparation sits on the desk and an unreachable service never blocks it", async ({ page }) => {
    await page.goto("/TradeDesk?view=market");
    const band = page.locator("[data-desk-preparation]");
    await expect(band.getByRole("heading", { name: "Market-data warmup" })).toBeVisible();
    await expect(band).toContainText("Session");
    await expect(band.getByRole("link", { name: "Warmup details" })).toHaveAttribute("href", /\/Warmup/);
    await expect(page.locator(".trade-desk-table")).toBeVisible();
  });

  test("the portfolio cell totals every position, independent of the view", async ({ page }) => {
    await page.goto("/TradeDesk?view=market");
    const cell = page.locator("[data-desk-portfolio]");
    await expect(cell).toContainText(/Portfolio · (\d+ open|unavailable)/);
    await expect(cell).toContainText("cost");
  });

  test("shared tickets keep the desk view and provide an explicit discovery source", async ({ page }) => {
    await seedTwoLists(page);
    await page.route("**/api/v1/orders/context", route => route.fulfill({contentType:"application/json",body:JSON.stringify({accountId:"fixture",operatorOverrideAllowed:true})}));
    await page.goto(`/TradeDesk?ticker=${sharedTicker}`);
    await expect(page.locator('#OrderWorkflowForm [name=symbol]')).toHaveValue(sharedTicker);
    await expect(page.locator('#OrderWorkflowForm [name=wishlist] option')).not.toHaveCount(1);
    await expect(page).toHaveURL(new RegExp(`ticker=${sharedTicker}`));

    const two = await page.locator("#WishlistSelect option", { hasText: "Universe Two" }).first().getAttribute("value");
    await page.goto(`/TradeDesk?id=${two}&ticker=${sharedTicker}`);
    await expect(page.locator('#OrderWorkflowForm [name=symbol]')).toHaveValue(sharedTicker);
    await expect(page).toHaveURL(new RegExp(`id=${two}`));
  });

  test("live streams follow the rendered symbols and refuse an empty set", async ({ page }) => {
    await seedTwoLists(page);
    await page.goto("/TradeDesk?view=market&search=UNI");
    const tickers = await page.locator("[data-desk-root]").getAttribute("data-stream-tickers");
    expect(tickers.split(",")).toEqual(expect.arrayContaining([sharedTicker, firstOnly]));
    const rendered = await page.locator('.trade-desk-table [data-symbol-row]').evaluateAll(rows => rows.map(row => row.dataset.symbol));
    expect(tickers.split(',').sort()).toEqual([...new Set(rendered)].sort());

    const refused = await page.request.get("/api/v1/desk/quotes/stream?tickers=%3Cscript%3E", { failOnStatusCode: false });
    expect(refused.status()).toBe(400);
  });
});
