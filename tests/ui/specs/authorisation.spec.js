import { expect, test } from "@playwright/test";

/**
 * What is reachable without signing in.
 *
 * This suite deliberately does NOT sign in. Authorisation is fail-closed by a
 * fallback policy, so the risk it guards against is an endpoint added later
 * that nobody remembered to protect. Asserting the open list explicitly means
 * widening it has to be a deliberate edit to this file.
 */

// Everything an operator can reach must require a session. The order and
// paper-run endpoints matter most: they can move money in the paper account.
const protectedPages = [
  "/TradeDesk",
  "/RunningTrades",
  "/Orders",
  "/News",
  "/Backtests",
  "/Operations",
  "/Paper",
  "/Wishlists",
  "/Warmup",
  "/Users",
  "/OrderTicket?ticker=MSFT&limitPrice=100"
];

const protectedApis = [
  "/api/mobile/catalog",
  "/api/mobile/wishlists",
  "/api/mobile/paper/jobs",
  "/api/profiler/alpaca",
  "/api/strategies/evaluate"
];

// The deliberate exceptions, and the reason each one is open.
const publicRoutes = [
  ["/Login", "an operator has to be able to reach the sign-in form"],
  ["/Earnings", "an advisory monitor that cannot route an order"],
  ["/api/earnings/today-next-business-day", "backs the anonymous Earnings screen"],
  ["/health", "a readiness probe cannot hold a session"],
  ["/health/trading-readiness", "a readiness probe cannot hold a session"]
];

test.describe("authorisation", () => {
  test("every operator page requires a session", async ({ page }) => {
    for (const route of protectedPages) {
      await page.goto(route);
      // Redirected to the sign-in form rather than rendering the screen.
      await expect(page, `${route} rendered without a session`).toHaveURL(/\/Login/);
    }
  });

  test("the mobile and operations APIs require a session", async ({ request }) => {
    for (const route of protectedApis) {
      const response = await request.get(route, { maxRedirects: 0 });
      // 401, or a 302 to the login form for a cookie scheme. Never 200.
      expect(response.status(), `${route} answered without a session`).not.toBe(200);
    }
  });

  test("order submission cannot be reached without a session", async ({ request }) => {
    // The endpoint that actually places a paper order. It was reachable
    // anonymously before the fallback policy went in.
    const response = await request.post("/api/mobile/orders/confirm", {
      data: { ticketToken: "does-not-matter" },
      maxRedirects: 0,
      failOnStatusCode: false
    });
    expect(response.status()).not.toBe(200);
  });

  test("only the documented routes are public", async ({ request }) => {
    for (const [route, reason] of publicRoutes) {
      const response = await request.get(route, { failOnStatusCode: false, maxRedirects: 0 });
      // Reachable is the claim, not healthy. /health/trading-readiness answers
      // 503 when the entry gate is blocked, which is the endpoint doing its job
      // and says nothing about authorisation. What must not happen is a 401 or a
      // redirect to the sign-in form.
      expect(response.status(), `${route} should be public because ${reason}`).not.toBe(401);
      expect(
        response.headers()["location"] ?? "",
        `${route} should be public because ${reason}`
      ).not.toContain("/Login");
    }
  });
});
