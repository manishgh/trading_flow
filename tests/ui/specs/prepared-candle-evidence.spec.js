import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";

const progressSource = fs.readFileSync(path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../src/TradingFlow.Web/wwwroot/js/lab-progress.js"), "utf8");

// Creates a browser-only job panel and fetch stub; no source list, job or provider request is written.
async function openPanel(page, snapshot) {
  const fields = ["id", "hash", "provider", "feed", "basis", "coverage", "time", "reused", "downloaded", "note"];
  await page.setContent(`<div class="lab-main"><section data-lab-job="${snapshot.jobId}">
    <span data-lab-status></span><span data-lab-stage></span><p data-lab-error hidden></p>
    <div data-lab-prepared hidden>${fields.map(field => `<span data-prepared-${field}></span>`).join("")}</div>
    <ul data-lab-events></ul></section></div><script data-job-id="${snapshot.jobId}"></script>`);
  await page.evaluate(value => {
    window.fixtureSnapshot = value;
    window.fixtureReads = 0;
    window.tradingFlowFetch = async () => { window.fixtureReads++; return { ok: true, json: async () => window.fixtureSnapshot }; };
  }, snapshot);
  await page.evaluate(source => {
    const script = document.createElement("script");
    script.dataset.jobId = window.fixtureSnapshot.jobId;
    script.textContent = source;
    document.body.append(script);
  }, progressSource);
}

// Exercises polling with only synthetic server facts, including a queued job that has no selected hash.
test("prepared candle evidence appears only after commitment and renders failure text safely", async ({ page }) => {
  const jobId = randomUUID();
  await openPanel(page, { jobId, status: "running", currentStage: "preparing_candles", events: [], preparedMarketData: null });
  await expect(page.locator("[data-lab-stage]")).toHaveText("Preparing candles");
  await expect(page.locator("[data-lab-prepared]")).toBeHidden();
  const prepared = { snapshotId: "synthetic-snapshot", snapshotSha256: "a".repeat(64), providers: ["fixture"],
    dataFeeds: ["fixture-feed"], priceAdjustments: ["raw", "all"], startUtc: "2026-01-01T00:00:00Z",
    endUtc: "2026-06-30T23:59:00Z", preparedAtUtc: "2026-10-21T09:00:00Z", reusedQueryCount: 3,
    downloadedQueryCount: 2, historicalEvidenceNote: "Captured now; original historical revision availability is unproved." };
  await page.evaluate(value => { window.fixtureSnapshot = value; }, { jobId, status: "running", currentStage: "market_pipeline", events: [], preparedMarketData: prepared,
    errorMessage: "Selected evidence changed <img src=x onerror=alert(1)>" });
  await expect(page.locator("[data-lab-prepared]")).toBeVisible();
  for (const [field, expected] of Object.entries({ id: prepared.snapshotId, hash: prepared.snapshotSha256, provider: "fixture",
    feed: "fixture-feed", basis: "raw, all", coverage: `${prepared.startUtc} to ${prepared.endUtc}`, time: prepared.preparedAtUtc,
    reused: "3", downloaded: "2", note: prepared.historicalEvidenceNote })) {
    await expect(page.locator(`[data-prepared-${field}]`)).toHaveText(expected);
  }
  await expect(page.locator("[data-lab-error]")).toHaveText("Selected evidence changed <img src=x onerror=alert(1)>");
  await expect(page.locator("[data-lab-error] img")).toHaveCount(0);
});

// A response for a different selected job must never replace the current job's evidence.
test("prepared candle poll rejects a mismatched job snapshot", async ({ page }) => {
  await openPanel(page, { jobId: randomUUID(), status: "running", currentStage: "preparing_candles", events: [] });
  await expect(page.locator("[data-lab-stage]")).toHaveText("Preparing candles");
  const reads = await page.evaluate(() => window.fixtureReads);
  await page.evaluate(otherJobId => { window.fixtureSnapshot = { jobId: otherJobId, status: "running", currentStage: "wrong_job", events: [],
    preparedMarketData: { snapshotId: "wrong-snapshot" } }; }, randomUUID());
  await expect.poll(() => page.evaluate(() => window.fixtureReads)).toBeGreaterThan(reads);
  await expect(page.locator("[data-lab-stage]")).toHaveText("Preparing candles");
  await expect(page.locator("[data-lab-prepared]")).toBeHidden();
});

// A late reply to a detached panel cannot write evidence into a newly selected job panel.
test("prepared candle poll ignores a detached panel reply", async ({ page }) => {
  const jobId = randomUUID();
  await openPanel(page, { jobId, status: "running", currentStage: "preparing_candles", events: [] });
  await expect(page.locator("[data-lab-stage]")).toHaveText("Preparing candles");
  await page.evaluate(() => {
    window.tradingFlowFetch = () => new Promise(resolve => { window.releaseFixtureReply = resolve; });
  });
  await expect.poll(() => page.evaluate(() => typeof window.releaseFixtureReply)).toBe("function");
  await page.evaluate(() => {
    const old = document.querySelector("[data-lab-job]");
    const replacement = old.cloneNode(true);
    replacement.dataset.labJob = "new-synthetic-job";
    replacement.querySelector("[data-lab-stage]").textContent = "New selection";
    old.replaceWith(replacement);
    window.releaseFixtureReply({ ok: true, json: async () => ({ jobId: old.dataset.labJob,
      status: "running", currentStage: "old_reply", events: [], preparedMarketData: { snapshotId: "old-snapshot" } }) });
  });
  await expect(page.locator("[data-lab-stage]")).toHaveText("New selection");
  await expect(page.locator("[data-lab-prepared]")).toBeHidden();
});
