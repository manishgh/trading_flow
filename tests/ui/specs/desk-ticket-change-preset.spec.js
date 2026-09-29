import { expect, test } from "@playwright/test";
import { signIn } from "./sign-in.js";

/**
 * The remaining desk work (docs/ui-desk-universe-plan.md): ticket prices with
 * no stand-in numbers, Change against the previous close, the phone universe
 * picker, the full Finviz signal catalogue, and saving a custom screen.
 *
 * Nothing here confirms an order. The isolated environment has no broker, no
 * Alpaca credentials and no live market state, so values that need them must
 * render empty or unknown - never as an invented figure.
 */

const ticker = "PLANX";

async function seedSymbol(page) {
  await page.goto("/TradeDesk");
  if (await page.locator(`.trade-desk-table [data-symbol="${ticker}"]`).count() > 0) return;
  await page.goto("/Wishlists");
  await page.locator("#CreateWishlistName").fill("Ticket Plan");
  await page.getByRole("button", { name: "Add new" }).click();
  await page.waitForLoadState("domcontentloaded");
  await page.locator("#TickerTop").fill(ticker);
  await page.getByRole("button", { name: "Add ticker" }).click();
  await page.waitForLoadState("domcontentloaded");
}

test.describe("ticket prices, change, phone picker and presets", () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page);
    await page.setViewportSize({ width: 1440, height: 900 });
  });

  test("the desk ticket shows no stand-in stop, target or limit and says why", async ({ page }) => {
    await seedSymbol(page);
    await page.goto(`/TradeDesk?ticker=${ticker}`);
    const ticket = page.locator(".desk-ticket");
    await expect(ticket.locator("#TicketStop")).toHaveValue("");
    await expect(ticket.locator("#TicketTarget")).toHaveValue("");
    await expect(ticket.locator("#TicketLimit")).toHaveValue("");
    await expect(ticket.locator("[data-ticket-stop-note]")).not.toBeEmpty();
    // No account equity here, so 1R has no budget to size against.
    await expect(ticket.getByRole("button", { name: "1R risk" })).toBeDisabled();
  });

  test("the Orders ticket follows a chosen strategy and shows no stand-in prices", async ({ page }) => {
    await page.goto("/Orders?ticker=MSFT");
    await expect(page.locator("#OrdersStrategy")).toHaveCount(1);
    await expect(page.locator("#TicketStop")).toHaveValue("");
    await expect(page.locator("#TicketTarget")).toHaveValue("");
    await expect(page.locator("[data-ticket-stop-note]")).not.toBeEmpty();
  });

  test("change reads against the previous close and follows live quotes", async ({ page }) => {
    await seedSymbol(page);
    await page.goto("/TradeDesk");
    const change = page.locator(`.trade-desk-table [data-symbol="${ticker}"] [data-column="price"] [data-change-field]`);
    // Without Alpaca there is no previous close, and the cell says so.
    await expect(change).toHaveText("—");
    await expect(change).toHaveAttribute("title", /No previous close/);

    // With a reference close, a live mid recomputes the change in place.
    await page.waitForFunction(() => Boolean(window.TradingFlowDesk));
    await change.evaluate(node => { node.dataset.prevClose = "100"; });
    await page.evaluate(symbol => window.TradingFlowDesk.applyQuotes([
      { ticker: symbol, midPrice: 110, midText: "110.00", bidText: "109.99", askText: "110.01", timestamp: new Date().toISOString() }
    ]), ticker);
    await expect(change).toHaveText("+10.00 (+10.00%)");
    await expect(change).toHaveClass(/up/);
  });

  test("the phone chooses wishlists and screeners at the list level only", async ({ page }) => {
    await seedSymbol(page);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto("/TradeDesk");
    await expect(page.locator(".desk-universe-switch")).toBeVisible();
    await expect(page.locator("#WishlistSelect")).toBeVisible();

    await page.locator(".desk-universe-switch").getByRole("link", { name: "Screener" }).click();
    await expect(page.locator("#ScreenerPick")).toBeVisible();

    await page.goto(`/TradeDesk?ticker=${ticker}`);
    await expect(page.locator("#WishlistSelect")).toBeHidden();
    await expect(page.locator(".desk-universe-switch")).toBeHidden();
  });

  test("the catalogue carries the newly confirmed Finviz signals", async ({ page }) => {
    await page.goto("/TradeDesk?scope=screener");
    const picker = page.locator("#ScreenerPick");
    for (const [code, label] of [
      ["ta_toplosers", "Top Losers"], ["ta_newlow", "New Low"], ["ta_mostvolatile", "Most Volatile"],
      ["ta_overbought", "Overbought"], ["ta_p_tlresistance", "TL Resistance"], ["ta_p_horizontal", "Horizontal S/R"],
      ["ta_p_wedgesupport", "Triangle Descending"], ["it_latestsales", "Recent Insider Selling"]
    ]) {
      await expect(picker.locator(`option[value="signal:${code}"]`)).toHaveText(label);
    }
  });

  test("a custom screen can be saved by name and is then offered as a saved screen", async ({ page }) => {
    const name = "UI saved channel screen";
    await page.goto("/TradeDesk?scope=screener&screenerQuery=" + encodeURIComponent("f=cap_smallover&s=ta_p_channelup"));
    await page.locator("#PresetName").fill(name);
    await page.getByRole("button", { name: "Save as preset" }).click();

    await expect(page).toHaveURL(/scope=screener/);
    await expect(page.locator(".status-banner")).toContainText(`Saved "${name}"`);
    const saved = page.locator('#ScreenerPick optgroup[label="My saved screens"] option', { hasText: name });
    await expect(saved).toHaveCount(1);
    await expect(page.locator("#ScreenerPick")).toHaveValue(name);
  });
});
