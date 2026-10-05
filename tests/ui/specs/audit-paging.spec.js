import { expect, test } from '@playwright/test';
import { signIn } from './sign-in.js';

function snapshot() {
  return {
    success: true, refreshedAt: 'test snapshot', evaluations: 100003,
    accepted: 25001, rejected: 50001, tickerCount: 10, strategyCount: 1,
    totalMatching: 50001, pageNumber: 1, pageCount: 1001, pageSize: 50,
    sort: 'time', descending: true, comparison: [], comparisonRunName: null,
    funnel: [{ label: 'Evaluated', remaining: 100003, total: 100003, removed: 0, remainingPercent: 100 }],
    reasons: [{ reasonKey: 'rvol', displayReason: 'Relative volume', exampleReason: 'rvol', count: 40, percentOfRejected: 0.1 }],
    records: Array.from({ length: 50 }, (_, index) => ({
      id: index + 1, localTime: '05 Oct 11:00', ticker: `T${index}`, strategy: 'Swing',
      decision: 'Rejected', reasonKey: 'rvol', reason: 'Relative volume', rawReason: 'rvol',
      explanation: '<script>untrusted</script>', chips: [], gateEvidence: [], signalJson: '{}'
    }))
  };
}

test.beforeEach(async ({ page }) => { await signIn(page); });

test('export links keep server filters even when the live read fails', async ({ page }) => {
  await page.route('**/Audit/*handler=Snapshot*', route => route.fulfill({ status: 503, body: '' }));
  await page.goto('/Audit/empty?decision=rejected&ticker=AA&reason=rvol');
  for (const label of ['Export CSV', 'Export JSON']) {
    const url = new URL(await page.getByRole('link', { name: label }).getAttribute('href'), page.url());
    expect(url.searchParams.get('decision')).toBe('rejected');
    expect(url.searchParams.get('ticker')).toBe('AA');
    expect(url.searchParams.get('reason')).toBe('rvol');
  }
  await page.locator('#tickerFilter').fill('MSFT');
  await expect(page.getByRole('link', { name: 'Export CSV' })).toHaveAttribute('href', /ticker=MSFT/);
  await expect(page.locator('#auditReadStatus')).toContainText('503');
});

test('bounded refresh retains open details and focused reason controls', async ({ page }) => {
  const data = snapshot();
  await page.route('**/Audit/*handler=Snapshot*', route => route.fulfill({ json: data }));
  await page.goto('/Audit/large');
  await expect(page.locator('#auditRecordsBody > tr')).toHaveCount(50);
  await expect(page.locator('#auditEvaluations')).toHaveText('100003');
  await expect(page.locator('#auditOutcomePanel')).toBeVisible();
  const progress = page.locator('#auditOutcomePanel progress').first();
  await expect(progress).toHaveAttribute('value','100');
  data.funnel[0].remainingPercent = 25;
  await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted:true })));
  await expect(progress).toHaveJSProperty('value',25);
  expect(await progress.evaluate(node => getComputedStyle(node).backgroundColor)).toBe('rgba(0, 0, 0, 0)');
  await expect(page.locator('#auditRecordsBody script')).toHaveCount(0);
  const summary = page.locator('#auditRecordsBody summary').first();
  await summary.click();
  const reason = page.locator('[data-reason-card=rvol]');
  await reason.focus();
  await page.evaluate(async () => {
    window.auditReasonNode = document.activeElement;
    window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }));
  });
  await expect(reason).toBeFocused();
  expect(await page.evaluate(() => window.auditReasonNode === document.activeElement)).toBe(true);
  await expect(page.locator('#auditRecordsBody details').first()).toHaveAttribute('open', '');
  const retained = page.locator('#auditRecordsBody [data-id="1"] summary');
  await retained.focus();
  data.records.reverse();
  await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true })));
  await expect(page.locator('#auditRecordsBody > tr').first()).toHaveAttribute('data-id', '50');
  await expect(retained).toBeFocused();
  await expect(page.locator('#auditRecordsBody [data-id="1"] details')).toHaveAttribute('open', '');
  await page.locator('[data-audit-sort=ticker] a').click();
  await expect(page).toHaveURL(/sort=ticker/);
  await expect(page.getByRole('link', { name: 'Export JSON' })).toHaveAttribute('href', /handler=Export/);
});

test('restored pages restart refresh after pagehide', async ({ page }) => {
  let reads = 0;
  await page.route('**/Audit/*handler=Snapshot*', route => { reads++; return route.fulfill({ json: snapshot() }); });
  await page.goto('/Audit/large');
  await expect.poll(() => reads).toBeGreaterThan(0);
  const before = reads;
  await page.evaluate(() => {
    window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true }));
    window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }));
  });
  await expect.poll(() => reads).toBeGreaterThan(before);
});
