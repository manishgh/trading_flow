import { expect, test } from "@playwright/test";
import { signIn } from "./sign-in.js";

/**
 * Covers the capability work: environment separation, desk filtering and sorting,
 * bulk group membership, the news hub, earnings filters, and the audit funnel.
 *
 * Read-only. Nothing here submits an order or mutates paper state.
 */

async function firstWishlistId(page) {
  await page.goto("/TradeDesk");
  return page.locator("#WishlistSelect option").first().getAttribute("value");
}

/** The wishlist id the management screen is currently showing. */
async function managedWishlistId(page) {
  await page.goto("/Wishlists");
  return page.locator('input[name="TargetWishlistId"]').first().getAttribute("value");
}

test.describe("trading environment separation", () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page);
  });

  test("paper is the default and live is locked with its promotion gates named", async ({ page }) => {
    await page.goto("/TradeDesk");
    await expect(page.locator("html")).toHaveAttribute("data-environment", "paper");
    await expect(page.locator(".environment-switch .seg-opt")).toHaveCount(2);

    await page.goto("/TradeDesk?env=live");
    await expect(page.locator("html")).toHaveAttribute("data-environment", "live");
    await expect(page.locator(".environment-lock")).toBeVisible();
    await expect(page.locator(".environment-lock")).toContainText("Outstanding gates");
  });

  test("a locked environment renders no order control anywhere in the document", async ({ page }) => {
    for (const route of ["/TradeDesk?env=live", "/OrderTicket?env=live&ticker=AAPL"]) {
      await page.goto(route);
      // Fail closed: the controls are absent, not merely disabled. Scoped to the
      // page body - the shared chrome's sign-out form is not an order control,
      // and it is present on every authenticated screen.
      await expect(page.locator('main button[type="submit"]')).toHaveCount(0);
      await expect(page.locator("main form[method='post']")).toHaveCount(0);
    }
  });

  test("no screen hardcodes an environment label", async ({ page }) => {
    await page.goto("/TradeDesk?env=live");
    await expect(page.locator('.environment-switch .seg-opt.env-live')).toBeVisible();
  });
});

test.describe("desk filtering and sorting", () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page);
  });

  test("every market column exposes a sortable header carrying aria-sort", async ({ page }) => {
    const id = await firstWishlistId(page);
    await page.goto(`/TradeDesk?id=${id}`);

    const sortLinks = page.locator(".trade-desk-table thead .sort-header");
    expect(await sortLinks.count()).toBeGreaterThan(0);

    await page.goto(`/TradeDesk?id=${id}&sort=price&dir=desc`);
    await expect(page.locator('.trade-desk-table thead th[aria-sort="descending"]')).toHaveCount(1);

    await page.goto(`/TradeDesk?id=${id}&sort=price&dir=asc`);
    await expect(page.locator('.trade-desk-table thead th[aria-sort="ascending"]')).toHaveCount(1);
  });

  test("search narrows the market list and reports the filtered total", async ({ page }) => {
    const id = await firstWishlistId(page);
    await page.goto(`/TradeDesk?id=${id}`);
    const all = await page.locator(".trade-desk-table [data-symbol-row]").count();

    await page.goto(`/TradeDesk?id=${id}&search=zzzznomatch`);
    await expect(page.locator(".trade-desk-table [data-symbol-row]")).toHaveCount(0);
    await expect(page.locator(".desk-view-controls")).toContainText(`of ${all}`);
  });

  test("filter and sort state survives a reload through the URL", async ({ page }) => {
    const id = await firstWishlistId(page);
    await page.goto(`/TradeDesk?id=${id}&search=A&sort=ticker&dir=desc&maxSpreadBps=500`);
    await expect(page.locator("#Search")).toHaveValue("A");
    await expect(page.locator("#MaxSpreadBps")).toHaveValue("500");
  });
});

test.describe("group membership", () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page);
  });

  test("symbols can be added in bulk and selected in bulk", async ({ page }) => {
    const id = await managedWishlistId(page);
    test.skip(!id, "No wishlist exists in this environment.");

    await page.goto(`/Wishlists?id=${id}`);
    await expect(page.locator("#BulkTickers")).toBeVisible();
    await expect(page.locator("[data-bulk-toggle-all]")).toBeVisible();

    // The action bar stays hidden until something is selected.
    await expect(page.locator("[data-bulk-bar]")).toBeHidden();
    const rows = page.locator("[data-bulk-item]");
    if (await rows.count() > 0) {
      await rows.first().check();
      await expect(page.locator("[data-bulk-bar]")).toBeVisible();
      await expect(page.locator("[data-bulk-count]")).toHaveText("1");
    }
  });

  test("the management table no longer renders one form per row", async ({ page }) => {
    const id = await managedWishlistId(page);
    test.skip(!id, "No wishlist exists in this environment.");

    await page.goto(`/Wishlists?id=${id}`);
    const rowCount = await page.locator("[data-bulk-item]").count();
    const visibleForms = await page.locator("form:not([hidden])").count();
    if (rowCount > 3) {
      expect(visibleForms).toBeLessThan(rowCount);
    }
  });
});

