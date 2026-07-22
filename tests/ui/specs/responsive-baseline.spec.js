import { expect, test } from "@playwright/test";

const coreRoutes = [
  "/TradeDesk",
  "/RunningTrades",
  "/Paper",
  "/Wishlists",
  "/Backtests",
  "/Warmup",
  "/OrderTicket?ticker=MSFT&limitPrice=100",
  "/Audit/ui-release-audit",
  "/Job/00000000-0000-0000-0000-000000000000",
  "/PaperJob/00000000-0000-0000-0000-000000000000",
  "/OptimizationJob/00000000-0000-0000-0000-000000000000"
];

const viewports = [
  { width: 320, height: 800 },
  { width: 390, height: 844 },
  { width: 768, height: 1024 },
  { width: 1024, height: 768 },
  { width: 1440, height: 900 }
];

test.describe("approved UI baseline", () => {
  for (const route of coreRoutes) {
    test(`${route} renders without provider credentials`, async ({ page }) => {
      await page.goto(route);
      await expect(page.locator("main")).toBeVisible();
      await expect(page).toHaveTitle(/TradingFlow/);
    });
  }

  test("shell exposes skip navigation and the current destination", async ({ page }) => {
    await page.goto("/TradeDesk");
    await expect(page.locator('a[href="#main-content"]')).toHaveCount(1);
    await expect(page.locator('.primary-nav [aria-current="page"]')).toHaveText("Desk");
    await expect(page.locator('a[href="#"]')).toHaveCount(0);
  });

  for (const viewport of viewports) {
    test(`core routes fit the ${viewport.width}px viewport`, async ({ page }) => {
      await page.setViewportSize(viewport);

      for (const route of coreRoutes) {
        await page.goto(route);
        const dimensions = await page.evaluate(() => ({
          viewportWidth: document.documentElement.clientWidth,
          documentWidth: document.documentElement.scrollWidth
        }));

        expect(dimensions.documentWidth, `${route} widened the document`).toBeLessThanOrEqual(
          dimensions.viewportWidth
        );
      }
    });
  }

  test("mobile shell actions meet the 44px target", async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto("/TradeDesk");

    const undersized = await page.locator(
      '.primary-nav a, button:visible, a.button:visible, input:not([type="hidden"]):not([type="checkbox"]):visible, select:visible'
    ).evaluateAll(elements => elements
      .map(element => ({
        label: element.getAttribute("aria-label") || element.textContent?.trim() || element.getAttribute("name"),
        height: element.getBoundingClientRect().height
      }))
      .filter(item => item.height < 43.5));

    expect(undersized).toEqual([]);
  });

  test("internal page links resolve successfully", async ({ page, request }) => {
    const internalLinks = new Set();

    for (const route of coreRoutes) {
      await page.goto(route);
      const links = await page.evaluate(() => Array.from(document.querySelectorAll("a[href]"))
        .map(anchor => new URL(anchor.href, window.location.href))
        .filter(url => url.origin === window.location.origin && url.protocol.startsWith("http"))
        .map(url => `${url.pathname}${url.search}`));

      links.forEach(link => internalLinks.add(link));
    }

    for (const link of internalLinks) {
      const response = await request.get(link);
      expect(response.status(), `${link} returned ${response.status()}`).toBeLessThan(400);
    }
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

  test("core routes expose named controls and unique element ids", async ({ page }) => {
    for (const route of coreRoutes) {
      await page.goto(route);
      const violations = await page.evaluate(() => {
        const visible = element => {
          const style = getComputedStyle(element);
          const rect = element.getBoundingClientRect();
          return style.display !== "none" && style.visibility !== "hidden" && rect.width > 0 && rect.height > 0;
        };
        const ids = [...document.querySelectorAll("[id]")].map(element => element.id);
        const duplicateIds = [...new Set(ids.filter((id, index) => ids.indexOf(id) !== index))];
        const unnamedControls = [...document.querySelectorAll("input:not([type=hidden]), select, textarea, button")]
          .filter(visible)
          .filter(element => {
            const labelText = [...(element.labels ?? [])].map(label => label.textContent?.trim()).join("");
            return !(labelText || element.getAttribute("aria-label") || element.getAttribute("aria-labelledby") || element.textContent?.trim());
          })
          .map(element => `${element.tagName.toLowerCase()}#${element.id || "(no-id)"}`);
        const unnamedLinks = [...document.querySelectorAll("a[href]")]
          .filter(visible)
          .filter(element => !(element.textContent?.trim() || element.getAttribute("aria-label") || element.getAttribute("aria-labelledby")))
          .map(element => element.getAttribute("href"));
        return { duplicateIds, unnamedControls, unnamedLinks, h1Count: document.querySelectorAll("h1").length };
      });

      expect(violations.duplicateIds, `${route} has duplicate ids`).toEqual([]);
      expect(violations.unnamedControls, `${route} has unnamed controls`).toEqual([]);
      expect(violations.unnamedLinks, `${route} has unnamed links`).toEqual([]);
      expect(violations.h1Count, `${route} should expose one page heading`).toBe(1);
    }
  });
});
