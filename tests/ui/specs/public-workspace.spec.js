import { expect, test } from '@playwright/test';

// Signed out, the landing page asks for sign-in and the two public tabs stay reachable.
for (const width of [320, 412, 1280]) {
  test(`public News and Earnings tabs open without signing in at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 915 });
    await page.goto('/');
    await expect(page).toHaveURL(/\/Login\?ReturnUrl=%2FDesk$/);
    const tabs = page.getByRole('navigation', { name: 'Primary navigation' });
    await expect(tabs.getByRole('link')).toHaveText(['News', 'Earnings']);
    await expect(page.getByRole('link', {name:'Sign in', exact:true})).toBeVisible();
    await expect(page.locator('.workspace-sidebar')).toHaveCount(0);
    for (const name of ['News', 'Earnings']) {
      await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('link', { name, exact: true }).click();
      await expect(page).toHaveURL(new RegExp('/' + name + '$'));
      await expect(page.getByRole('navigation', { name:'Primary navigation' }).getByRole('link',{name,exact:true})).toHaveAttribute('aria-current','page');
      await expect(page.getByRole('navigation', { name:'Primary navigation' }).getByRole('link',{name,exact:true})).toHaveAttribute('aria-current','page');
      await expect(page.getByRole('heading', { name: name === 'Earnings' ? 'Earnings monitor' : name, level: 1, exact: true })).toBeVisible();
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBeTruthy();
    }
  });
}

test('anonymous News refuses private group filters without broadening scope', async ({ page, request }) => {
  const group = '11111111-1111-1111-1111-111111111111';
  expect((await page.goto('/News?groupId=' + group)).status()).toBe(200);
  expect(new URL(page.url()).pathname).toBe('/Login');
  expect(new URL(page.url()).searchParams.get('ReturnUrl')).toBe('/News?groupId='+group);
  await expect(page.locator('#GroupId')).toHaveCount(0);
  const feed = await request.get('/api/v1/news/feed?hours=4');
  expect(feed.status()).toBe(200);
  expect((await feed.json()).items).toBeInstanceOf(Array);
  const privateFeed = await request.get('/api/v1/news/latest?configPath=configs/paper/alpaca-paper.yaml');
  expect([302, 401]).toContain(privateFeed.status());
});

test('anonymous provider mutations reject a valid anonymous CSRF token', async ({ request }) => {
  const tokenResponse = await request.get('/api/auth/csrf');
  expect(tokenResponse.status()).toBe(200);
  const { requestToken } = await tokenResponse.json();
  for (const path of ['/api/v1/news/refresh', '/api/earnings/refresh', '/api/v1/earnings/refresh']) {
    const response = await request.post(path, { headers: { 'X-CSRF-TOKEN': requestToken }, maxRedirects: 0 });
    expect([302, 401], path).toContain(response.status());
  }
});

test('anonymous Earnings Refresh only reads stored results', async ({ page }) => {
  const providerPosts = [];
  page.on('request', request => {
    if (request.method() === 'POST' && request.url().includes('/earnings/refresh')) providerPosts.push(request.url());
  });
  await page.goto('/Earnings');
  const refresh = page.locator('#earnings-refresh');
  await expect(refresh).toBeEnabled();
  const loaded = page.waitForResponse(response => response.url().includes('/api/earnings/today-next-business-day') && response.request().method() === 'GET');
  await refresh.click();
  expect((await loaded).status()).toBe(200);
  expect(providerPosts).toEqual([]);
});
