import { expect, test } from '@playwright/test';
import { signIn } from './sign-in.js';

async function api(page, url, body) {
  return page.evaluate(async ({ url, body }) => {
    const response = await window.tradingFlowFetch(url, { method: 'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(body) });
    if (!response.ok) throw new Error(`Fixture request failed: ${response.status}`);
    return response.json();
  }, { url, body });
}

test('inline destination creation is separate from membership and both changes can be undone', async ({ page, request }) => {
  expect((await request.post('/api/v1/wishlists/create', {data:{commandId:crypto.randomUUID(),name:'Anonymous'}})).status()).toBe(401);
  await signIn(page);
  const source = await api(page, '/api/v1/wishlists', {name:`Inline source ${Date.now()}`});
  let createdId;
  try {
    await api(page, '/api/v1/wishlists/bulk', {commandId:crypto.randomUUID(),operation:'add',targetId:source.id,tickers:['AAA','BBB']});
    await page.goto(`/TradeDesk?id=${source.id}&view=market`);
    await page.locator('[data-bulk-item][value="AAA"]').check();
    await page.getByRole('button',{name:'Copy to group',exact:true}).click();
    await expect(page.locator('#groupActionApply')).toBeEnabled();
    await page.locator('#groupActionCreateName').fill(`Inline target ${Date.now()}`);
    await page.locator('#groupActionCreate').click();
    await expect(page.locator('#groupActionStatus')).toContainText('No symbols added');
    createdId = await page.locator('#groupActionTarget').inputValue();
    const read = () => page.evaluate(async id => (await window.tradingFlowFetch(`/api/v1/wishlists/${id}`)).json(), createdId);
    expect((await read()).items).toHaveLength(0);
    await expect(page.locator('#groupActionSource')).toHaveValue(source.id);
    await page.locator('#groupActionApply').click();
    await expect(page.locator('#groupActionStatus')).toContainText('1 membership changes saved');
    expect((await read()).items.map(item => item.ticker)).toEqual(['AAA']);
    await page.locator('#groupActionCreateUndo').click();
    await expect(page.locator('#groupActionStatus')).toContainText('changed');
    expect((await read()).items).toHaveLength(1);
    await page.locator('#groupActionUndo').click();
    await expect(page.locator('#groupActionStatus')).toContainText('Change undone');
    await page.locator('#groupActionCreateUndo').click();
    await expect(page.locator('#groupActionStatus')).toContainText('Empty group creation undone');
  } finally {
    await page.evaluate(async ids => {for (const id of ids.filter(Boolean)) await window.tradingFlowFetch(`/api/v1/wishlists/${id}`,{method:'DELETE'});}, [source.id,createdId]);
  }
});

test('uncertain empty creation retains its identity over reload without applying membership', async ({ page }) => {
  await signIn(page);
  await page.goto('/Wishlists');
  await page.evaluate(() => {
    const button=document.createElement('button'); button.textContent='Create fixture destination';
    button.dataset.groupAction='add'; button.dataset.tickers='AAA'; document.querySelector('main').append(button);
  });
  let first, retry;
  await page.route('**/api/v1/wishlists/create', async route => {
    const body=route.request().postDataJSON();
    if (!first) {first=body; await route.abort();}
    else {retry=body; await route.fulfill({json:{id:body.commandId,name:body.name,receipt:{commandId:body.commandId,affected:1,undone:false}}});}
  });
  await page.getByRole('button',{name:'Create fixture destination'}).click();
  await expect(page.locator('#groupActionStatus')).not.toHaveText('Loading groups…');
  await page.locator('#groupActionCreateName').fill('Uncertain destination');
  await page.locator('#groupActionCreate').click();
  await expect(page.locator('#groupActionCreate')).toHaveText('Retry exact empty group creation');
  await page.locator('#groupActionClose').click(); await page.reload();
  await page.locator('#groupActionResume').click();
  await expect(page.locator('#groupActionApply')).toBeDisabled();
  await page.locator('#groupActionCreate').click();
  await expect(page.locator('#groupActionStatus')).toContainText('No symbols added');
  expect(retry).toEqual(first);
});

test('selected symbol group action ignores grid selection and changes nothing before Apply', async ({ page }) => {
  await signIn(page);
  const nonce = `${Date.now()}`;
  const source = await api(page, '/api/v1/wishlists', { name:`Rail source ${nonce}` });
  const target = await api(page, '/api/v1/wishlists', { name:`Rail target ${nonce}` });
  try {
    await api(page, '/api/v1/wishlists/bulk', { commandId:crypto.randomUUID(), operation:'add', targetId:source.id, tickers:['RAILX','OTHERX'] });
    await api(page, `/api/v1/wishlists/${target.id}/items`, { ticker:'KEEPX', displayName:'Existing symbol', notes:'Keep these notes' });
    const read = () => page.evaluate(async id => (await window.tradingFlowFetch(`/api/v1/wishlists/${id}`)).json(), target.id);
    const before = await read();
    await page.goto(`/TradeDesk?view=market&id=${source.id}&ticker=RAILX`);
    await page.locator('[data-bulk-item][value="OTHERX"]').check();
    const action = page.locator('#symbol-detail').getByRole('button', {name:'Add RAILX to group',exact:true});
    await action.click();
    await expect(page.locator('#groupActionApply')).toBeEnabled();
    await expect(page.locator('#groupActionSummary')).toContainText('add: RAILX');
    await expect(page.locator('#groupActionSummary')).not.toContainText('OTHERX');
    await page.locator('#groupActionTarget').selectOption(target.id);
    await page.locator('#groupActionClose').click();
    expect((await read()).items).toEqual(before.items);
    await action.click();
    await expect(page.locator('#groupActionApply')).toBeEnabled();
    await page.locator('#groupActionTarget').selectOption(target.id);
    const issued = page.waitForRequest(request => request.url().endsWith('/api/v1/wishlists/bulk') && request.method() === 'POST');
    await page.locator('#groupActionApply').click();
    expect((await issued).postDataJSON().tickers).toEqual(['RAILX']);
    await expect(page.locator('#groupActionStatus')).toContainText('1 membership changes saved');
    const changed = await read();
    expect(changed.items.map(item => item.ticker).sort()).toEqual(['KEEPX','RAILX']);
    expect(changed.items.find(item => item.ticker === 'KEEPX').notes).toBe('Keep these notes');
    await page.locator('#groupActionUndo').click();
    await expect(page.locator('#groupActionStatus')).toContainText('Change undone');
    expect((await read()).items).toEqual(before.items);
  } finally {
    await page.evaluate(async ids => { for (const id of ids) await window.tradingFlowFetch(`/api/v1/wishlists/${id}`, {method:'DELETE'}); }, [source.id,target.id]);
  }
});

test('25-symbol copy uses one reviewed command and can undo', async ({ page }) => {
  await signIn(page);
  const nonce = `${Date.now()}`;
  const source = await api(page, '/api/v1/wishlists', { name:`Bulk source ${nonce}` });
  const target = await api(page, '/api/v1/wishlists', { name:`Bulk target ${nonce}` });
  await api(page, '/api/v1/wishlists/bulk', { commandId:crypto.randomUUID(), operation:'add', targetId:source.id, tickers:Array.from({length:25},(_,i)=>`T${i}`) });
  await page.goto(`/Wishlists?id=${source.id}`);
  await expect(page.locator('#editMembershipForm')).toHaveCount(1);
  await expect(page.locator('form[id^=edit_]')).toHaveCount(0);
  await page.locator('[data-bulk-toggle-all]').check();
  await page.getByRole('button', {name:'Copy to group', exact:true}).click();
  await expect(page.locator('#groupActionApply')).toBeEnabled();
  await page.locator('#groupActionTarget').selectOption(target.id);
  await page.locator('#groupActionApply').click();
  await expect(page.locator('#groupActionStatus')).toContainText('25 membership changes saved');
  await page.locator('#groupActionUndo').click();
  await expect(page.locator('#groupActionStatus')).toContainText('Change undone');
  const result = await page.evaluate(async id => (await window.tradingFlowFetch(`/api/v1/wishlists/${id}`)).json(), target.id);
  expect(result.items).toHaveLength(0);
  await page.evaluate(async ids => { for (const id of ids) await window.tradingFlowFetch(`/api/v1/wishlists/${id}`, {method:'DELETE'}); }, [source.id,target.id]);
});

test('uncertain group changes retain command identity across close and reload', async ({ page }) => {
  await signIn(page);
  await page.route('**/api/v1/wishlists/mutable', route => route.fulfill({json:[{ id:'11111111-1111-1111-1111-111111111111', name:'Fixture' }]}));
  await page.goto('/Wishlists');
  await page.evaluate(() => {
    const button = document.createElement('button'); button.textContent = 'Fixture group change';
    button.dataset.groupAction = 'add'; button.dataset.tickers = 'AAA'; document.querySelector('main').append(button);
  });
  let first, retry, release;
  const held = new Promise(resolve => { release = resolve; });
  await page.route('**/api/v1/wishlists/bulk', async route => {
    const request = route.request().postDataJSON();
    if (!first) { first = request; await held; await route.abort(); }
    else { retry = request; await route.fulfill({json:{commandId:request.commandId, affected:1, undone:false}}); }
  });
  await page.getByRole('button',{name:'Fixture group change'}).click();
  await expect(page.locator('#groupActionApply')).toBeEnabled();
  await page.locator('#groupActionTarget').selectOption('11111111-1111-1111-1111-111111111111');
  await page.locator('#groupActionApply').click();
  await expect.poll(() => Boolean(first)).toBe(true);
  await page.keyboard.press('Escape'); await expect(page.locator('#groupActionDialog')).toBeVisible();
  release(); await expect(page.locator('#groupActionApply')).toHaveText('Retry the same change');
  await page.locator('#groupActionClose').click();
  await page.reload();
  await page.locator('#groupActionResume').click();
  await page.locator('#groupActionApply').click();
  await expect(page.locator('#groupActionStatus')).toContainText('1 membership changes saved');
  expect(retry).toEqual(first);
});

test('public News protects private scopes and validates filters', async ({ page, request }) => {
  expect((await request.get('/api/v1/news/feed?scope=Positions')).status()).toBe(401);
  expect((await request.get('/api/v1/news/feed?sentiment=typo')).status()).toBe(400);
  await page.goto('/News?view=ticker&source=wire&classification=Company&pageNumber=999999');
  await expect(page.locator('#NewsSource')).toHaveValue('wire');
  await expect(page.locator('#NewsClassification')).toHaveValue('Company');
  await expect(page.locator('#groupActionDialog')).toHaveCount(0);
  await expect(page.getByRole('navigation',{name:'News pages'})).toContainText('Page 1 of 1');
});

const item = (id, ticker, time) => ({ id, ticker, companyName:ticker, scheduledAtUtc:time, dayGroup:'other',
  releaseWindowLabel:'After market · estimated', isScheduleEstimate:true, epsOutcome:'Pending',
  breakoutAssessment:'AwaitingEarningsRelease', provider:'calendar', analysisReason:'Awaiting reported evidence',
  providerReceivedAtUtc:'2026-11-01T00:00:00Z' });

test('Earnings calendar uses New York dates and filter URLs survive reload', async ({ page }) => {
  await page.route('**/api/earnings/calendar?*', route => route.fulfill({ json:{
    generatedAtUtc:'2026-11-01T12:00:00Z', monitoringActive:false, monitoringIntervalSeconds:60,
    nextBusinessDateLabel:'Monday', newsWindowLabel:'Stored evidence', filters:{session:'all',marketCap:'all'}, news:[],
    items:[item('one','AAA','2026-11-01T00:30:00Z'),item('two','BBB','2026-11-01T07:30:00Z')], totalMatching:2 } }));
  await page.goto('/Earnings?from=2026-10-31&to=2026-11-01&view=week&minRvol=1.5&breakout=AwaitingEarningsRelease');
  await expect(page.locator('#earnings-feed')).toHaveClass(/earnings-week/);
  const saturday = page.locator('.earnings-day').filter({has:page.getByRole('heading',{name:'2026-10-31 · New York'})});
  const sunday = page.locator('.earnings-day').filter({has:page.getByRole('heading',{name:'2026-11-01 · New York'})});
  await expect(saturday).toContainText('AAA'); await expect(sunday).toContainText('BBB');
  await page.getByText('More earnings filters and views',{exact:true}).click();
  await expect(page.locator('#earnings-minRvol')).toHaveValue('1.5');
  await expect(page.locator('#earnings-breakout')).toHaveValue('AwaitingEarningsRelease');
  await page.reload();
  await expect(page.locator('#earnings-feed')).toHaveClass(/earnings-week/);
  await page.setViewportSize({width:320,height:800});
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
