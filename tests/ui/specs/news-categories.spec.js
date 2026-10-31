import { expect, test } from '@playwright/test';
import { signIn } from './sign-in.js';
import path from 'node:path';

for (const width of [390, 1440]) test(`News category navigation and sector filters work at ${width}px`, async ({ page }) => {
  await page.setViewportSize({ width, height: 1000 });
  await page.goto('/News?hours=4&category=all');
  const navigation = page.getByRole('navigation', { name: 'News categories', exact: true });
  for (const label of ['All news', 'Company', 'Sector', 'Financial / macro', 'Government / policy', 'Global events'])
    await expect(navigation.getByRole('link', { name: label, exact: true })).toBeVisible();
  await navigation.getByRole('link', { name: 'Global events', exact: true }).click();
  await expect(navigation.locator('[aria-current="page"]')).toHaveText('Global events');
  await expect(navigation.locator('[aria-current]')).toHaveCount(1);
  await page.getByLabel('Sector topic', { exact: true }).selectOption('energy');
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  let url = new URL(page.url());
  expect(url.searchParams.get('category')).toBe('global');
  expect(url.searchParams.get('sector')).toBe('energy');
  expect(url.searchParams.get('hours')).toBe('4');
  expect(url.searchParams.get('pageNumber')).toBe('1');
  await expect(page.locator('#news-results-heading')).toContainText('Global events');
  await expect(page.getByLabel('Sector topic', { exact: true })).toHaveValue('energy');
  await expect(page.locator('.news-more-filters')).not.toHaveAttribute('open', '');
  await page.locator('.news-more-filters > summary').click();
  await page.getByLabel('Only stories with an article link', { exact: true }).check();
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  url = new URL(page.url());
  expect(url.searchParams.get('linkedOnly')).toBe('true');
  expect(url.searchParams.get('stockLinkedOnly')).toBeNull();
  await page.getByLabel('Only directly linked stocks', { exact: true }).check();
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  expect(new URL(page.url()).searchParams.get('stockLinkedOnly')).toBe('true');
  await expect(page.locator('#news-live-body')).toContainText('directly linked');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBeTruthy();
});

for (const width of [390, 1440]) test(`Populated News fixture keeps stories and source membership aligned at ${width}px`, async ({ page }) => {
  await page.setViewportSize({ width, height: 1000 });
  await signIn(page);
  const list = await page.evaluate(async () => {
    const response = await window.tradingFlowFetch('/api/v1/wishlists', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: 'Category UI fixture ' + crypto.randomUUID() })
    });
    if (!response.ok) throw Error('Owned fixture wishlist creation failed');
    const created = await response.json();
    for (const ticker of ['UIFIX1', 'UIFIX2']) {
      const added = await window.tradingFlowFetch('/api/v1/wishlists/' + created.id + '/items', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ticker })
      });
      if (!added.ok) throw Error('Owned fixture stock creation failed');
    }
    return created;
  });
  try {
    await page.goto('/News?category=all&stockSource=wishlist:' + list.id);
    await expect(page.locator('.news-more-filters')).not.toHaveAttribute('open', '');
    await page.evaluate(() => {
      const panel = document.querySelector('section[aria-labelledby="news-results-heading"]');
      panel.querySelector('.empty-state')?.remove();
      const timeline = document.createElement('div'); timeline.className = 'news-timeline';
      for (const story of [
        { ticker: 'MARKET', headline: 'UI fixture: conflict disrupts global energy supply', categories: ['global', 'sector'], sectors: ['energy'] },
        { ticker: 'MARKET', headline: 'UI fixture: government announces export restrictions', categories: ['policy', 'sector'], sectors: ['technology'] },
        { ticker: 'UIFIX1', headline: 'UI fixture: company reports quarterly earnings', categories: ['company', 'financial'], sectors: [] }
      ]) timeline.append(window.TradingFlowNews.create({ ...story, timestamp: new Date().toISOString(),
        provider: 'UI fixture', summary: 'Synthetic presentation fixture in the isolated browser test only.',
        categoryRationale: 'Inferred display topic. Direct stock links use provider symbols.', categoryVersion: 'display-topics-v1' }));
      panel.append(timeline);
    });
    await expect(page.locator('[data-news-item]')).toHaveCount(3);
    const feedBox = await page.locator('section[aria-labelledby="news-results-heading"]').boundingBox();
    const sidebarBox = await page.getByRole('complementary', { name: 'Stocks in this source', exact: true }).boundingBox();
    if (width >= 980) {
      expect(sidebarBox.x).toBeGreaterThan(feedBox.x + feedBox.width);
      expect(Math.abs(sidebarBox.y - feedBox.y)).toBeLessThan(2);
    } else expect(sidebarBox.y).toBeGreaterThan(feedBox.y + feedBox.height);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBeTruthy();
    if (process.env.TRADINGFLOW_UI_EVIDENCE_ROOT)
      await page.screenshot({ path: path.join(process.env.TRADINGFLOW_UI_EVIDENCE_ROOT, `news-categories-${width}.png`), fullPage: true });
  } finally {
    await page.evaluate(id => window.tradingFlowFetch('/api/v1/wishlists/' + id, { method: 'DELETE' }), list.id);
  }
});

test('News topic API validation and private scope authorization remain independent', async ({ page, request }) => {
  expect((await request.get('/api/v1/news/feed?category=global&sector=energy')).status()).toBe(200);
  expect((await request.get('/api/v1/news/feed?category=not-a-category')).status()).toBe(400);
  expect((await request.get('/api/v1/news/feed?sector=not-a-sector')).status()).toBe(400);
  expect((await request.get('/api/v1/news/feed?category=global&stockSource=wishlists')).status()).toBe(401);
  const target = '/News?category=global&sector=energy&stockSource=wishlists&hours=4&linkedOnly=true';
  await page.goto(target);
  expect(new URL(page.url()).searchParams.get('ReturnUrl')).toBe(target);
});

test('Shared live article rendering keeps inferred labels separate from direct symbols', async ({ page }) => {
  await signIn(page);
  await page.goto('/News?category=global');
  await page.evaluate(() => {
    const panel = document.querySelector('section[aria-labelledby="news-results-heading"]');
    panel.append(window.TradingFlowNews.create({
      ticker: 'MARKET', timestamp: new Date().toISOString(), headline: 'UI fixture: global energy disruption',
      categories: ['global', 'policy', 'sector'], sectors: ['energy'],
      categoryRationale: 'Inferred display topic; stock impact is not established.', categoryVersion: 'display-topics-v1'
    }));
  });
  const article = page.locator('[data-news-item]').last();
  await expect(article).toContainText('Global events');
  await expect(article).toContainText('Government / policy');
  await expect(article).toContainText('Energy');
  await expect(article.locator('[data-news-tickers]')).toContainText('Broad market');
  await expect(article.getByRole('button', { name: 'Add symbols to group' })).toBeHidden();
});
