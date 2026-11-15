import { expect, test } from "@playwright/test";
import { signIn } from "./sign-in.js";

// Select the current, enabled research parent by its actual strategy identity and version.
async function openExperimentRegistration(page) {
  await signIn(page);
  await page.goto("/Paper?paperExecutionMode=experiment");
  await page.getByText("Register an experiment", { exact: true }).click();
  const source = page.locator("#ExperimentSourceStrategyPath");
  const parent = source.getByRole("option", { name: /^RESEARCH ETF Buy-and-Hold Control V1 .* · 1\.0\.0$/ });
  await expect(parent).toHaveCount(1);
  await source.selectOption(await parent.getAttribute("value"));
}

// Registering the first paper experiment must work before any paper strategy exists: the run form's
// strategy choice is not part of registration.
test("the first paper experiment can be registered from a research strategy", async ({ page }) => {
  await openExperimentRegistration(page);
  await expect(page.locator("#ExperimentSemanticVersion")).toHaveValue("");
  await page.getByRole("button", { name: "Register experiment", exact: true }).click();
  await page.waitForURL(url => url.searchParams.has("strategyPath") || url.searchParams.get("handler") === "CreateExperiment");
  await expect(page.locator(".paper-validation")).toHaveText("");
  await expect(page).toHaveURL(url => url.searchParams.has("strategyPath"));
  await expect(page.getByText("The SelectedStrategyPath field is required.")).toHaveCount(0);
  await expect(page.locator("#SelectedStrategyDropdown")).toHaveValue(new URL(page.url()).searchParams.get("strategyPath"));
});

// Use the real paper form with synthetic preview/start responses. A lost response keeps the same start command.
test("paper stocks are reviewed in the selected mode before confirmation", async ({ page }) => {
  await openExperimentRegistration(page);
  // The full browser suite shares its owned journal; do not collide with the first-registration case.
  await page.locator("#ExperimentSemanticVersion").fill("1.0.101");
  await page.getByRole("button", { name: "Register experiment", exact: true }).click();
  await expect(page).toHaveURL(url => url.searchParams.has("strategyPath"));
  const path = await page.locator("#SelectedStrategyDropdown").inputValue();
  const preview = page.getByRole("button", { name: "Preview stocks", exact: true });
  const start = page.getByRole("button", { name: "Start strategy run", exact: true });
  const mode = page.getByLabel("Stock membership", { exact: true });
  const universeSnapshotId = "11111111-2222-3333-4444-555555555555";
  const contentSha256 = "f".repeat(64);
  const previews = [];
  const starts = [];
  await page.route("**/api/v1/catalog", route => route.fulfill({ json: {
    paperExperimentStrategies: [{ path, horizon: "swing", artifactStrategyId: "fixture", semanticVersion: "1.0.0", contentSha256 }]
  }}));
  await page.route("**/api/v1/universes/preview", route => {
    const body = route.request().postDataJSON();
    previews.push(body);
    return route.fulfill({ json: { universeSnapshotId, contentSha256, members: [{ symbol: "MSFT" }],
      paperMembership: { mode: body.paperMembershipMode, sources: [
        { sourceKind: "finviz", isBoundedObservation: true, normalizedQuery: "f=sh_relvol_o2", memberCount: 1 }
      ] } } });
  });
  await page.route("**/api/v1/paper/runs", route => {
    starts.push(route.request().postDataJSON());
    return starts.length === 1 ? route.abort("failed") : route.fulfill({ json: { jobId: universeSnapshotId, runName: "fixture", status: "queued" } });
  });

  await expect(mode).toHaveValue("fixed_at_launch");
  await expect(start).toBeHidden();
  await page.getByRole("radio", { name: "Finviz screen", exact: true }).check();
  await page.locator("#ScreenerFilter").fill("Saved screen fixture");
  await preview.click();
  await expect(page.locator("[data-paper-stock-review]")).toContainText("Fixed at launch");
  await expect(start).toBeVisible();
  await mode.selectOption("refresh_selected_sources");
  await expect(start).toBeHidden();
  await preview.click();
  await expect(page.locator("[data-paper-stock-review]")).toContainText("Refresh selected sources");
  await expect(page.locator("[data-paper-stock-review]")).toContainText("bounded observation");
  expect(starts).toHaveLength(0);
  expect(previews).toHaveLength(2);
  await start.click();
  await expect(page.locator("[data-run-workflow-status]")).toContainText("Retry keeps the same command");
  await start.click();
  await expect(page.getByRole("link", { name: "Open run" })).toHaveAttribute("href", "/PaperJob/" + universeSnapshotId);
  expect(starts[1]).toEqual(starts[0]);
  expect(starts[0]).toMatchObject({ universeSnapshotId, universeContentSha256: contentSha256, paperMembershipMode: "refresh_selected_sources" });
});

for (const version of ["", "1.0.0"]) test("changed experiment settings require a new version when the field is " +
    (version ? "equal to the parent" : "blank"), async ({ page }) => {
  await openExperimentRegistration(page);
  await page.getByRole("checkbox", { name: "Change settings (needs a new version)" }).check();
  // This research parent disables protection with zero values. Supply valid changes so native form validation allows the server to check the version.
  await page.locator("#ExperimentParameters_StopAtrMultiple").fill("4.25");
  await page.locator("#ExperimentParameters_TargetRMultiple").fill("2");
  await page.locator("#ExperimentSemanticVersion").fill(version);
  const invalid = await page.locator(".paper-experiment-registration form").evaluate(form =>
    Array.from(form.querySelectorAll("input:invalid"), input => input.name + ": " + input.validationMessage));
  expect(invalid).toEqual([]);
  await page.getByRole("button", { name: "Register experiment", exact: true }).click();
  await expect(page.locator(".paper-validation")).toContainText("Changed experiment parameters require a new semantic version.");
  expect(new URL(page.url()).searchParams.has("strategyPath")).toBe(false);
});

// Preparation's way back to trading leads to the Desk, never to a removed page.
test("preparation links back to the Desk", async ({ page }) => {
  await signIn(page);
  await page.goto("/Preparation");
  await expect(page.getByRole("link", { name: "Open the Desk", exact: true })).toHaveAttribute("href", "/Desk");
});
