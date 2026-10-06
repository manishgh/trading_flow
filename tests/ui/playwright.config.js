import { defineConfig } from "@playwright/test";
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
          FINVIZ_API_KEY: " ", ALPACA_KEY_ID: " ", ALPACA_SECRET_KEY: " "
        },
        url: `${baseURL}/health`,
        timeout: 120_000,
        reuseExistingServer: false
      }
});