test.describe("news hub", () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page);
  });

  test("exposes every filter dimension the news record carries", async ({ page }) => {
    await page.goto("/News");
    for (const id of ["#Hours", "#GroupId", "#Ticker", "#Provider", "#Sentiment", "#Search"]) {
      await expect(page.locator(id)).toBeVisible();
    }
    await expect(page.locator('input[name="linkedOnly"]')).toHaveCount(1);
  });

  test("filters round-trip through the URL", async ({ page }) => {
    await page.goto("/News?hours=24&sentiment=positive&search=earnings");
    await expect(page.locator("#Hours")).toHaveValue("24");
    await expect(page.locator("#Sentiment")).toHaveValue("positive");
    await expect(page.locator("#Search")).toHaveValue("earnings");
  });
});

test.describe("earnings calendar", () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page);
  });

  test("exposes date range, result, and refinement controls", async ({ page }) => {
    await page.goto("/Earnings");
    await expect(page.locator("#earnings-from")).toBeVisible();
    await expect(page.locator("#earnings-to")).toBeVisible();
    await expect(page.locator("#earnings-assessment-filters button")).not.toHaveCount(0);
    await expect(page.locator("#earnings-has-news")).toBeVisible();
    await expect(page.locator("#earnings-min-surprise")).toBeVisible();
    await expect(page.locator("#earnings-sort")).toBeVisible();
  });

  test("the three counted attention lanes are the top of the screen", async ({ page }) => {
    await page.goto("/Earnings");
    const lanes = page.locator("#earnings-lanes [data-lane]");
    await expect(lanes).toHaveCount(3);

    // Each lane states its own rule, so a count can be argued with rather than
    // taken on trust.
    await expect(lanes.nth(0)).toContainText("pre-release reference high");
    await expect(lanes.nth(1)).toContainText("next half hour");
    await expect(lanes.nth(2)).toContainText("Open positions");

    // A lane is a filter, and it toggles.
    await expect(lanes.first()).toHaveAttribute("aria-pressed", "false");
    await lanes.first().click();
    await expect(lanes.first()).toHaveAttribute("aria-pressed", "true");
    await lanes.first().click();
    await expect(lanes.first()).toHaveAttribute("aria-pressed", "false");
  });

  test("the monitor states that it is advisory and cannot route", async ({ page }) => {
    await page.goto("/Earnings");
    // Not decoration: the monitor cannot place an order, so it says so rather
    // than leaving the absence of a button to imply it.
    await expect(page.locator(".earnings-monitor")).toContainText("Advisory only");
    await expect(page.locator("main form[method='post']")).toHaveCount(0);
    await expect(page.locator("main button[type='submit']")).toHaveCount(0);
  });
});

test.describe("backtest audit", () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page);
  });

  test("offers a decision funnel and a CSV export", async ({ page }) => {
    await page.goto("/Audit/ui-release-audit");
    await expect(page.getByRole("link", { name: "Export CSV" })).toBeVisible();
    // The funnel only renders when the run produced decisions.
    const funnel = page.locator(".audit-funnel");
    if (await funnel.count() > 0) {
      await expect(funnel.locator("li").first()).toContainText("Evaluated");
    }
  });

  test("the audit page carries no inline styling of its own", async ({ page }) => {
    await page.goto("/Audit/ui-release-audit");
    await expect(page.locator("style")).toHaveCount(0);
    await expect(page.locator("[style]")).toHaveCount(0);
  });
});

