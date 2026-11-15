import { expect, test } from "@playwright/test";
import { signIn } from "./sign-in.js";
import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const statusPath = "/api/finviz-screen-capture/status";
const disabledMessage = "Scheduled capture is disabled in UI fixtures.";

test("Operations distinguishes archive-root presence from verified writes", async ({ page }) => {
  const runId = process.env.TRADINGFLOW_UI_OWNED_RUN_ID;
  expect(runId).toMatch(/^[0-9a-f-]{36}$/i);
  const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../..");
  const fixture = path.join(repository, ".tmp", "ui-tests", "test-runs", `run-${runId}`, "raw", "fixture", "2001-01-01");
  await fs.mkdir(fixture, { recursive: true });
  // These deliberately incomplete synthetic bytes cannot establish a successful provider receipt.
  await fs.writeFile(path.join(fixture, "unverified.manifest.json"), '{"synthetic":true}');
  await signIn(page);
  await page.goto("/Operations");
  const row = page.getByRole("row").filter({ hasText: "IRawArchiveWriter" });
  await expect(row.getByText("Root found", { exact: true })).toBeVisible();
  await expect(row).toContainText("this check does not verify successful writes");
  await expect(row).toContainText("Historical manifest totals and last-write time are not queried on this page");
  await expect(row).toContainText("automatic deletion is disabled");
  await expect(row).not.toContainText("newest write");
  await expect(page.getByRole("region", { name: "Scheduled Finviz capture", exact: true })).toContainText(disabledMessage);
});

test("Finviz capture status requires sign-in", async ({ request }) => {
  const response = await request.get(statusPath, { maxRedirects: 0 });
  expect(response.status()).toBe(401);
});

test("the signed-in Finviz status endpoint accepts reads only", async ({ page }) => {
  await signIn(page);
  const response = await page.request.get(statusPath);
  expect(response.ok()).toBe(true);
  expect(await response.json()).toMatchObject({
    enabled: false,
    message: disabledMessage,
    nextSlotAtUtc: null
  });

  // Use the app's CSRF-aware fetch so the method check reaches routing.
  const writeStatus = await page.evaluate(async path =>
    (await window.tradingFlowFetch(path, { method: "POST" })).status, statusPath);
  expect(writeStatus).toBe(405);
});

for (const viewport of [{ width: 390, height: 844 }, { width: 1280, height: 900 }]) {
  test(`scheduled capture reports missing evidence at ${viewport.width}px`, async ({ page }) => {
    await page.setViewportSize(viewport);
    await signIn(page);

    // These are synthetic records in the test host's own database. Saving a query does not request an export.
    const group = `Capture status group ${viewport.width}`;
    const screen = `Capture status screen ${viewport.width}`;
    await page.goto("/Wishlists");
    await page.getByRole("textbox", { name: "New stock group", exact: true }).fill(group);
    await page.getByRole("button", { name: "Add new", exact: true }).click();
    await expect(page.getByRole("heading", { name: group, exact: true })).toBeVisible();
    await page.getByRole("link", { name: "Saved Finviz screens", exact: true }).click();
    await page.locator("#PresetName").fill(screen);
    await page.locator("#PresetQuery").fill("sh_price_o5,sh_avgvol_o500");
    await page.getByRole("button", { name: "Save screen", exact: true }).click();
    await expect(page.getByText(`Saved the screen ${screen}.`, { exact: true })).toBeVisible();

    const snapshot = await page.request.get(statusPath);
    expect(snapshot.ok()).toBe(true);
    expect((await snapshot.json()).screens).toContainEqual(expect.objectContaining({
      name: screen,
      state: "Disabled",
      lastCaptureAtUtc: null,
      evidenceMembershipVerified: null
    }));

    const mutations = [];
    page.on("request", request => {
      if (!["GET", "HEAD"].includes(request.method())) {
        mutations.push(`${request.method()} ${request.url()}`);
      }
    });

    const savedScreens = page.url();
    for (const route of [savedScreens, "/Operations"]) {
      const response = await page.goto(route);
      expect(response?.ok()).toBe(true);
      const capture = page.getByRole("region", { name: "Scheduled Finviz capture", exact: true });
      await expect(capture.getByText(disabledMessage, { exact: true })).toBeVisible();
      const row = capture.getByRole("row", { name: new RegExp(`^${screen} `) });
      await expect(row.getByText("Disabled", { exact: true })).toBeVisible();
      await expect(row.getByText("No capture recorded", { exact: true })).toBeVisible();
      await expect(capture.locator("time")).toHaveCount(0);
      await expect(capture.locator("form, button")).toHaveCount(0);

      const dimensions = await page.evaluate(() => ({
        viewport: document.documentElement.clientWidth,
        page: document.documentElement.scrollWidth
      }));
      expect(dimensions.page, `${route} widened the document`).toBeLessThanOrEqual(dimensions.viewport);
    }

    expect(mutations).toEqual([]);
  });
}
