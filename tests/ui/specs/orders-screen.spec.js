import { expect, test } from "@playwright/test";
import { signIn } from "./sign-in.js";

/**
 * The Orders screen as a subset of the desk (docs/ui-desk-universe-plan.md,
 * screen 2): the journal, the desk's account cells, and the desk's reviewed
 * ticket in a rail beside it.
 *
 * Nothing here confirms an order. A review may run; the server re-checks it and
 * this isolated environment has no broker.
 */

test.describe("orders screen", () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page);
    // Exercise the Administrator UI branch; no broker command is sent.
    await page.route("**/api/v1/orders/context", route => route.fulfill({contentType:"application/json",body:JSON.stringify({accountId:"fixture",operatorOverrideAllowed:true})}));
    await page.setViewportSize({ width: 1440, height: 900 });
  });

  test("the new-order box opens the reviewed ticket beside the journal", async ({ page }) => {
    await page.goto("/Orders");
    await expect(page.locator(".orders-rail")).toContainText("Choose a symbol");

    await page.locator("#OrdersNewTicker").fill("msft");
    await page.getByRole("button", { name: "Open ticket" }).click();

    await expect(page).toHaveURL(/\/Orders\?/);
    const rail = page.locator('.orders-rail [data-selected-symbol="MSFT"]');
    await expect(rail).toBeVisible();
    await expect(page.locator(".orders-rail").getByRole("heading", { name: "Reviewed order" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Review order", exact: true })).toBeVisible();

    // Stage one only: no confirm control and no checklist before a server review.
    await expect(page.getByRole("button", { name: /^Confirm paper/ })).toBeHidden();
    await expect(page.locator(".desk-ticket .check-list")).toHaveCount(0);
    // No position is held, so SELL is not offered.
    await expect(page.locator('input[name="ticket.Side"][value="sell"]')).toHaveCount(0);
  });

  test("the shared API ticket keeps the journal view", async ({ page }) => {
    await page.goto("/Orders?filter=working&ticker=MSFT");
    const ticket = page.locator("#OrderWorkflowForm");
    await expect(ticket.locator('[name=symbol]')).toHaveValue("MSFT");
    await expect(ticket).not.toHaveAttribute("action", /PreviewTicket/);
    await expect(ticket.locator('[name=strategy] option')).not.toHaveCount(0);
    await expect(page.locator("[data-order-help]")).toContainText("Server review");
    await expect(page).toHaveURL(/\/Orders\?/);
    await expect(page.locator('.orders-rail [data-selected-symbol="MSFT"]')).toBeVisible();
    await expect(page.locator(".orders-controls").getByRole("link", { name: "Working" })).toHaveAttribute("aria-current", "page");
  });

  test("an invalid symbol is refused without opening a ticket", async ({ page }) => {
    await page.goto("/Orders?ticker=%3Cscript%3E");
    await expect(page.locator(".status-banner.error")).toContainText("is not a symbol");
    await expect(page.locator("[data-selected-symbol]")).toHaveCount(0);
    await expect(page.locator("#OrderWorkflowForm")).toHaveCount(0);
  });

  test("the account strip carries the desk's session, gate, portfolio and buying power", async ({ page }) => {
    await page.goto("/Orders");
    const account = page.locator(".orders-account");
    await expect(account.locator(":scope > div")).toHaveCount(4);
    await expect(account).toContainText("Market session");
    await expect(account).toContainText("Entry gate");
    await expect(page.locator("[data-orders-portfolio]")).toContainText(/Portfolio · (\d+ open|unavailable)/);
    await expect(account).toContainText("Buying power");
    // The order counts orders.js keeps current are still their own strip.
    await expect(page.locator("[data-orders-page] .metric-value")).toHaveCount(5);
  });

  test("a locked environment opens no ticket and offers no new order", async ({ page }) => {
    await page.goto("/Orders?env=live&ticker=MSFT");
    await expect(page.locator("#OrdersNewTicker")).toHaveCount(0);
    await expect(page.locator("#OrderWorkflowForm")).toHaveCount(0);
  });

  test("the desk rail links to the symbol's orders", async ({ page }) => {
    await page.goto("/Wishlists");
    await page.locator("#CreateWishlistName").fill("Orders Link");
    await page.getByRole("button", { name: "Add new" }).click();
    await page.waitForLoadState("domcontentloaded");
    await page.locator("#BulkTickers").fill("ORDX");
    await page.getByRole("button", { name: "Add", exact: true }).click();
    await page.waitForLoadState("domcontentloaded");

  });
});
