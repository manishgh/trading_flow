import { defineConfig } from "@playwright/test";
import path from "node:path";
import { fileURLToPath } from "node:url";

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(testDirectory, "../..");
const dataRoot = path.join(repositoryRoot, ".tmp", "ui-tests", "data");
const baseURL = process.env.TRADINGFLOW_UI_BASE_URL ?? "http://127.0.0.1:53018";

export default defineConfig({
  testDir: "./specs",
  timeout: 30_000,
  expect: { timeout: 5_000 },
  fullyParallel: false,
  reporter: [["line"]],
  use: {
    baseURL,
    headless: true,
    trace: "retain-on-failure"
  },
  webServer: process.env.TRADINGFLOW_UI_BASE_URL
    ? undefined
    : {
        command: `dotnet run --project "${path.join(repositoryRoot, "src", "TradingFlow.Web", "TradingFlow.Web.csproj")}" --no-restore --urls ${baseURL}`,
        cwd: repositoryRoot,
        env: {
          ASPNETCORE_ENVIRONMENT: "Development",
          TRADINGFLOW_UI_TEST_MODE: "true",
          TRADINGFLOW_DATA_ROOT: dataRoot
        },
        url: `${baseURL}/health`,
        timeout: 120_000,
        reuseExistingServer: false
      }
});
