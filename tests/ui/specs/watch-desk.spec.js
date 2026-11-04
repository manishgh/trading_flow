import {expect,test} from '@playwright/test';
import {signIn,signInAsAdmin} from './sign-in.js';
import http from 'node:http';
import {randomUUID} from 'node:crypto';
const id='11111111-1111-1111-1111-111111111111';
const sources=[{key:'all',name:'All wishlist stocks',group:'',description:'Across your wishlists'},{key:'wishlist:'+id,name:'Core',group:'My wishlists',description:'Observed'},{key:'swing:trend',name:'Established trends',group:'Finviz lists',description:'Discovery only'},{key:'saved:one',name:'My Elite momentum',group:'Finviz lists',description:'Locally imported'}];
function row(symbol,{held=false,stale=false,followed=true}={}){return {membership:{symbol,name:symbol==='AAPL'?'Apple Inc.':symbol,wishlists:followed?[{id,name:'Core',observed:true}]:[]},quote:{midPrice:200,bidPrice:199.95,askPrice:200.05,timestamp:new Date(Date.now()-(stale?3600000:2000)).toISOString()},quoteFreshness:stale?'stale':'current',previousClose:{close:198,sessionDate:'2026-09-24'},technical:{rsi:58.2,ema20:198,ema50:190,ema200:180,atr:4.2,relativeVolume:1.3,timeframe:'1d',timestamp:'2026-09-25T20:00:00Z',dataFeed:'sip'},observation:{state:'ready',detail:'260 completed daily sessions',evaluation:{shouldAlert:false,reason:'Participation below watch threshold',sourceBarTimestampUtc:'2026-09-25T20:00:00Z'}},holding:held?{symbol,quantity:10,averageEntry:180,costBasis:1800,marketValue:2000,unrealizedPl:200}:null,holdingsKnown:true,news:[{headline:symbol+' company update',url:'https://example.org/news/'+symbol,provider:'Fixture news',symbols:[symbol],publishedAtUtc:new Date().toISOString()}]};}
function snapshot(source){const rows=source==='all'?[row('AAPL',{held:true}),row('MSFT',{stale:true}),row('NVDA')]:[row('TSLA',{followed:false})];return {selection:{source:sources.find(x=>x.key===source)||sources[0],totalCount:rows.length,screenedAtUtc:source==='all'?null:new Date().toISOString(),error:null},rows,account:{available:true,detail:'Alpaca paper account · broker unrealized P&L',readAtUtc:new Date().toISOString()},news:rows.flatMap(x=>x.news),newsCoverage:'Matched stored news · historical coverage may be incomplete.',session:'Regular',readAtUtc:new Date().toISOString(),feed:'SIP'};}
let fixtureServer,fixtureUrl,paused=false;const connections=new Set();
test.beforeAll(async()=>{
 fixtureServer=http.createServer((req,res)=>{res.writeHead(200,{'Content-Type':'text/event-stream','Cache-Control':'no-store','Access-Control-Allow-Origin':'*'});res.flushHeaders();connections.add(res);
 const url=new URL(req.url,'http://fixture');const send=()=>{if(paused)return;const value=snapshot(url.searchParams.get('source'));const search=(url.searchParams.get('search')||'').toUpperCase();value.rows=value.rows.filter(x=>(x.membership.symbol+' '+x.membership.name).toUpperCase().includes(search));value.selection.matchedCount=value.rows.length;value.selection.page=0;value.selection.pageCount=1;res.write('event: snapshot\ndata: '+JSON.stringify(value)+'\n\n');};send();const timer=setInterval(send,5000);req.on('close',()=>{clearInterval(timer);connections.delete(res);});
 });await new Promise(resolve=>fixtureServer.listen(0,'127.0.0.1',resolve));fixtureUrl='http://127.0.0.1:'+fixtureServer.address().port;
});
test.afterAll(async()=>{for(const res of connections)res.end();fixtureServer.closeAllConnections();await new Promise(resolve=>fixtureServer.close(resolve));});
async function fixture(page){paused=false;await page.route('**/api/desk/sources',r=>r.fulfill({json:sources}));await page.addInitScript(url=>{const Native=window.EventSource;window.EventSource=class extends Native{constructor(path,options){super(path.startsWith('/api/desk/stream?')?url+path:path,options);}};},fixtureUrl);}

