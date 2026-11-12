import { expect, test } from "@playwright/test";
import { signIn } from "./sign-in.js";

// Exercise the shared ticket in an isolated host; no order is confirmed by this screen suite.
test.describe("orders screen", () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page);
    await page.route("**/api/v1/orders/context", route => route.fulfill({json:{accountId:"fixture",operatorOverrideAllowed:true}}));
    await page.setViewportSize({ width: 1440, height: 900 });
  });

  // Opening a ticket leaves the journal and filter selection in the same document.
  test("the new-order box opens a compact reviewed popup over the journal", async ({ page }) => {
    await page.goto("/Orders?filter=working");
    await expect(page.locator("#OrderPopup")).toBeHidden();
    await expect(page.locator(".orders-rail")).toHaveCount(0);
    await page.locator("#OrdersNewTicker").fill("msft");
    await page.getByRole("button", { name: "Open ticket" }).click();
    await expect(page).toHaveURL(/filter=working$/);
    await expect(page.getByRole("dialog",{name:"Buy MSFT"})).toBeVisible();
    const ticket=page.frameLocator('#OrderPopupFrame');
    await expect(ticket.locator('[name=symbol]')).toHaveValue('MSFT');
    await expect(ticket.getByRole("button", { name: "Review order", exact: true })).toBeVisible();
    await expect(ticket.getByRole("button", { name: /^Confirm paper/ })).toBeHidden();
    await expect(page.locator(".orders-controls").getByRole("link", { name: "Working" })).toHaveAttribute("aria-current", "page");
  });

  // Existing deep links keep their query and initialize the same API workflow once opened.
  test("the shared API ticket keeps the journal view", async ({ page }) => {
    await page.goto("/Orders?filter=working&ticker=MSFT");
    const ticket=page.frameLocator('#OrderPopupFrame');
    await expect(ticket.locator('#OrderWorkflowForm [name=symbol]')).toHaveValue("MSFT");
    await expect(ticket.locator('#OrderWorkflowForm')).not.toHaveAttribute("action", /PreviewTicket/);
    await expect(ticket.locator('[name=strategy] option')).not.toHaveCount(0);
    await expect(ticket.locator("[data-order-help]")).toContainText("Server review");
    await expect(page.locator('#OrderPopup')).toBeVisible();
    await expect(page.locator(".orders-controls").getByRole("link", { name: "Working" })).toHaveAttribute("aria-current", "page");
  });

  // Server symbol normalization continues to reject invalid direct links before a ticket loads.
  test("an invalid symbol is refused without opening a ticket", async ({ page }) => {
    await page.goto("/Orders?ticker=%3Cscript%3E");
    await expect(page.locator(".status-banner.error")).toContainText("is not a symbol");
    await expect(page.locator("#OrderPopup")).toBeHidden();
    await expect(page.locator("#OrderPopupFrame")).not.toHaveAttribute('src',/OrderTicket/);
  });

  // Account facts still describe the session in which a ticket will be reviewed.
  test("the account strip carries session, gate, portfolio and buying power", async ({ page }) => {
    await page.goto("/Orders");
    const account = page.locator(".orders-account");
    await expect(account.locator(":scope > div")).toHaveCount(4);
    await expect(account).toContainText("Market session");
    await expect(account).toContainText("Entry gate");
    await expect(page.locator("[data-orders-portfolio]")).toContainText(/Portfolio · (\d+ open|unavailable)/);
    await expect(account).toContainText("Buying power");
    await expect(page.locator("[data-orders-page] .metric-value")).toHaveCount(5);
  });

  // Locked environments do not render the popup or the new-order launcher.
  test("a locked environment opens no ticket and offers no new order", async ({ page }) => {
    await page.goto("/Orders?env=live&ticker=MSFT");
    await expect(page.locator("#OrdersNewTicker")).toHaveCount(0);
    await expect(page.locator("#OrderPopup")).toHaveCount(0);
    await expect(page.locator('.environment-lock')).toBeVisible();
  });
});
