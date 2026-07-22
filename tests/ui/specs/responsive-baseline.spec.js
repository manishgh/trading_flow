import { expect, test } from "@playwright/test";

const coreRoutes = [
  "/TradeDesk",
  "/RunningTrades",
  "/Paper",
  "/Wishlists",
  "/Backtests",
  "/Warmup"
];

test.describe("approved UI baseline", () => {
  for (const route of coreRoutes) {
    test(`${route} renders without provider credentials`, async ({ page }) => {
      await page.goto(route);
      await expect(page.locator("main")).toBeVisible();
      await expect(page).toHaveTitle(/TradingFlow/);
    });
  }

  test("legacy shell reproduces the documented 320px overflow before UI1", async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 800 });
    await page.goto("/TradeDesk");

    const dimensions = await page.evaluate(() => ({
      viewportWidth: document.documentElement.clientWidth,
      documentWidth: document.documentElement.scrollWidth
    }));

    expect(dimensions.documentWidth).toBeGreaterThan(dimensions.viewportWidth);
  });

  test("legacy shell reproduces missing navigation accessibility before UI1", async ({ page }) => {
    await page.goto("/TradeDesk");
    await expect(page.locator('a[href="#main-content"]')).toHaveCount(0);
    await expect(page.locator('nav [aria-current="page"]')).toHaveCount(0);
    await expect(page.locator('a[href="#"]')).not.toHaveCount(0);
  });

  test("read-only suite contains no order submissions", async ({ page }) => {
    const unsafeRequests = [];
    page.on("request", request => {
      if (request.method() !== "GET" && request.method() !== "HEAD") {
        unsafeRequests.push(`${request.method()} ${request.url()}`);
      }
    });

    for (const route of coreRoutes) {
      await page.goto(route);
    }

    expect(unsafeRequests).toEqual([]);
  });
});
