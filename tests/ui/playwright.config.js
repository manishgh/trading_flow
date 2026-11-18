import { defineConfig } from "@playwright/test";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { randomUUID } from "node:crypto";

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(testDirectory, "../..");
if (process.env.TRADINGFLOW_UI_BASE_URL) throw new Error("Mutation tests must own an isolated host; operator/review URLs are not test targets.");
const runId = process.env.TRADINGFLOW_UI_OWNED_RUN_ID ?? randomUUID();
if (!/^[0-9a-f-]{36}$/i.test(runId)) throw new Error("Invalid owned test-run identifier.");
process.env.TRADINGFLOW_UI_OWNED_RUN_ID = runId;
const dataRoot = path.join(repositoryRoot, ".tmp", "ui-tests", "test-runs", "run-" + runId);
const artifactRoot = path.join(repositoryRoot, ".tmp", "ui-test-artifacts", "run-" + runId);
const baseURL = "http://127.0.0.1:53018";

// The operator catalog switched every strategy off on 18 Nov 2026. The owned test host reads a copy (inside its own
// run folder, deleted by the cleanup reporter) with the set-aside opening-range and ETF strategies enabled, matching the
// .NET tests' TestRepository.SetAsideStrategiesEnabledCatalog. Every other entry, path and content hash is unchanged.
const setAsideStrategyIds = new Set(["technical.opening-range-breakout", "etf.trend-10m", "etf.buy-and-hold-control"]);
const testCatalogPath = path.join(dataRoot, "strategy-catalog.json");
const catalog = JSON.parse(fs.readFileSync(path.join(repositoryRoot, "configs", "strategy-catalog.json"), "utf8"));
for (const entry of catalog.strategies) {
  if (setAsideStrategyIds.has(entry.strategyId)) { delete entry.enabled; delete entry.disabledReason; }
}
fs.mkdirSync(dataRoot, { recursive: true });
fs.writeFileSync(testCatalogPath, JSON.stringify(catalog, null, 2) + "\n");

export default defineConfig({
  testDir: "./specs",
  timeout: 30_000,
  expect: { timeout: 5_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [["line"], ["./cleanup-reporter.js"]],
  outputDir: artifactRoot,
  use: {
    baseURL,
    headless: true,
    trace: "off"
  },
  webServer: {
        command: `dotnet run --project "${path.join(repositoryRoot, "src", "TradingFlow.Web", "TradingFlow.Web.csproj")}" --no-restore --urls ${baseURL}`,
        cwd: repositoryRoot,
        env: {
          ASPNETCORE_ENVIRONMENT: "Development",
          TRADINGFLOW_UI_TEST_MODE: "true",
          TRADINGFLOW_DISABLE_RUNTIME: "false",
          TRADINGFLOW_ENABLE_MARKET_FEEDS: "false",
          TRADINGFLOW_DATA_ROOT: dataRoot,
          TRADINGFLOW_UI_STRATEGY_CATALOG: testCatalogPath,
          FINVIZ_API_KEY: " ", ALPACA_KEY_ID: " ", ALPACA_SECRET_KEY: " "
        },
        url: `${baseURL}/health`,
        timeout: 120_000,
        reuseExistingServer: false
      }
});
