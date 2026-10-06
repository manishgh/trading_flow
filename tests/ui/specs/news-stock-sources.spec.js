import {expect,test} from '@playwright/test';
import {signIn,testUser,testPassword} from './sign-in.js';

async function createWishlist(page) {
 return page.evaluate(async()=>{
  const r=await window.tradingFlowFetch('/api/v1/wishlists',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:'News source '+crypto.randomUUID()})});
  if(!r.ok)throw Error('Wishlist setup failed');return r.json();
 });
}
async function cleanup(page,id) {
 await page.evaluate(id=>window.tradingFlowFetch('/api/v1/wishlists/'+id,{method:'DELETE'}),id);
}

test('private News sign-in returns to the exact requested filters',async({page})=>{
 const target='/News?hours=4&stockSource=wishlists&sentiment=positive&search=earnings&view=ticker&stocksPage=2';
 await page.goto(target);
 expect(new URL(page.url()).searchParams.get('ReturnUrl')).toBe(target);
 await page.locator('#Input_UserName').fill(testUser);
 await page.locator('#Input_Password').fill(testPassword);
 await page.getByRole('button',{name:'Sign in',exact:true}).click();
 expect(new URL(page.url()).pathname+new URL(page.url()).search).toBe(target);
 await expect(page.getByLabel('Stock source',{exact:true})).toHaveValue('wishlists');
});

test('News updates its loaded feed while preserving unsubmitted filters',async({page})=>{
 await signIn(page);await page.clock.install();await page.goto('/News?hours=4');
 let refreshes=0;
 await page.route('**/News?hours=4',async route=>{
  refreshes++;
  const response=await route.fetch();
  await route.fulfill({response,body:(await response.text()).replace('No stories match these filters.','Live-update fixture')});
 });
 await page.clock.fastForward(30000);
 await expect(page.locator('#news-live-status')).toContainText('Updated');
 expect(refreshes).toBe(1);
 await page.getByLabel('Ticker',{exact:true}).fill('AAPL');
 await page.clock.fastForward(30000);
 expect(refreshes).toBe(1);
 await expect(page.getByLabel('Ticker',{exact:true})).toHaveValue('AAPL');
});

test('a slow live response does not replace a feed after a group dialog opens',async({page})=>{
 await signIn(page);await page.clock.install();await page.goto('/News?hours=4');
 await page.evaluate(()=>document.querySelector('[aria-labelledby="news-results-heading"]').append(
  window.TradingFlowNews.create({ticker:'AA',headline:'Group-dialog fixture'})));
 let finish;const hold=new Promise(resolve=>{finish=resolve;});
 let started;const requestStarted=new Promise(resolve=>{started=resolve;});
 await page.route('**/News?hours=4',async route=>{
  const response=await route.fetch();started();await hold;
  await route.fulfill({response,body:(await response.text()).replace('No stories match these filters.','Late-response fixture')});
 });
 await page.clock.fastForward(30000);await requestStarted;
 await page.getByRole('button',{name:'Add symbols to group'}).first().click();
 finish();
 await expect(page.getByRole('dialog')).toBeVisible();
 await expect(page.locator('#news-live-status')).not.toContainText('Updated');
 await expect(page.getByText('Late-response fixture',{exact:true})).toHaveCount(0);
});

