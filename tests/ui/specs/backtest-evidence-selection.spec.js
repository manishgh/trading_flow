import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';

const workflow = fs.readFileSync(path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../src/TradingFlow.Web/wwwroot/js/run-workflow.js'), 'utf8');
const reference = { id: 'approved-profile', manifestSha256: 'a'.repeat(64) };
const summary = { reference, provider: 'fixture', version: 'v1', taxonomyVersions: ['taxonomy-2'], classifierVersions: ['classifier-3'],
  evaluationSha256s: ['b'.repeat(64)], availabilityBasis: ['actual_receipt'], coverageScopes: ['company', 'sector'],
  eventLookbackDays: 5, maxCoverageAgeHours: 24, unknownReasons: ['global coverage absent <img src=x>'], dependencyGraphSha256: 'c'.repeat(64) };

// Uses a fulfilled loopback document so production crypto APIs run in the same secure context as the real app.
async function openFixture(page, saved = []) {
  const html = `<form class="lab-rail"><select name="SelectedConfigPath"><option>profile.yaml</option></select>
    <input name="RunName" value="fixture"><input name="LookbackDays" value="180">
    <input name="StartingCapital" value="10000"><input name="AccountRiskBudgetPct" value="1">
    <input name="MaxPositionNotionalPct" value="25"><input name="MaxConcurrentPositions" value="4">
    <input type="checkbox" name="StrategyPaths" value="fixture.yaml" checked>
    <select name="StockSource"><option value="fixture" data-source-kind="wishlist" data-source-key="fixture">Fixture</option></select>
    <input name="GroupSize" value="5"><select name="DecisionContextEvidence" data-evidence-picker></select>
    <p data-evidence-availability></p><div data-evidence-summary></div><div data-run-validation hidden></div>
    <button type="submit">Preview groups</button><button type="submit" data-backtest-action="save">Save plan</button>
    <select data-saved-backtest-plan></select><button type="button" data-start-backtest-plan disabled>Start saved plan</button>
    <div data-backtest-plan-preview></div></form>
    <section data-job-evidence="22222222-2222-2222-2222-222222222222" hidden><div data-job-evidence-summary></div></section>`;
  await page.route('**/backtest-evidence-fixture', route => route.fulfill({ contentType: 'text/html', body: html }));
  await page.goto('/backtest-evidence-fixture');
  expect(await page.evaluate(() => window.isSecureContext && typeof crypto.randomUUID === 'function')).toBe(true);
  await page.evaluate(({ saved, reference, summary }) => {
    window.fixtureSaved = saved; window.fixtureRequests = []; window.fixtureStarts = [];
    window.tradingFlowFetch = async (url, options = {}) => {
      const body = options.body ? JSON.parse(options.body) : null;
      let value;
      if (url.endsWith('/catalog')) value = { strategies: [{ path: 'fixture.yaml', artifactStrategyId: 'fixture', semanticVersion: '1.0.0', contentSha256: 'd'.repeat(64) }],
        evidenceChoices: [{ reference, name: 'Approved fixture', available: true, unavailableReason: null, summary },
          { reference: null, name: 'Missing strict archive', available: false, unavailableReason: 'Original receipts absent', summary: null }] };
      else if (url.endsWith('/universes/preview')) value = { universeSnapshotId: '11111111-1111-1111-1111-111111111111' };
      else if (url.endsWith('/details')) value = { job: { jobId: '22222222-2222-2222-2222-222222222222' }, evidenceSummary: summary };
      else if (url.endsWith('/start')) { window.fixtureStarts.push(body); value = { jobs: [] }; }
      else if (url.endsWith('/plans') && !body) value = window.fixtureSaved;
      else {
        window.fixtureRequests.push(body);
        value = { planId: url.endsWith('/preview') ? 0 : 7, savedAtUtc: '2026-10-01T00:00:00Z', run: body.run,
          configurationSha256: 'e'.repeat(64), executionCount: 1, membershipDescription: 'Fixture stocks',
          cohorts: [{ name: 'Group 1', symbols: ['MSFT'], startingCapital: 10000 }], evidenceSummary: body.run.decisionContextEvidence ? summary : null,
          resolvedSettings: { configurationKey: 'profile.yaml', configurationSha256: 'e'.repeat(64), fixedBuyFee: 1, fixedSellFee: 2,
            secFeeRate: 0.00001, finraTafPerShare: 0.0002, finraTafCap: 8, maxBarParticipationPct: 5, impactBpsAtMaxParticipation: 3,
            executionReplayPolicyId: 'native-open-cutoff', precommittedLiquidationSourceUtc: null,
            cutoffPolicy: 'Positions remain open at the exclusive cutoff; no forced terminal exit.',
            strategies: [{ identity: body.run.strategies[0], name: 'Recovery fixture', slippageBps: 10,
              derivedFromIdentity: { strategyId: 'original-parent', semanticVersion: '1.0.0', contentSha256: 'f'.repeat(64) },
              admissionProfile: { profileId: 'reviewed-native', profileVersion: '1.0.0', executionTimingPolicy: 'existing-native', riskPolicy: 'shared-risk' } }] } };
        if (value.planId) window.fixtureSaved = [value];
      }
      return { ok: true, status: 200, json: async () => value };
    };
  }, { saved, reference, summary });
  await page.addScriptTag({ content: workflow });
  await expect(page.locator('[data-evidence-picker] option')).toHaveCount(3);
}

// Frozen evidence stays attached through preview, save, start and reopening; edits explicitly invalidate the selected saved plan.
test('evidence selection preserves immutable server facts through the saved-plan workflow', async ({ page }) => {
  await openFixture(page);
  const save = page.getByRole('button', { name: 'Save plan', exact: true });
  await expect(save).toBeDisabled();
  await page.locator('[data-evidence-picker]').selectOption(reference.id);
  await expect(save).toBeDisabled();
  await expect(page.locator('[data-evidence-summary]')).toContainText(summary.reference.manifestSha256);
  await expect(page.locator('[data-evidence-summary]')).toContainText(summary.unknownReasons[0]);
  await expect(page.locator('[data-evidence-summary] img')).toHaveCount(0);
  await expect(page.locator('[data-job-evidence]')).toBeVisible();
  expect(await page.locator('[data-job-evidence-summary] p').allTextContents()).toEqual(
    await page.locator('[data-evidence-summary] p').allTextContents());
  await page.getByRole('button', { name: 'Preview groups', exact: true }).click();
  await expect(page.locator('[data-run-workflow-status]')).toContainText('Review the groups, then save the plan before starting.');
  await expect(page.locator('[data-backtest-plan-preview]')).toContainText('Event lookback days: 5');
  await expect(page.locator('[data-backtest-plan-preview]')).toContainText('Fixed buy fee USD: 1 · Fixed sell fee USD: 2');
  await expect(page.locator('[data-backtest-plan-preview]')).toContainText('Parent: original-parent 1.0.0 (' + 'f'.repeat(64) + ')');
  await expect(page.locator('[data-backtest-plan-preview]')).toContainText('Positions remain open at the exclusive cutoff; no forced terminal exit.');
  await expect(save).toBeEnabled();
  await page.getByRole('button', { name: 'Save plan', exact: true }).click();
  const start = page.getByRole('button', { name: 'Start saved plan', exact: true });
  await expect(start).toBeEnabled();
  const requests = await page.evaluate(() => window.fixtureRequests);
  expect(requests.map(item => item.run.decisionContextEvidence)).toEqual([reference, reference]);
  expect(requests[1].run.idempotencyKey).toBe(requests[0].run.idempotencyKey);
  expect(Object.keys(requests[1].run.decisionContextEvidence).sort()).toEqual(['id', 'manifestSha256']);
  await start.click();
  expect(await page.evaluate(() => window.fixtureStarts.length)).toBe(1);
  const saved = await page.evaluate(() => window.fixtureSaved);
  await openFixture(page, saved);
  await page.locator('[data-saved-backtest-plan]').selectOption('7');
  await expect(page.locator('[data-backtest-plan-preview]')).toContainText(reference.manifestSha256);
  await expect(page.locator('[data-backtest-plan-preview]')).toContainText('Availability basis: actual_receipt');
  await expect(page.locator('[data-backtest-plan-preview]')).toContainText('Adverse slippage bps: 10');
  await expect(page.locator('[data-backtest-plan-preview]')).toContainText('Admission: reviewed-native 1.0.0');
  await expect(start).toBeEnabled();
  await page.locator('[data-evidence-picker]').selectOption(reference.id);
  await expect(start).toBeDisabled();
  await expect(save).toBeDisabled();
  await expect(page.locator('[data-backtest-plan-preview]')).toBeEmpty();
});

// Missing strict evidence is explanatory catalog text; unbound price-only requests retain their previous wire shape.
test('unavailable reasons remain visible and price-only plans have no evidence reference', async ({ page }) => {
  await openFixture(page);
  await expect(page.locator('[data-evidence-availability]')).toContainText('Original receipts absent');
  await expect(page.locator('[data-evidence-picker] option').last()).toBeDisabled();
  await page.getByRole('button', { name: 'Preview groups', exact: true }).click();
  await expect(page.locator('[data-run-workflow-status]')).toContainText('Review the groups, then save the plan before starting.');
  await expect(page.locator('[data-backtest-plan-preview]')).toContainText('Group 1: MSFT');
  expect(await page.evaluate(() => window.fixtureRequests[0].run.decisionContextEvidence)).toBeUndefined();
});

// Departing the connection discards form-bound references and saved-plan retry identity.
test('page departure clears evidence and saved plan controls', async ({ page }) => {
  await openFixture(page);
  await page.locator('[data-evidence-picker]').selectOption(reference.id);
  await page.getByRole('button', { name: 'Preview groups', exact: true }).click();
  await expect(page.locator('[data-run-workflow-status]')).toContainText('Review the groups, then save the plan before starting.');
  await expect(page.getByRole('button', { name: 'Save plan', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Save plan', exact: true }).click();
  await expect(page.locator('[data-start-backtest-plan]')).toBeEnabled();
  await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pagehide')));
  await expect(page.locator('[data-evidence-picker]')).toHaveValue('');
  await expect(page.locator('[data-evidence-summary]')).toBeEmpty();
  await expect(page.locator('[data-start-backtest-plan]')).toBeDisabled();
});
