import { expect, test } from "@playwright/test";
import { signIn } from "./sign-in.js";

// Registering the first paper experiment must work before any paper strategy exists: the run form's
// strategy choice is not part of registration.
test("the first paper experiment can be registered from a research strategy", async ({ page }) => {
  await signIn(page);
  await page.goto("/Paper?paperExecutionMode=experiment");
  await page.getByText("Register an experiment", { exact: true }).click();
  const source = page.locator("#ExperimentSourceStrategyPath");
  const connors = await source.locator("option").filter({ hasText: "7.0.0" }).first().getAttribute("value");
  await source.selectOption(connors);
  await page.locator("#ExperimentSemanticVersion").fill(`7.0.${Date.now() % 100000}`);
  await page.getByRole("button", { name: "Register experiment", exact: true }).click();
  await expect(page.getByText("The SelectedStrategyPath field is required.")).toHaveCount(0);
  await expect(page.locator("#SelectedStrategyDropdown option")).not.toHaveCount(0);
});

// Preparation's way back to trading leads to the Desk, never to a removed page.
test("preparation links back to the Desk", async ({ page }) => {
  await signIn(page);
  await page.goto("/Preparation");
  await expect(page.getByRole("link", { name: "Open the Desk", exact: true })).toHaveAttribute("href", "/Desk");
});