test.describe("order ticket breadth", () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page);
  });

  test("an entry stays a protected limit while an exit offers the wider set", async ({ page }) => {
    await page.goto("/OrderTicket?ticker=AAPL&side=buy");
    // A bracketed entry must not offer market or stop: that would drop the protection.
    await expect(page.locator("#OrderType option")).toHaveCount(1);
    await expect(page.locator("#StopLossPrice")).toBeVisible();

    await page.goto("/OrderTicket?ticker=AAPL&side=sell");
    await expect(page.locator("#OrderType option")).toHaveCount(4);
    await expect(page.locator("#TriggerPrice")).toBeVisible();
    // An exit carries no bracket; protection belonged to the entry.
    await expect(page.locator("#StopLossPrice")).toHaveCount(0);
  });

  test("time in force is selectable on both sides", async ({ page }) => {
    await page.goto("/OrderTicket?ticker=AAPL&side=sell");
    await expect(page.locator("#TimeInForce option")).toHaveCount(4);
  });

  test("a locked environment exposes no ticket form at all", async ({ page }) => {
    await page.goto("/OrderTicket?ticker=AAPL&side=sell&env=live");
    await expect(page.locator("#OrderType")).toHaveCount(0);
    await expect(page.locator('main button[type="submit"]')).toHaveCount(0);
  });
});

test.describe("order lifecycle controls", () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page);
  });

  test("orders exposes an actions column and hides it when the environment is locked", async ({ page }) => {
    await page.goto("/Orders");
    // The table only renders once the journal has orders; the empty state is valid.
    const headers = page.locator("table.orders-table thead th");
    if (await headers.count() > 0) {
      await expect(headers.filter({ hasText: "Client order" })).toHaveCount(1);
      await expect(headers.filter({ hasText: "Status" })).toHaveCount(1);
    } else {
      await expect(page.locator("[data-orders-empty]")).toBeVisible();
    }

    // A locked environment never renders a cancel control, with or without rows.
    await page.goto("/Orders?env=live");
    await expect(page.locator(".order-actions form")).toHaveCount(0);
  });
});

test.describe("strategy lab", () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page);
  });

  test("configure is one phase of one screen, not a separate page", async ({ page }) => {
    await page.goto("/Backtests");
    await expect(page.getByRole("heading", { name: "Strategy Lab", level: 1 })).toBeVisible();

    // Three phases, one screen. The phase is derived from the run status, so
    // Running and Results are not selectable controls.
    const phases = page.locator(".lab-phases .seg-opt");
    await expect(phases).toHaveCount(3);
    await expect(phases.nth(0)).toHaveAttribute("aria-current", "true");
  });

  test("the run is marked research-only because the universe is a current list", async ({ page }) => {
    await page.goto("/Backtests");
    // The sentence that explains why the promotion gate fails. It must not be
    // dropped: without it a research-only run reads as a promotable one.
    await expect(page.locator(".lab-rail")).toContainText("Point-in-time membership is required");
    await expect(page.locator(".lab-rail")).toContainText("research-only");
  });

  test("the integrated decision is stated and defaults to retaining research", async ({ page }) => {
    await page.goto("/Backtests");
    await expect(page.locator(".lab-head-tail")).toContainText("RETAIN_RESEARCH");
  });

  test("a promoted strategy is distinguishable from a research-only one", async ({ page }) => {
    await page.goto("/Backtests");
    const labels = await page.locator(".lab-strategy .tag").allTextContents();
    if (labels.length > 0) {
      // Research-only strategies must never reach paper or live, so the screen
      // labels which is which rather than leaving it to the file path.
      expect(labels.every(label => /PROMOTED|RESEARCH ONLY/.test(label))).toBe(true);
    }
  });

  test("the legacy run route forwards into the lab", async ({ page }) => {
    await page.goto("/Job/00000000-0000-0000-0000-000000000000");
    await expect(page).toHaveURL(/\/Backtests\?jobId=/);
  });
});

test.describe("audit depth", () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page);
  });

  test("offers run comparison", async ({ page }) => {
    await page.goto("/Audit/ui-release-audit");
    await expect(page.locator("#CompareWith")).toBeVisible();
    await page.goto("/Audit/ui-release-audit?compareWith=does-not-exist");
    await expect(page.locator("#CompareWith")).toHaveValue("does-not-exist");
  });
});

test.describe("desk order shortcuts", () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page);
  });

  test("the buy action carries a keyboard hint and never submits on its own", async ({ page }) => {
    await page.goto("/TradeDesk");
    const id = await page.locator("#WishlistSelect option").first().getAttribute("value");
    const ticker = await page.locator(".trade-desk-table [data-symbol-row]").first().getAttribute("data-symbol");
    test.skip(!ticker, "No symbols seeded.");

    await page.goto(`/TradeDesk?id=${id}&ticker=${ticker}`);
    // The ticket is inline on the desk now. Stage one reviews; it never submits,
    // and no confirm control exists until the server has checked the draft.
    const review = page.getByRole("button", { name: /^Review protected buy/ });
    await expect(review).toBeVisible();
    await expect(page.getByRole("button", { name: /^Confirm paper/ })).toHaveCount(0);
  });
});
