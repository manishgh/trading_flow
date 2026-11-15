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
