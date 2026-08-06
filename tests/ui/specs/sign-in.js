/* Signing in for the browser suite.

   The suite drives the real login form rather than bypassing authorisation,
   because every operator screen sits behind it: a suite that skipped sign-in
   would be testing a shell no operator ever sees. The account is seeded by the
   app itself under TRADINGFLOW_UI_TEST_MODE into the isolated .tmp data root. */

export const testUser = process.env.TRADINGFLOW_UI_TEST_USER ?? "ui-operator";
export const testPassword = process.env.TRADINGFLOW_UI_TEST_PASSWORD ?? "Ui-Test-Operator-1!";

/** Signs in, unless this context already holds a session. */
export async function signIn(page) {
  await page.goto("/Login");
  // Already authenticated: the login page redirects and the form is not present.
  if (await page.locator("#Input_UserName").count() === 0) {
    return;
  }
  await page.locator("#Input_UserName").fill(testUser);
  await page.locator("#Input_Password").fill(testPassword);
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForLoadState("domcontentloaded");
}
