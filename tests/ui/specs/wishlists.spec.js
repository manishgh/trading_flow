import { expect, test } from "@playwright/test";
import { signIn } from "./sign-in.js";

/** Creates a wishlist through the page itself and returns its id. */
async function createWishlist(page, name) {
  await page.goto("/Wishlists");
  await page.locator("#CreateWishlistName").fill(name);
  await page.getByRole("button", { name: "Add new", exact: true }).click();
  await expect(page.locator("#wl-title")).toHaveText(name);
  return new URL(page.url()).searchParams.get("id");
}

test.describe("manage wishlists", () => {
  test.beforeEach(async ({ page }) => { await signIn(page); });

  test("the add box adds one or many tickers and names what it skipped, beside the box", async ({ page }) => {
    const id = await createWishlist(page, `Add box ${Date.now()}`);
    await page.locator("#BulkTickers").fill("amd, tsla bad$ymbol");
    await page.getByRole("button", { name: "Add", exact: true }).click();
    const message = page.locator(".wl-add + .wl-message");
    await expect(message).toContainText("Added AMD, TSLA");
    await expect(message).toContainText("skipped “bad$ymbol” (not a ticker)");
    await expect(page.locator("[data-bulk-item]")).toHaveCount(2);

    await page.locator("#BulkTickers").fill("AMD");
    await page.getByRole("button", { name: "Add", exact: true }).click();
    await expect(page.locator(".wl-add + .wl-message")).toContainText("Nothing new added · 1 already in the list");
    await page.evaluate(async wishlistId => { await window.tradingFlowFetch(`/api/v1/wishlists/${wishlistId}`, { method: "DELETE" }); }, id);
  });

  test("notes save when the field is left, without reloading", async ({ page }) => {
    const id = await createWishlist(page, `Notes ${Date.now()}`);
    await page.locator("#BulkTickers").fill("OUST");
    await page.getByRole("button", { name: "Add", exact: true }).click();
    const notes = page.getByRole("textbox", { name: "OUST notes" });
    await notes.fill("Earnings 3 Nov");
    await notes.press("Enter");
    await expect(page.locator("[data-inline-status]")).toHaveText("Saved OUST.");
    await page.reload();
    await expect(page.getByRole("textbox", { name: "OUST notes" })).toHaveValue("Earnings 3 Nov");
    await page.evaluate(async wishlistId => { await window.tradingFlowFetch(`/api/v1/wishlists/${wishlistId}`, { method: "DELETE" }); }, id);
  });

  test("selection shows copy, move and remove; settings and delete sit behind Settings", async ({ page }) => {
    const id = await createWishlist(page, `Selection ${Date.now()}`);
    await page.locator("#BulkTickers").fill("AAPL MSFT");
    await page.getByRole("button", { name: "Add", exact: true }).click();
    await expect(page.locator("[data-bulk-bar]")).toBeHidden();
    await page.getByRole("checkbox", { name: "Select MSFT" }).check();
    const bar = page.locator("[data-bulk-bar]");
    await expect(bar).toContainText("1 selected · MSFT");
    for (const name of ["Copy to…", "Move to…", "Remove"]) await expect(bar.getByRole("button", { name, exact: true })).toBeVisible();

    await expect(page.getByRole("button", { name: "Delete wishlist" })).toBeHidden();
    await page.locator(".wl-settings > summary").click();
    await page.locator("#WishlistDescription").fill("Checked by the browser suite");
    await page.getByRole("button", { name: "Save settings" }).click();
    await expect(page.locator(".wl-head + .wl-message")).toContainText("Saved the settings of");
    await page.evaluate(async wishlistId => { await window.tradingFlowFetch(`/api/v1/wishlists/${wishlistId}`, { method: "DELETE" }); }, id);
  });

  test("removing through the dialog refreshes the list when the dialog closes", async ({ page }) => {
    const id = await createWishlist(page, `Remove ${Date.now()}`);
    await page.locator("#BulkTickers").fill("AAPL MSFT");
    await page.getByRole("button", { name: "Add", exact: true }).click();
    await page.getByRole("checkbox", { name: "Select AAPL" }).check();
    await page.locator("[data-bulk-bar]").getByRole("button", { name: "Remove", exact: true }).click();
    await expect(page.locator("#groupActionSource")).toBeHidden();
    await expect(page.locator("#groupActionApply")).toBeEnabled();
    await page.locator("#groupActionApply").click();
    await expect(page.locator("#groupActionStatus")).toContainText("Saved: 1 stock changed");
    await page.locator("#groupActionClose").click();
    await expect(page.locator("[data-bulk-item]")).toHaveCount(1);
    await expect(page.getByRole("checkbox", { name: "Select MSFT" })).toBeVisible();
    await page.evaluate(async wishlistId => { await window.tradingFlowFetch(`/api/v1/wishlists/${wishlistId}`, { method: "DELETE" }); }, id);
  });

  test("saved screens live on their own tab and report beside the form", async ({ page }) => {
    const id = await createWishlist(page, `Screens ${Date.now()}`);
    await page.getByRole("link", { name: "Saved Finviz screens" }).click();
    await expect(page.locator("#BulkTickers")).toHaveCount(0);
    const name = `Suite screen ${Date.now()}`;
    await page.locator("#PresetName").fill(name);
    await page.locator("#PresetQuery").fill("sh_price_o5,sh_avgvol_o500");
    await page.getByRole("button", { name: "Save screen" }).click();
    await expect(page).toHaveURL(/view=screens/);
    await expect(page.locator(".wl-preset-form + .wl-message")).toContainText(`Saved the screen ${name}`);
    const row = page.locator("tr", { hasText: name });
    page.once("dialog", dialog => dialog.accept());
    await row.getByRole("button", { name: "Delete" }).click();
    await expect(page.locator("tr", { hasText: name })).toHaveCount(0);
    await page.evaluate(async wishlistId => { await window.tradingFlowFetch(`/api/v1/wishlists/${wishlistId}`, { method: "DELETE" }); }, id);
  });
});