for(const width of [390,1440])test('News stock-source dropdown and exact stock filter round-trip at '+width,async({page})=>{
 await page.setViewportSize({width,height:1000});await signIn(page);
 const list=await createWishlist(page);
 try {
  await page.goto('/News?groupId='+list.id+'&hours=24&view=ticker&source=wire&provider=wire&sentiment=positive&search=earnings&ticker=AAPL');
  const dropdown=page.getByLabel('Stock source',{exact:true});
  expect(await dropdown.locator('optgroup').evaluateAll(xs=>xs.map(x=>x.label))).toEqual(['My wishlists','Finviz lists']);
  await expect(dropdown).toHaveValue('wishlist:'+list.id);
  for(const label of ['All news / all stocks','All wishlist stocks','All Finviz stocks','All wishlist + Finviz stocks',list.name,'Top Gainers'])
   await expect(dropdown.getByRole('option',{name:label,exact:true})).toHaveCount(1);
  await dropdown.selectOption('wishlists');
  await page.getByRole('button',{name:'Apply',exact:true}).click();
  const url=new URL(page.url());
  expect(url.searchParams.get('stockSource')).toBe('wishlists');
  expect(url.searchParams.get('groupId')).toBeNull();
  expect(url.searchParams.get('ticker')).toBe('AAPL');
  expect(url.searchParams.get('provider')).toBe('wire');
  expect(url.searchParams.get('source')).toBe('wire');
  await page.reload();await expect(dropdown).toHaveValue('wishlists');
  await expect(page.getByRole('heading',{name:'Stocks in this source',exact:true})).toBeVisible();
  const feedBox=await page.getByRole('region',{name:'Timeline',exact:true}).boundingBox();
  const stocksBox=await page.getByRole('complementary',{name:'Stocks in this source',exact:true}).boundingBox();
  if(width>=980) { expect(stocksBox.x).toBeGreaterThan(feedBox.x+feedBox.width); expect(Math.abs(stocksBox.y-feedBox.y)).toBeLessThan(2); }
  else expect(stocksBox.y).toBeGreaterThan(feedBox.y+feedBox.height);
  await expect(page.getByLabel('News provider',{exact:true})).toHaveValue('wire');
  await expect(page.getByLabel('Publisher / source',{exact:true})).toHaveValue('wire');
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBeTruthy();
  await page.getByRole('link',{name:'Clear',exact:true}).click();
  await expect(dropdown).toHaveValue('all');
 } finally {await cleanup(page,list.id);}
});

test('saved Finviz dropdown and unavailable combined scope never substitute all news',async({page})=>{
 await signIn(page);
 const name='News Finviz '+crypto.randomUUID();
 const screen=await page.evaluate(async name=>{
  const r=await window.tradingFlowFetch('/api/desk/screens',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name,url:'ta_sma50_pa'})});
  if(!r.ok)throw Error('Saved screen setup failed');return r.json();
 },name);
 await page.goto('/News');
 const dropdown=page.getByLabel('Stock source',{exact:true});
 await expect(dropdown.getByRole('option',{name,exact:true})).toHaveAttribute('value',screen.key);
 for(const key of [screen.key,'finviz','wishlists-finviz']) {
  await dropdown.selectOption(key);await page.getByRole('button',{name:'Apply',exact:true}).click();
  await expect(dropdown).toHaveValue(key);
  await expect(page.getByText('News scope unavailable.',{exact:true})).toBeVisible();
  await expect(page.locator('.news-timeline')).toHaveCount(0);
  const api=await page.evaluate(async key=>{const r=await window.tradingFlowFetch('/api/v1/news/feed?stockSource='+encodeURIComponent(key));return {status:r.status,body:await r.json()};},key);
  expect(api.status).toBe(200);expect(api.body.enabled).toBe(false);expect(api.body.items).toEqual([]);
 }
});

test('anonymous stock-source requests require sign-in and deleted or conflicting selections fail explicitly',async({page,request})=>{
 for(const key of ['finviz','wishlists','wishlists-finviz','positions','wishlist:11111111-1111-1111-1111-111111111111']) {
  expect((await request.get('/api/v1/news/feed?stockSource='+key)).status()).toBe(401);
  expect((await page.goto('/News?stockSource='+key)).status()).toBe(200);
  expect(new URL(page.url()).pathname).toBe('/Login');
  expect(new URL(page.url()).searchParams.get('ReturnUrl')).toBe('/News?stockSource='+key);
 }
 expect((await request.get('/api/v1/news/feed?stockSource=all')).status()).toBe(200);
 expect((await request.get('/api/v1/news/feed?groupId=not-a-guid')).status()).toBe(400);
 expect((await page.goto('/News?groupId=not-a-guid')).status()).toBe(400);
 await signIn(page);
 const result=await page.evaluate(async()=>{
  const paths=['/News?stockSource=saved:11111111-1111-1111-1111-111111111111','/News?stockSource=finviz&scope=positions','/api/v1/news/feed?stockSource=typo','/api/v1/news/feed?stockSource=finviz&ticker=AAPL,MSFT'];
  return Promise.all(paths.map(async path=>(await window.tradingFlowFetch(path)).status));
 });
 expect(result).toEqual([400,400,400,400]);
});
