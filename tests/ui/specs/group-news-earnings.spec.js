import { expect, test } from '@playwright/test';
import { signIn } from './sign-in.js';

async function api(page, url, body) {
  return page.evaluate(async ({ url, body }) => {
    const response = await window.tradingFlowFetch(url, { method: 'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(body) });
    if (!response.ok) throw new Error(`Fixture request failed: ${response.status}`);
    return response.json();
  }, { url, body });
}

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
  await expect(page.locator('#groupActionStatus')).not.toHaveText('Loading wishlists…');
  // With no other wishlist the dialog opens New wishlist itself; open it only when it is still closed.
  if (!(await page.locator('#groupActionCreatePanel').evaluate(panel => panel.open))) await page.locator('#groupActionCreatePanel > summary').click();
  await page.locator('#groupActionCreateName').fill('Uncertain destination');
  await page.locator('#groupActionCreate').click();
  await expect(page.locator('#groupActionCreate')).toHaveText('Retry creating this wishlist');
  await page.locator('#groupActionClose').click(); await page.reload();
  await page.locator('#groupActionResume').click();
  await expect(page.locator('#groupActionApply')).toBeDisabled();
  await page.locator('#groupActionCreate').click();
  await expect(page.locator('#groupActionStatus')).toContainText('No stocks added');
  expect(retry).toEqual(first);
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
  await page.getByRole('button', {name:'Copy to…', exact:true}).click();
  // Apply stays unavailable until a destination wishlist is chosen, and the source list is never offered as one.
  await expect(page.locator('#groupActionTarget option')).not.toHaveCount(0);
  await expect(page.locator('#groupActionApply')).toBeDisabled();
  await expect(page.locator('#groupActionSource')).toBeHidden();
  await expect(page.locator(`#groupActionTarget option[value="${source.id}"]`)).toHaveCount(0);
  await page.locator('#groupActionTarget').selectOption(target.id);
  await expect(page.locator('#groupActionApply')).toBeEnabled();
  await page.locator('#groupActionApply').click();
  await expect(page.locator('#groupActionStatus')).toContainText('Saved: 25 stocks changed');
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
  await expect(page.locator('#groupActionTarget option')).toHaveCount(2);
  await page.locator('#groupActionTarget').selectOption('11111111-1111-1111-1111-111111111111');
  await expect(page.locator('#groupActionApply')).toBeEnabled();
  await page.locator('#groupActionApply').click();
  await expect.poll(() => Boolean(first)).toBe(true);
  await page.keyboard.press('Escape'); await expect(page.locator('#groupActionDialog')).toBeVisible();
  release(); await expect(page.locator('#groupActionApply')).toHaveText('Retry the same change');
  await page.locator('#groupActionClose').click();
  await page.reload();
  await page.locator('#groupActionResume').click();
  await page.locator('#groupActionApply').click();
  await expect(page.locator('#groupActionStatus')).toContainText('Saved: 1 stock changed');
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

// The week preset uses New York's week even when UTC and the operator have reached Monday.
test('Earnings This week preserves New York dates and its URL across reload', async ({ page }) => {
  await page.clock.install({ time:new Date('2026-11-02T00:30:00Z') });
  const requests = [];
  const payload = {
    generatedAtUtc:'2026-11-02T00:30:00Z', monitoringActive:false, monitoringIntervalSeconds:60,
    nextBusinessDateLabel:'Monday', newsWindowLabel:'Stored evidence', filters:{session:'all',marketCap:'all'}, news:[],
    items:[item('one','AAA','2026-11-01T00:30:00Z'),item('two','BBB','2026-11-02T00:30:00Z')], totalMatching:2 };
  await page.route('**/api/earnings/today-next-business-day?*', route => route.fulfill({ json:payload }));
  await page.route('**/api/earnings/calendar?*', route => {
    requests.push(new URL(route.request().url()));
    return route.fulfill({ json:payload });
  });
  await page.goto('/Earnings');
  await page.locator('#earnings-this-week').click();
  await expect(page.locator('#earnings-this-week')).toHaveAttribute('aria-current','page');
  await expect.poll(() => requests.length).toBe(1);
  expect(requests[0].searchParams.get('from')).toBe('2026-10-26');
  expect(requests[0].searchParams.get('to')).toBe('2026-11-01');
  const saturday = page.locator('.earnings-day').filter({has:page.getByRole('heading',{name:'Saturday, October 31'})});
  const sunday = page.locator('.earnings-day').filter({has:page.getByRole('heading',{name:'Sunday, November 1'})});
  await expect(saturday).toContainText('AAA'); await expect(sunday).toContainText('BBB');
  expect(new URL(page.url()).search).toBe('?scope=week');
  await page.reload();
  await expect(page.locator('#earnings-this-week')).toHaveAttribute('aria-current','page');
  await expect.poll(() => requests.length).toBe(2);
  expect(requests[1].searchParams.get('from')).toBe('2026-10-26');
  expect(requests[1].searchParams.get('to')).toBe('2026-11-01');
  await expect(saturday).toContainText('AAA'); await expect(sunday).toContainText('BBB');
  await page.setViewportSize({width:320,height:800});
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  const nextDayRequest = page.waitForRequest(request => request.url().includes('/api/earnings/today-next-business-day?') && new URL(request.url()).searchParams.get('scope') === 'nextBusinessDay');
  await page.locator('[data-scope-value="nextBusinessDay"]').click();
  expect(new URL((await nextDayRequest).url()).searchParams.has('from')).toBe(false);
  await expect(page.locator('[data-scope-value="nextBusinessDay"]')).toHaveAttribute('aria-current','page');
});

// Removed query dimensions must disappear from requests and URLs while visible refinements still round-trip.
test('Earnings ignores obsolete custom dates and Evidence controls in saved URLs', async ({ page }) => {
  const removed = ['from','to','view','schedule','result','minMarketCap','maxMarketCap','minEventReturn','maxEventReturn',
    'minPriceReaction','maxPriceReaction','minEpsEstimate','maxEpsEstimate','minEpsActual','maxEpsActual',
    'minRevenueEstimate','maxRevenueEstimate','minRevenueActual','maxRevenueActual','provider','newsProvider',
    'day','breakout','minEpsSurprise','maxEpsSurprise','minRevenueSurprise','maxRevenueSurprise','minSentiment','maxSentiment','onlyGroups','groupId'];
  const supported = {session:'afterMarketClose',marketCap:'2bPlus',sector:'Technology',onlyHeld:'true',hasNews:'true',minSurprise:'5',sort:'cap',assessment:'beat',search:'AA'};
  const query = new URLSearchParams(supported);
  for (const key of removed) query.set(key, key === 'from' || key === 'to' ? '2026-11-01' : key === 'view' ? 'week' : 'legacy');
  const requests = [];
  await page.route('**/api/earnings/**', route => {
    requests.push(new URL(route.request().url()));
    return route.fulfill({json:{generatedAtUtc:'2026-11-01T12:00:00Z',monitoringActive:false,monitoringIntervalSeconds:60,
      nextBusinessDateLabel:'Monday',newsWindowLabel:'Stored evidence',items:[item('one','AA','2026-11-01T07:30:00Z')],news:[],totalMatching:1,
      filters:{session:'afterMarketClose',marketCap:'2bPlus',sectors:[{value:'all',label:'All sectors',count:1},{value:'Technology',label:'Technology',count:1}]}}});
  });
  await page.goto('/Earnings?'+query);
  await expect(page.locator('#earnings-count')).toContainText('1 of 1');
  await expect(page.locator('.market-more-filters')).toHaveJSProperty('open',true);
  await expect(page.locator('#earnings-advanced, #earnings-from, #earnings-to, #earnings-range-apply, #earnings-range-clear')).toHaveCount(0);
  await expect(page.getByText('Evidence & display',{exact:true})).toHaveCount(0);
  await expect(page.locator('#earnings-search')).toHaveValue('AA');
  await expect(page.locator('#earnings-sector')).toHaveValue('Technology');
  await expect(page.locator('#earnings-min-surprise')).toHaveValue('5');
  await expect(page.locator('#earnings-sort')).toHaveValue('cap');
  await expect(page.locator('#earnings-only-held')).toHaveAttribute('aria-pressed','true');
  await expect(page.locator('#earnings-has-news')).toHaveAttribute('aria-pressed','true');
  await expect(page.locator('[data-assessment-value="beat"]')).toHaveAttribute('aria-current','page');
  await expect(page.locator('[data-scope-value="today"]')).toHaveAttribute('aria-current','page');
  for (const key of removed) {
    expect(requests[0].searchParams.has(key),key+' API query').toBe(false);
    expect(new URL(page.url()).searchParams.has(key),key+' page query').toBe(false);
    await expect(page.locator('input[name="'+key+'"], select[name="'+key+'"]')).toHaveCount(0);
  }
  for (const [key,value] of Object.entries(supported)) expect(requests[0].searchParams.get(key),key).toBe(value);
  await page.reload();
  await expect(page.locator('#earnings-count')).toContainText('1 of 1');
  for (const [key,value] of Object.entries(supported)) expect(new URL(page.url()).searchParams.get(key),key).toBe(value);
  expect(requests.every(request => request.pathname.endsWith('/today-next-business-day'))).toBe(true);
});

// The Today heading names today's New York date from the response, and a refresh patches cards in place.
test('Earnings updates cards in place without dimming the list, under today’s New York date', async ({ page }, testInfo) => {
  const payload = appActual => ({ generatedAtUtc:'2026-11-04T18:00:00Z', monitoringActive:true, monitoringIntervalSeconds:60,
    nextBusinessDateLabel:'Thursday, November 5', newsWindowLabel:'Stored evidence', filters:{session:'all',marketCap:'all'}, news:[], totalMatching:2,
    items:[{...item('amd','AMD','2026-11-04T12:30:00Z'), dayGroup:'today', epsEstimate:1.95, epsActual:2.09},
      {...item('app','APP','2026-11-04T21:30:00Z'), dayGroup:'today', epsEstimate:4.03, epsActual:appActual}] });
  let calls = 0, release; const gate = new Promise(resolve => { release = resolve; });
  await page.route('**/api/earnings/today-next-business-day?*', async route => {
    if (++calls === 1) return route.fulfill({ json:payload(null) });
    await gate; await route.fulfill({ json:payload(4.3) }).catch(() => {});
  });
  await page.goto('/Earnings');
  const heading = page.locator('.earnings-day-head h2');
  await expect(heading).toHaveCount(1);
  await expect(heading).toContainText('Nov 04, 2026');
  await expect(page.locator('.earnings-day-summary')).toHaveText('2 of 2 events · 1 reported');
  const card = ticker => page.locator('.earnings-event').filter({ has:page.getByRole('heading', { name:ticker, exact:true }) });
  await card('APP').getByText('Provenance and previous quarter').click();
  await card('AMD').evaluate(node => { node.dataset.sameNode = 'yes'; });

  await page.locator('#earnings-refresh').click();
  await expect(page.locator('#earnings-spinner')).toBeVisible();
  expect(await page.locator('.earnings-body').evaluate(node => [getComputedStyle(node).opacity, getComputedStyle(node).pointerEvents])).toEqual(['1', 'auto']);
  await card('AMD').getByText('Provenance and previous quarter').click();
  await expect(card('AMD').locator('details')).toHaveAttribute('open', '');

  release();
  await expect(card('APP')).toContainText('4.30');
  await expect(page.locator('#earnings-spinner')).toBeHidden();
  await expect(card('AMD')).toHaveAttribute('data-same-node', 'yes');
  await expect(card('AMD').locator('details')).toHaveAttribute('open', '');
  await expect(card('APP').locator('details')).toHaveAttribute('open', '');
  await expect(heading).toContainText('Nov 04, 2026');
  // Capture the populated workspace after the refresh contract has been verified.
  await card('AMD').getByText('Provenance and previous quarter').click();
  await card('APP').getByText('Provenance and previous quarter').click();
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    await page.screenshot({ path: testInfo.outputPath(`earnings-workspace-${width}.png`), fullPage: true });
  }
});

test('shared News preserves safe provenance and Earnings scopes exact symbols',async({page})=>{
  await signIn(page);
  await page.route('**/api/earnings/today-next-business-day?*',route=>route.fulfill({json:{generatedAtUtc:'2026-11-01T12:00:00Z',monitoringActive:false,monitoringIntervalSeconds:60,filters:{session:'all',marketCap:'all'},
    items:[item('aa','AA','2026-11-01T07:30:00Z'),item('aaa','AAA','2026-11-01T08:30:00Z')],totalMatching:2,
    news:[{tickers:'AA, BBB',publishedAtUtc:'2026-11-01T07:00:00Z',headline:'Exact AA report',url:'https://example.org/aa',sentimentScore:.2,provider:'wire',source:'release',category:'Company',whyItMatters:'Reported evidence for AA'},
      {tickers:'AAA',publishedAtUtc:'2026-11-01T07:05:00Z',headline:'Different AAA report',url:'javascript:alert(1)',sentimentScore:-.3,provider:'wire',category:'Company',summary:'<script>unsafe</script>'}]} }));
  await page.goto('/Earnings?search=AA');
  await expect(page.locator('#earnings-news-feed [data-news-item]')).toHaveCount(2);
  await expect(page.locator('#earnings-news-feed script')).toHaveCount(0);
  await expect(page.locator('#earnings-news-feed a[href^="javascript:"]')).toHaveCount(0);
  await page.locator('.earnings-event').filter({has:page.getByRole('heading',{name:'AA',exact:true})}).getByRole('button',{name:'Show evidence'}).click();
  await expect(page.locator('#earnings-news-count')).toHaveText('1');
  await expect(page.locator('#earnings-news-feed')).toContainText('Exact AA report');
  await expect(page.locator('#earnings-news-feed')).not.toContainText('Different AAA report');
  await expect(page.locator('#earnings-news-feed')).toContainText('Reported evidence for AA');
  await page.locator('#earnings-news-feed').getByRole('button',{name:'Add symbols to group'}).click();
  await expect(page.locator('#groupActionSummary')).toContainText('AA, BBB');
  await page.locator('#groupActionClose').click();
  await expect(page.locator('#earnings-search')).toHaveValue('AA');
  await expect(page.locator('#earnings-schedule, #earnings-minEventReturn')).toHaveCount(0);
  const groupability=await page.evaluate(()=>{
    const market=window.TradingFlowNews.create({ticker:'MARKET',headline:'Fed update'}),mixed=window.TradingFlowNews.create({ticker:'MARKET, AA',headline:'Mixed update'});
    const marketHidden=market.querySelector('[data-group-action]').hidden;
    window.TradingFlowNews.patch(market,{ticker:'MARKET, AA',headline:'Fed update'});
    const revealed=!market.querySelector('[data-group-action]').hidden && market.querySelector('[data-group-action]').dataset.tickers==='AA';
    window.TradingFlowNews.patch(market,{ticker:'MARKET',headline:'Fed update'});
    return {marketHidden,mixed: mixed.querySelector('[data-group-action]').dataset.tickers,revealed,hiddenAgain:market.querySelector('[data-group-action]').hidden};
  });
  expect(groupability).toEqual({marketHidden:true,mixed:'AA',revealed:true,hiddenAgain:true});
});

// Earnings news that arrives while the page is open flashes once; stories already shown do not.
test('a new earnings story flashes when it arrives and older stories do not', async ({ page }) => {
  await page.clock.install();
  let calls = 0;
  const story = (headline, at) => ({ publishedAtUtc:at, tickers:'AAA', category:'Company', sentimentScore:.4, sentimentLabel:'Bullish',
    headline, summary:'', provider:'alpaca', source:'Benzinga', url:'https://example.org/'+encodeURIComponent(headline), whyItMatters:'Result news' });
  await page.route('**/api/earnings/today-next-business-day?*', route => {
    calls++;
    const news = [story('AAA beats estimates','2026-11-04T12:00:00Z')];
    if (calls > 1) news.unshift(story('AAA raises guidance','2026-11-04T12:05:00Z'));
    return route.fulfill({ json:{ generatedAtUtc:'2026-11-04T12:06:00Z', monitoringActive:true, monitoringIntervalSeconds:60,
      nextBusinessDateLabel:'Thursday', newsWindowLabel:'Latest 24 hours', filters:{session:'all',marketCap:'all',sessions:[],marketCaps:[],sectors:[]},
      news, items:[item('one','AAA','2026-11-04T12:00:00Z')], totalMatching:1 } });
  });
  await page.goto('/Earnings');
  await expect(page.locator('#earnings-news-feed')).toContainText('AAA beats estimates');
  await expect(page.locator('.earnings-news-flash')).toHaveCount(0);
  await page.clock.fastForward(11000);
  await expect(page.locator('#earnings-news-feed')).toContainText('AAA raises guidance');
  await expect(page.locator('.earnings-news-flash')).toHaveCount(1);
  await expect(page.locator('.earnings-news-flash')).toContainText('NEW');
  await expect(page.locator('.earnings-news-flash')).toContainText('AAA raises guidance');
});