for(const width of [412,1280,1600])test('stock desk is readable and focused at '+width,async({page},testInfo)=>{
 await page.setViewportSize({width,height:1000});await fixture(page);await signIn(page);await page.goto('/');await expect(page).toHaveURL(/Desk/);
 await expect(page.locator('#watch-rows tr')).toHaveCount(3);await expect(page.getByRole('heading',{level:1})).toHaveText('Desk');
 await expect(page.getByRole('navigation',{name:'Primary navigation'}).getByRole('link')).toHaveText(['News','Earnings','Trading Desk']);
 await expect(page.locator('#watch-rows tr[data-symbol=AAPL]')).toContainText('$200.05');await expect(page.locator('#watch-rows tr[data-symbol=AAPL]')).toContainText('$199.95');
 // Any cell selects its row, not only the symbol button.
 await page.locator('#watch-rows tr[data-symbol=NVDA] td').nth(4).click();await expect(page.locator('#watch-symbol')).toHaveText('NVDA');
 expect(await page.locator('#watch-source optgroup').evaluateAll(xs=>xs.map(x=>x.label))).toEqual(['My wishlists','Finviz lists']);
 await expect(page.locator('#watch-rows tr[data-symbol=MSFT]')).toContainText('Delayed · 1h ago');
 await page.locator('#watch-rows button').filter({hasText:'AAPL'}).click();await expect(page.locator('#watch-symbol')).toHaveText('AAPL');
 // With no strategy chosen the strategy block stays hidden instead of repeating "select a strategy".
 await expect(page.locator('#watch-strategy-block')).toBeHidden();await expect(page.locator('#watch-strategy-checks')).toBeEmpty();
 await expect(page.locator('#watch-prev-close')).toHaveText('Prev close $198.00 · +1.01%');
 await expect(page.locator('#watch-quantity')).toHaveText('10');await expect(page.locator('#watch-pl')).toContainText('200.00');
 // Without a strategy signal a non-administrator has no buy; the Desk says how to get one.
 await expect(page.locator('#watch-buy')).toBeHidden();await expect(page.locator('#watch-order-help')).toContainText('Start a paper strategy run');
 expect((await page.locator('#watch-sell').boundingBox()).height).toBeLessThan(80);
 await expect(page.locator('#watch-news-title')).toHaveText('AAPL news');await expect(page.locator('#watch-preparation')).not.toHaveAttribute('open','');
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBeTruthy();
 await page.screenshot({path:testInfo.outputPath('stock-desk-'+width+'.png'),fullPage:true});
});
test('source switch discards old rows and screener-only stocks require following',async({page})=>{
 await fixture(page);await signIn(page);await page.goto('/Desk');await expect(page.locator('#watch-rows tr')).toHaveCount(3);
 await page.locator('#watch-source').selectOption('saved:one');await expect(page.locator('#watch-rows tr')).toHaveCount(1);await expect(page.locator('#watch-rows')).not.toContainText('AAPL');
 await page.locator('#watch-rows .watch-symbol-button').click();await expect(page.locator('#watch-buy')).toBeHidden();await expect(page.locator('#watch-sell')).toBeHidden();await expect(page.locator('#watch-follow')).toBeVisible();
 await page.locator('#watch-search').fill('does-not-exist');await expect(page.locator('#watch-empty')).toContainText('No matching stocks');
 await page.locator('#watch-search').clear();await expect(page.locator('#watch-rows tr')).toHaveCount(1);
});

test('uncertain strategy response retries the original key before an explicit completed refresh',async({page})=>{
 await fixture(page);await signIn(page);
 const strategy={strategyId:'fixture.selected',semanticVersion:'1.0.0',contentSha256:'a'.repeat(64)};
 const keys=[],accepted=new Map();
 await page.route('**/api/v1/desk/strategy-choices',route=>route.fulfill({json:[{candidateRunId:null,strategy,name:'Fixture selected strategy',mode:'current',runStartedAtUtc:null}]}));
 await page.route(/\/api\/v1\/desk\/strategy-assessments(?:\/.*)?$/,async route=>{
  const request=route.request();
  if(request.method()==='POST'&&!request.url().endsWith('/cancel')){
   const body=request.postDataJSON();keys.push(body.idempotencyKey);
   if(!accepted.has(body.idempotencyKey))accepted.set(body.idempotencyKey,{contractVersion:'1.0',jobId:randomUUID(),status:'queued',detail:'Fixture queued',strategy,profileSha256:'b'.repeat(64),createdAtUtc:new Date().toISOString(),startedAtUtc:null,finishedAtUtc:null,result:null});
   if(keys.length===1)return route.abort('failed');
   return route.fulfill({json:accepted.get(body.idempotencyKey)});
  }
  const job=[...accepted.values()].find(job=>request.url().includes(job.jobId));
  if(!job)return route.fulfill({status:404,json:{message:'Unknown fixture job'}});
  job.status='completed';job.detail='Fixture complete';job.finishedAtUtc=new Date().toISOString();
  job.result={contractVersion:'1.0',candidateRunId:null,strategy,readAtUtc:new Date().toISOString(),stocks:['AAPL','MSFT','NVDA'].map(symbol=>({symbol,strategy,candidateRunId:null,candidateId:null,state:'evaluated',detail:'Fixture completed checks',evaluatedAtUtc:new Date().toISOString(),latestCompletedCandleAtUtc:new Date().toISOString(),expired:false,checks:[]}))};
  return route.fulfill({json:job});
 });
 await page.goto('/Desk');await expect(page.locator('#watch-rows tr')).toHaveCount(3);
 await page.locator('#watch-strategy').selectOption('0');
 await expect(page.locator('#watch-strategy-status')).toContainText('failed',{ignoreCase:true});
 await page.locator('#watch-strategy-refresh').click();
 await expect.poll(()=>keys.length).toBe(2);expect(keys[0]).toBe(keys[1]);expect(accepted.size).toBe(1);
 await expect(page.locator('#watch-strategy-status')).toContainText('completed',{timeout:12000});
 await page.locator('#watch-strategy-refresh').click();
 await expect.poll(()=>keys.length).toBe(3);expect(keys[2]).not.toBe(keys[0]);expect(accepted.size).toBe(2);
});
test('real source API accepts sanitized Elite filters and rejects missing CSRF',async({page})=>{
 await signIn(page);await page.goto('/Desk');await expect(page.locator('#watch-source')).toContainText('All wishlist stocks');await expect(page.locator('#watch-connection')).toContainText(/current quotes|no stocks in this view/);
 const result=await page.evaluate(async()=>{const csrf=await fetch('/api/auth/csrf').then(r=>r.json());const name='UI saved '+crypto.randomUUID();const payload={name,url:'https://elite.finviz.com/screener.ashx?f=ta_sma50_pa&auth=not-a-real-secret'};const rejected=await fetch('/api/desk/screens',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});const saved=await fetch('/api/desk/screens',{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':csrf.requestToken},body:JSON.stringify(payload)});const entry=await saved.json();const sources=await fetch('/api/desk/sources').then(r=>r.json());return {rejected:rejected.status,saved:saved.status,screen:sources.find(x=>x.key===entry.key)};});
 expect(result.rejected).toBe(400);expect(result.saved).toBe(200);expect(result.screen.query).toBe('ta_sma50_pa');expect(result.screen.group).toBe('Finviz lists');
});
test('preparation opens within desk and retains its real controls',async({page})=>{
 await fixture(page);await signIn(page);await page.goto('/Desk');await page.locator('#watch-preparation>summary').click();
 await expect(page.locator('#prep-status')).not.toHaveText('Loading workflowsâ€¦');await expect(page.locator('#prep-new')).toBeVisible();await expect(page).toHaveURL(/Desk/);
});

test('disconnect marks account observations stale and disables sell',async({page})=>{
 await fixture(page);await signIn(page);await page.goto('/Desk');await page.getByRole('button',{name:'AAPL',exact:true}).click();await expect(page.locator('#watch-sell')).toBeVisible();
 paused=true;for(const res of connections)res.end();await expect(page.locator('#watch-quantity')).toHaveText('Stale snapshot');await expect(page.locator('#watch-holding-block')).toHaveAttribute('title',/^Stale/);await expect(page.locator('#watch-sell')).toBeHidden();await expect(page.locator('#watch-rows tr[data-symbol=AAPL]')).toContainText('Stale holding');
});
test('catalog startup failure offers working retry',async({page})=>{
 await fixture(page);await signIn(page);await page.goto('/Orders');
 let first=true;
 await page.route('**/api/desk/sources',r=>{if(first){first=false;return r.fulfill({status:503,body:'Unavailable'});}return r.fulfill({json:sources});});
 await page.goto('/Desk');await expect(page.locator('#watch-retry')).toBeVisible();
 await page.locator('#watch-retry').click();await expect(page.locator('#watch-rows tr')).toHaveCount(3);
});
test('dark theme and keyboard source selection remain readable',async({page},testInfo)=>{
 await page.setViewportSize({width:1600,height:1000});await fixture(page);await signIn(page);await page.goto('/Desk');await page.locator('#ThemeSelect').selectOption('dark');await page.getByRole('button',{name:'AAPL',exact:true}).click();await expect(page.locator('#watch-quantity')).toHaveText('10');await page.screenshot({path:testInfo.outputPath('stock-desk-dark.png'),fullPage:true});
 await page.locator('#watch-source').focus();await page.keyboard.press('ArrowDown');await page.keyboard.press('Enter');await expect(page.locator('#watch-title')).not.toHaveText('All wishlist stocks');
});

test('open stream without updates expires holdings and sell availability',async({page})=>{
 await page.clock.install();await fixture(page);await signIn(page);await page.goto('/Desk');await page.getByRole('button',{name:'AAPL',exact:true}).click();await expect(page.locator('#watch-sell')).toBeVisible();
 paused=true;await page.clock.fastForward(31000);await expect(page.locator('#watch-quantity')).toHaveText('Stale snapshot');await expect(page.locator('#watch-sell')).toBeHidden();await expect(page.locator('#watch-holding-block')).toHaveAttribute('title',/^Stale/);
});

test('real follow API preserves notes, rejects nonmembers and refreshes membership',async({page})=>{
 await signIn(page);await page.goto('/Desk');
 const result=await page.evaluate(async()=>{
  const post=async(path,body)=>{const r=await window.tradingFlowFetch(path,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});return {status:r.status,data:await r.json()};};
  const list=(await post('/api/v1/wishlists',{name:'Desk follow '+crypto.randomUUID(),isObserved:false})).data;
  await post('/api/v1/wishlists/'+list.id+'/items',{ticker:'AAPL',displayName:'Apple',notes:'Preserve operator note'});
  const missing=await post('/api/desk/follow',{source:'all',symbol:'NOTFOLLOWED',wishlistId:list.id});
  const first=await post('/api/desk/follow',{source:'all',symbol:'AAPL',wishlistId:list.id});
  const retry=await post('/api/desk/follow',{source:'all',symbol:'AAPL',wishlistId:list.id});
  const lists=await fetch('/api/v1/wishlists').then(r=>r.json());const current=lists.find(x=>x.id===list.id);
  return {first:first.status,retry:retry.status,missing:missing.status,current};
 });
 expect(result.first).toBe(200);expect(result.retry).toBe(200);expect(result.missing).toBe(400);expect(result.current.isObserved).toBe(false);expect(result.current.items).toHaveLength(1);expect(result.current.items[0].notes).toBe('Preserve operator note');
 await page.reload();await page.locator('#watch-search').fill('AAPL');await expect(page.locator('#watch-rows')).toContainText('AAPL');
});

test('news stays alongside a long wishlist and unavailable quotes are explicit',async({page})=>{
 await fixture(page);await signIn(page);
 await page.addInitScript(()=>{const Native=window.EventSource;window.EventSource=class extends Native{
 addEventListener(type,listener,options){return super.addEventListener(type,type==='snapshot'?event=>{const value=JSON.parse(event.data);value.rows=Array.from({length:32},(_,i)=>({...value.rows[0],membership:{...value.rows[0].membership,symbol:'STOCK'+i},quote:null,observation:{state:'loading',detail:'Loading completed daily history'}}));listener(new MessageEvent('snapshot',{data:JSON.stringify(value)}));}:listener,options);}
 };});
 await page.setViewportSize({width:1600,height:1000});await page.goto('/Desk');
 await expect(page.locator('#watch-rows tr')).toHaveCount(32);await expect(page.locator('#watch-connection')).toContainText('0/32 current quotes · 32 unavailable');
 await expect(page.locator('#watch-rows tr').first()).toContainText('Choose a strategy at the top');
 const news=await page.locator('.watch-news').boundingBox(),stocks=await page.locator('.watch-stocks').boundingBox();
 expect(Math.abs(news.y-stocks.y)).toBeLessThan(5);expect(news.y).toBeLessThan(850);
 expect(await page.locator('.watch-table-scroll').evaluate(x=>x.scrollHeight>x.clientHeight)).toBe(true);
});

// An administrator reviews buy and sell orders inside the Desk's detail panel; the page never changes.
test('administrator buy and sell open the reviewed order inside the Desk',async({page})=>{
 await page.setViewportSize({width:1440,height:1000});await fixture(page);await signInAsAdmin(page);await page.goto('/Desk');
 await expect(page.locator('.watch-desk')).toHaveAttribute('data-administrator','true');
 await expect(page.locator('#watch-rows tr')).toHaveCount(3);
 const aapl=page.locator('#watch-rows tr[data-symbol=AAPL]');
 await expect(aapl.locator('.watch-buy')).toHaveText('Buy $200.05');await expect(aapl.locator('.watch-sell')).toHaveText('Sell $199.95');
 await aapl.locator('.watch-buy').click();
 await expect(page).toHaveURL(/\/Desk/);await expect(page.locator('#watch-order')).toBeVisible();
 await expect(page.locator('#watch-order-title')).toHaveText('Buy AAPL');
 const frame=page.locator('#watch-order-frame');
 await expect(frame).toHaveAttribute('src',/\/OrderTicket\?.*embedded=1/);await expect(frame).toHaveAttribute('src',/side=buy/);await expect(frame).toHaveAttribute('src',/ticker=AAPL/);
 const ticket=page.frameLocator('#watch-order-frame');
 await expect(ticket.locator('#OrderWorkflowForm')).toBeVisible();await expect(ticket.locator('.topbar')).toHaveCount(0);
 await expect(ticket.locator('[name=symbol]')).toHaveValue('AAPL');await expect(ticket.locator('[name=limit]')).toHaveValue('200.05');
 // The held position can be sold from the same panel; selling uses the bid.
 await aapl.locator('.watch-sell').click();
 await expect(page.locator('#watch-order-title')).toHaveText('Sell AAPL');await expect(frame).toHaveAttribute('src',/side=sell/);
 await expect(ticket.locator('[name=limit]')).toHaveValue('199.95');
 await page.locator('#watch-order-close').click();await expect(page.locator('#watch-order')).toBeHidden();
 await expect(page).toHaveURL(/\/Desk/);
});

test('a non-administrator gets no override buy on the Desk',async({page})=>{
 await fixture(page);await signIn(page);await page.goto('/Desk');
 await expect(page.locator('.watch-desk')).toHaveAttribute('data-administrator','false');
 await page.locator('#watch-rows button').filter({hasText:'AAPL'}).click();
 await expect(page.locator('#watch-buy')).toBeHidden();await expect(page.locator('#watch-order-help')).toContainText('Start a paper strategy run');
});

// Signals from a recorded paper run: the Signals view lists stocks whose checks all passed, and a
// non-administrator buys a signal through the reviewed entry bound to its exact candidate.
test('signals view lists passed stocks and a signal buys through its exact candidate',async({page})=>{
 await fixture(page);await signIn(page);
 await page.addInitScript(()=>{const Native=window.EventSource;window.EventSource=class extends Native{
 addEventListener(type,listener,options){return super.addEventListener(type,type==='snapshot'?event=>{const value=JSON.parse(event.data);
  const strategy={strategyId:'swing.fixture',semanticVersion:'1.0.0',contentSha256:'a'.repeat(64)};
  const check=(passed)=>({gateName:'strategy:entry_trigger',passed,rejectionCode:passed?null:'close_above_trigger',inputs:{}});
  for(const row of value.rows){if(row.membership.symbol==='AAPL')row.strategyAssessment={symbol:'AAPL',strategy,candidateRunId:'33333333-3333-3333-3333-333333333333',candidateId:'44444444-4444-4444-4444-444444444444',state:'recorded',detail:'Recorded run',expired:false,checks:[check(true)]};
   if(row.membership.symbol==='MSFT')row.strategyAssessment={symbol:'MSFT',strategy,candidateRunId:'33333333-3333-3333-3333-333333333333',candidateId:null,state:'recorded',detail:'Recorded run',expired:false,checks:[check(false)]};}
  listener(new MessageEvent('snapshot',{data:JSON.stringify(value)}));}:listener,options);}};});
 await page.goto('/Desk');await expect(page.locator('#watch-rows tr')).toHaveCount(3);
 await expect(page.locator('[data-view-count=signals]')).toHaveText('1');await expect(page.locator('[data-view-count=rejected]')).toHaveText('1');await expect(page.locator('[data-view-count=held]')).toHaveText('1');
 await page.locator('[data-view=signals]').click();await expect(page.locator('#watch-rows tr')).toHaveCount(1);await expect(page.locator('#watch-rows tr')).toHaveAttribute('data-symbol','AAPL');
 await page.locator('[data-view=rejected]').click();await expect(page.locator('#watch-rows tr')).toHaveAttribute('data-symbol','MSFT');
 await page.locator('[data-view=signals]').click();await page.locator('#watch-rows .watch-symbol-button').click();
 await expect(page.locator('#watch-buy')).toHaveText('Buy signal $200.05');
 await expect(page.locator('#watch-buy')).toHaveAttribute('href',/candidateId=44444444-4444-4444-4444-444444444444/);await expect(page.locator('#watch-buy')).toHaveAttribute('href',/runId=33333333/);
 await page.locator('#watch-buy').click();await expect(page).toHaveURL(/\/Desk/);await expect(page.locator('#watch-order-frame')).toHaveAttribute('src',/candidateId=44444444/);
 // A stock without a signal offers a non-administrator no buy, and says how to get one.
 await page.locator('[data-view=all]').click();await page.locator('#watch-rows .watch-symbol-button').filter({hasText:'NVDA'}).click();
 await expect(page.locator('#watch-buy')).toBeHidden();await expect(page.locator('#watch-order-help')).toContainText('Start a paper strategy run');
});

test('a locked live environment shows the lock and no order controls on the Desk',async({page})=>{
 await signIn(page);await page.goto('/Desk?env=live');
 await expect(page.locator('.environment-lock')).toBeVisible();await expect(page.locator('#watch-rows')).toHaveCount(0);await expect(page.locator('.watch-buy, #watch-buy')).toHaveCount(0);
});
