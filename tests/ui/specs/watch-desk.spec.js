import {expect,test} from '@playwright/test';
import {signIn,signInAsAdmin} from './sign-in.js';
import http from 'node:http';
import {randomUUID} from 'node:crypto';
const id='11111111-1111-1111-1111-111111111111';
const sources=[{key:'all',name:'All followed stocks',group:'',description:'Across your stock groups'},{key:'wishlist:'+id,name:'Core',group:'My stock groups',description:'Observed'},{key:'swing:trend',name:'Established trends',group:'Finviz lists',description:'Discovery only'},{key:'saved:one',name:'My Elite momentum',group:'Finviz lists',description:'Locally imported'}];
// Feed the UI explicit completed daily SMA fixture values through its ordinary snapshot shape.
function row(symbol,{held=false,stale=false,followed=true}={}){return {membership:{symbol,name:symbol==='AAPL'?'Apple Inc.':symbol,wishlists:followed?[{id,name:'Core',observed:true}]:[]},quote:{midPrice:200,bidPrice:199.95,askPrice:200.05,timestamp:new Date(Date.now()-(stale?3600000:2000)).toISOString()},quoteFreshness:stale?'stale':'current',previousClose:{close:198,sessionDate:'2026-09-24'},technical:{sma10:197.25,sma20:195.50,sma50:190.25,sma200:180.75,rsi:58.2,ema20:198,ema50:190,ema200:180,atr:4.2,relativeVolume:1.3,timeframe:'1d',timestamp:'2026-09-25T20:00:00Z',dataFeed:'sip'},observation:{state:'ready',detail:'260 completed daily sessions',evaluation:{shouldAlert:false,reason:'Participation below watch threshold',sourceBarTimestampUtc:'2026-09-25T20:00:00Z'}},holding:held?{symbol,quantity:10,averageEntry:180,costBasis:1800,marketValue:2000,unrealizedPl:200}:null,holdingsKnown:true,sessionVolume:{ticker:symbol,volume:1250000,barTimestamp:new Date(Date.now()-60000).toISOString(),sessionDate:new Intl.DateTimeFormat('en-CA',{timeZone:'America/New_York',year:'numeric',month:'2-digit',day:'2-digit'}).format(new Date()),asOfUtc:new Date().toISOString(),feed:'sip'},newsSentiment:{positive:2,negative:2,neutral:1,unscored:3,available:true,windowStartUtc:new Date(Date.now()-3600000).toISOString(),asOfUtc:new Date().toISOString()},news:[{headline:symbol+' company update',url:'https://example.org/news/'+symbol,provider:'Fixture news',symbols:[symbol],publishedAtUtc:new Date().toISOString()}]};}
function snapshot(source){const rows=source==='all'?[row('AAPL',{held:true}),row('MSFT',{stale:true}),row('NVDA')]:[row('TSLA',{followed:false})];return {selection:{source:sources.find(x=>x.key===source)||sources[0],totalCount:rows.length,screenedAtUtc:source==='all'?null:new Date().toISOString(),error:null},rows,account:{available:true,detail:'Alpaca paper account · broker unrealized P&L',readAtUtc:new Date().toISOString()},news:rows.flatMap(x=>x.news),newsCoverage:'Matched stored news · historical coverage may be incomplete.',session:'Regular',readAtUtc:new Date().toISOString(),feed:'SIP'};}
let fixtureServer,fixtureUrl,paused=false;const connections=new Set();
test.beforeAll(async()=>{
 fixtureServer=http.createServer((req,res)=>{res.writeHead(200,{'Content-Type':'text/event-stream','Cache-Control':'no-store','Access-Control-Allow-Origin':'*'});res.flushHeaders();connections.add(res);
 const url=new URL(req.url,'http://fixture');const send=()=>{if(paused)return;const value=snapshot(url.searchParams.get('source'));const search=(url.searchParams.get('search')||'').toUpperCase();value.rows=value.rows.filter(x=>(x.membership.symbol+' '+x.membership.name).toUpperCase().includes(search));value.selection.matchedCount=value.rows.length;value.selection.page=0;value.selection.pageCount=1;res.write('event: snapshot\ndata: '+JSON.stringify(value)+'\n\n');};send();const timer=setInterval(send,5000);req.on('close',()=>{clearInterval(timer);connections.delete(res);});
 });await new Promise(resolve=>fixtureServer.listen(0,'127.0.0.1',resolve));fixtureUrl='http://127.0.0.1:'+fixtureServer.address().port;
});
test.afterAll(async()=>{for(const res of connections)res.end();fixtureServer.closeAllConnections();await new Promise(resolve=>fixtureServer.close(resolve));});
const workspaceRoute = /\/api\/v1\/operations\/workspace(?:\?.*)?$/;

// Isolate runtime, preparation worker and feed facts from stock observations in the Desk stream.
function workspace(overrides = {}) {
 return {
  tradingRuntime: 'Trading enabled', marketSession: 'Regular market', marketFeeds: 'Market feeds active',
  openingRestriction: null, exitRestriction: null, observedAtUtc: new Date().toISOString(),
  preparationWorker: {state:'paused',detail:'Preparation worker paused. Saved evidence is available.',canCollect:false},
  preparation: null, news:{state:'ready',sources:[],running:true}, ...overrides
 };
}

async function fixture(page, status = workspace()) {
 paused=false;
 await page.route('**/api/desk/sources',r=>r.fulfill({json:sources}));
 await page.route(workspaceRoute, r=>r.fulfill({json:status}));
 await page.addInitScript(url=>{
  const Native=window.EventSource;
  window.EventSource=class extends Native{
   constructor(path,options){super(path.startsWith('/api/desk/stream?')?url+path:path,options);}
  };
 },fixtureUrl);
}

// Keep evidence and the two review actions present with overflow confined to the table on narrow screens.
async function assertColumns(page) {
 const table = page.locator('.watch-table');
 await expect(table.locator('th')).toHaveText(['Stock','Price · age','Trade','Strategy','Position','News','Daily technicals','RVOL','Volume','Next action']);
 for (const row of await table.locator('tbody tr').all()) {
  await expect(row.locator('td')).toHaveCount(10);
  await expect(row.locator('[data-order-side]')).toHaveCount(2);
  await expect(row.locator('.watch-row-action')).toHaveCount(1);
 }
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
}

// Open supplemental facts only when a test needs to inspect them.
async function showEvidence(page) {
 const evidence=page.locator('#watch-evidence');
 if(await evidence.getAttribute('open')===null)await evidence.locator('summary').click();
}

// Give the embedded and full chart the same completed fixture candles without opening a market-data connection.
async function deskChartFixture(page) {
 await page.route('**/api/v1/charts/history?**', route=>route.fulfill({status:403,json:{}}));
 await page.routeWebSocket(/\/api\/v1\/charts\/hub(?:\?.*)?$/, socket=>{
  socket.onMessage(message=>{
   for(const record of message.toString().split('\x1e').filter(Boolean)){
    const frame=JSON.parse(record);
    if(frame.protocol==='json')socket.send('{}\x1e');
    if(frame.type!==4||frame.target!=='Watch')continue;
    const key={...frame.arguments[0],context:frame.arguments[0].context==='auto'?'intraday':frame.arguments[0].context};
    const started=Date.parse('2026-11-13T14:30:00Z'), interval=5*60_000;
    const bars=Array.from({length:24},(_,index)=>{
     const startUtc=new Date(started+index*interval).toISOString(), endUtc=new Date(started+(index+1)*interval).toISOString();
     const open=198+Math.sin(index/3)*1.6+index/12, close=open+(index%3===0?-.7:.45);
     return {startUtc,endUtc,sessionDate:'2026-11-13',segment:'regular',open,high:Math.max(open,close)+.65,
      low:Math.min(open,close)-.65,close,volume:24000+index*500,completionState:'completed',sourceKind:'fixture',availableAtUtc:endUtc};
    });
    const snapshot={contractVersion:2,seriesKey:key,asOfUtc:bars.at(-1).endUtc,status:'history_only',
     evidence:{provider:'alpaca',feed:'sip',adjustmentPolicy:'all',coverage:'complete',lastCompletedUtc:bars.at(-1).endUtc,
      basisBreaks:[],notes:[]},
     bars,zones:[],levels:[],markers:[],indicators:[],epoch:'desk-fixture',sequence:1};
    const parts=JSON.stringify(snapshot).match(/[\s\S]{1,7000}/g);
    for(const [part,payload] of parts.entries())socket.send(JSON.stringify({type:2,invocationId:frame.invocationId,
     item:{kind:'snapshot',key,epoch:snapshot.epoch,sequence:1,part,parts:parts.length,payload}})+'\x1e');
   }
  });
 });
}

test('desktop Desk keeps row evidence, news and useful chart space together at 1440x900',async({page},testInfo)=>{
 await page.setViewportSize({width:1440,height:900});
 await fixture(page);
 await deskChartFixture(page);
 await signIn(page);
 await page.goto('/Desk');
 await page.getByRole('button',{name:'AAPL',exact:true}).click();
 await expect(page.locator('[data-chart-candle-time]')).toContainText('NY');
 await expect(page.locator('#watch-evidence')).not.toHaveAttribute('open','');
 const table=await page.locator('.watch-stocks').boundingBox();
 const news=await page.locator('.watch-news').boundingBox();
 const plot=await page.locator('[data-chart-plot]').boundingBox();
 const symbol=await page.locator('#watch-symbol').boundingBox();
 const summary=await page.locator('#watch-chart-summary').boundingBox();
 expect(summary.x).toBeGreaterThanOrEqual(symbol.x+symbol.width);
 await expect(page.locator('#watch-chart-summary [data-chart-status]')).toBeVisible();
 await expect(page.locator('[data-chart-status]')).toHaveCount(1);
 expect(news.x).toBeGreaterThanOrEqual(table.x+table.width);
 expect(plot.y).toBeGreaterThan(table.y);
 await page.screenshot({path:process.env.TRADINGFLOW_DESK_REVIEW_SCREENSHOT||testInfo.outputPath('desk-1440.png'),
  animations:'disabled'});
 expect(plot.y+Math.min(plot.height,180)).toBeLessThan(900);
});

test('phone Desk confines its wide evidence table to a local scroll area at 390x830',async({page},testInfo)=>{
 await page.setViewportSize({width:390,height:830});
 await fixture(page);
 await deskChartFixture(page);
 await signIn(page);
 await page.goto('/Desk');
 const row=page.locator('#watch-rows tr[data-symbol=AAPL]');
 await expect(row.getByRole('button',{name:'Buy AAPL'})).toBeEnabled();
 await expect(row.getByRole('button',{name:'Sell AAPL'})).toBeEnabled();
 const table=page.locator('.watch-table-scroll');
 await expect.poll(()=>table.evaluate(node=>node.scrollWidth>node.clientWidth)).toBe(true);
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
 await table.scrollIntoViewIfNeeded();
 await page.getByRole('button',{name:'AAPL',exact:true}).click();
 await page.locator('#watch-chart-summary').scrollIntoViewIfNeeded();
 await expect(page.locator('#watch-chart-summary [data-chart-status]')).toBeVisible();
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
 await page.screenshot({path:process.env.TRADINGFLOW_DESK_REVIEW_MOBILE||testInfo.outputPath('desk-390.png'),
  animations:'disabled'});
});

test('chart Back and primary Desk navigation restore the selected view without replaying writes',async({page})=>{
 const strategy={strategyId:'fixture.current',semanticVersion:'1.0.0',contentSha256:'c'.repeat(64)};
 const mutations=[];
 await fixture(page);
 await deskChartFixture(page);
 await page.route('**/api/v1/desk/strategy-choices',route=>route.fulfill({json:[{name:'Current fixture',strategy,mode:'paper'}]}));
 await page.route('**/api/v1/desk/strategy-assessments**',route=>{
  if(route.request().method()==='POST')mutations.push(new URL(route.request().url()).pathname);
  return route.fulfill({json:{strategy,status:'completed',jobId:'77777777-7777-7777-7777-777777777777',
   profileSha256:'b'.repeat(64),detail:'Fixture complete',result:{stocks:[]}}});
 });
 await page.route('**/OrderTicket?**',route=>{
  mutations.push('ticket-open');
  return route.fulfill({contentType:'text/html',body:'<main data-order-review-unavailable>Fixture order review</main>'});
 });
 await page.addInitScript(()=>{
  const Native=window.EventSource;
  window.EventSource=class extends Native{
   addEventListener(type,listener,options){
    return super.addEventListener(type,type==='snapshot'?event=>{
     const value=JSON.parse(event.data), requested=new URL(this.url).searchParams;
     value.selection.page=Number(requested.get('page')||0);
     value.selection.pageCount=4;
     listener(new MessageEvent('snapshot',{data:JSON.stringify(value)}));
    }:listener,options);
   }
  };
 });
 await signIn(page);
 await page.goto('/Desk');
 await page.locator('#watch-search').fill('Apple');
 await expect(page.locator('#watch-rows tr')).toHaveCount(1);
 await page.locator('#watch-strategy').selectOption('0');
 await expect(page.locator('#watch-strategy-status')).toContainText('Fixture complete');
 await page.locator('#watch-next').click();
 await expect(page.locator('#watch-page')).toHaveText('Page 2 of 4');
 await expect(page.locator('#watch-strategy-status')).toContainText('Fixture complete');
 await page.locator('#watch-sort').selectOption('news');
 await page.locator('[data-view=held]').click();
 await page.getByRole('button',{name:'Buy AAPL'}).click();
 await expect(page.frameLocator('#OrderPopupFrame').locator('main')).toHaveText('Fixture order review');
 await page.locator('#OrderPopupClose').click();
 await expect(page.locator('#watch-symbol')).toHaveText('AAPL');
 await page.locator('#watch-news-all').click();
 const writesBeforeReturn=mutations.slice();
 await page.getByRole('link',{name:'Open full chart for AAPL'}).click();
 await expect(page).toHaveURL(/\/Chart\?/);
 await page.getByRole('link',{name:/Back to Desk/}).click();
 await expect(page.locator('.watch-desk')).toBeVisible();
 await expect(page.locator('#watch-symbol')).toHaveText('AAPL');
 await expect(page.locator('#watch-search')).toHaveValue('Apple');
 await expect(page.locator('#watch-sort')).toHaveValue('news');
 await expect(page.locator('[data-view=held]')).toHaveAttribute('aria-pressed','true');
 await expect(page.locator('#watch-page')).toHaveText('Page 2 of 4');
 await expect(page.locator('#watch-news-title')).toHaveText('News for this view');
 await expect(page.locator('#watch-strategy-status')).toContainText('Refresh strategy checks');
 await expect(page.locator('#OrderPopup')).toBeHidden();
 expect(mutations).toEqual(writesBeforeReturn);

 await page.getByRole('link',{name:'Open full chart for AAPL'}).click();
 await page.getByRole('navigation',{name:'Primary navigation'}).getByRole('link',{name:'Desk',exact:true}).click();
 await expect(page.locator('#watch-symbol')).toHaveText('AAPL');
 await expect(page.locator('#watch-strategy')).toHaveValue('0');
 expect(mutations).toEqual(writesBeforeReturn);
 await page.goto('/Desk?source=saved:one&search=TSLA');
 await expect(page.locator('#watch-rows tr[data-symbol=TSLA]')).toBeVisible();
 await expect(page.locator('#watch-source')).toHaveValue('saved:one');
 await expect(page.locator('#watch-search')).toHaveValue('TSLA');
 await expect(page.locator('#watch-strategy')).toHaveValue('');

 await page.addInitScript(()=>{
  if(location.search.includes('other-review'))sessionStorage.setItem('tradingflow.desk.return.v1',
   JSON.stringify({url:'/Desk',operator:'different-operator',environment:'paper',source:'saved:one',search:'OTHER',
    preparationKey:'other-review',strategyId:'fixture.current',semanticVersion:'1.0.0',contentSha256:'c'.repeat(64)}));
 });
 await page.evaluate(()=>history.replaceState({deskObserved:true,deskOperator:'different-operator'},'',
  '/Desk?source=prepared&preparationKey=other-review&search=OTHER'));
 await page.reload();
 await expect(page.locator('#watch-source')).toHaveValue('all');
 await expect(page.locator('#watch-rows tr')).toHaveCount(3);
 await expect(page.locator('#watch-search')).toHaveValue('');
 await expect(page).not.toHaveURL(/other-review|search=OTHER/);
 expect(mutations).toEqual(writesBeforeReturn);

 await page.goto('/Desk?search=AAPL&order=buy');
 await expect(page.locator('#OrderPopup')).toBeVisible();
 await expect(page).not.toHaveURL(/order=buy/);
 await page.reload();
 await expect(page.locator('#watch-symbol')).toHaveText('AAPL');
 await expect(page.locator('#OrderPopup')).toBeHidden();
 expect(mutations).toEqual([...writesBeforeReturn,'ticket-open']);
});

for(const width of [412,680,1000,1024,1200,1265,1280,1600,2560])test('stock desk is readable and focused at '+width,async({page},testInfo)=>{
 await page.setViewportSize({width,height:1000});await fixture(page);await signIn(page);await page.goto('/');await expect(page).toHaveURL(/Desk/);
 await expect(page.locator('#watch-rows tr')).toHaveCount(3);await expect(page.getByRole('heading',{level:1})).toHaveText('Desk');
 await expect(page.getByRole('navigation',{name:'Primary navigation'}).getByRole('link')).toHaveText(['Desk','Preparation','Trading','Strategies','Earnings','News','Stock groups']);
 await assertColumns(page);
 const firstRow=page.locator('#watch-rows tr[data-symbol=AAPL]');
 await expect(firstRow.locator('.watch-row-price')).toContainText('$200.00');
 await expect(firstRow.getByRole('button',{name:'Buy AAPL'})).toContainText('$200.05');
 await expect(firstRow.getByRole('button',{name:'Sell AAPL'})).toContainText('$199.95');
 await expect(firstRow.locator('.watch-news-summary')).toContainText('2 positive');
 await expect(firstRow.locator('.watch-news-summary')).toContainText('2 negative');
 await expect(firstRow.locator('[data-technical=sma10]')).toHaveText('$197.25');
 await expect(firstRow.locator('.watch-rvol')).toContainText('1.3×');
 const exit=firstRow.getByRole('button',{name:'Review exit AAPL'});
 await expect(exit).toBeVisible();expect((await exit.boundingBox()).height).toBeGreaterThanOrEqual(44);
 await expect(page.locator('#watch-rows tr[data-symbol=MSFT]')).toContainText('Delayed 1h');
 await expect(page.locator('.watch-detail')).toBeHidden();

 await page.locator('#watch-rows tr[data-symbol=NVDA] td').nth(3).click();
 await expect(page.locator('#watch-symbol')).toHaveText('NVDA');
 await expect(page.locator('#watch-symbol')).toBeFocused();
 expect(await page.locator('#watch-source optgroup').evaluateAll(xs=>xs.map(x=>x.label))).toEqual(['My stock groups','Finviz lists']);
 const selected=firstRow.getByRole('button',{name:'AAPL',exact:true});
 await selected.focus();await selected.press('Enter');
 await expect(page.locator('#watch-symbol')).toHaveText('AAPL');
 await expect(page.locator('#watch-symbol')).toBeFocused();
 await expect(selected).toHaveAttribute('aria-pressed','true');
 const prepared=await page.locator('[data-preparation-summary]').boundingBox();
 const stocks=await page.locator('.watch-stocks').boundingBox();
 const detail=await page.locator('.watch-detail').boundingBox();
 const news=await page.locator('.watch-news').boundingBox();
 expect(prepared.y+prepared.height).toBeLessThan(stocks.y);
 expect(detail.y).toBeGreaterThanOrEqual(stocks.y+stocks.height);
 if(width<=1150)expect(news.y).toBeGreaterThanOrEqual(detail.y+detail.height);
 else{
  expect(news.x).toBeGreaterThanOrEqual(stocks.x+stocks.width);
  expect(news.y).toBeLessThan(stocks.y+stocks.height);
 }
 await expect(page.locator('#watch-prev-close')).toHaveText('Prev close $198.00 · +1.01%');
 await expect(page.locator('#watch-quantity')).toHaveText('10');
 await expect(page.locator('#watch-pl')).toContainText('200.00');
 await expect(page.locator('#watch-sma10')).toHaveText('$197.25');
 await expect(page.locator('#watch-sma20')).toHaveText('$195.50');
 await expect(page.locator('#watch-tech-time')).toContainText(/Completed 1d bar.*2026-09-25T20:00:00.000Z.*sip/);
 await expect(page.locator('#watch-rvol')).toHaveText('1.3');
 await expect(page.locator('#watch-sip-volume')).toContainText('1,250,000 SIP shares');
 await expect(page.locator('#watch-strategy-block')).toBeHidden();
 await expect(page.locator('#watch-strategy-checks')).toBeEmpty();
 await expect(page.locator('#watch-buy')).toBeEnabled();
 await expect(page.locator('#watch-order-help')).toContainText('Start a paper strategy run');
 await expect(page.locator('#watch-sell')).toBeVisible();
 await expect(page.locator('#watch-news-title')).toHaveText('AAPL news');
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBeTruthy();
 await page.evaluate(()=>window.scrollTo(0,0));
 await page.screenshot({path:testInfo.outputPath('stock-desk-'+width+'.png'),fullPage:true});
});


test('source switch discards old rows and screener-only stocks require following',async({page})=>{
 await fixture(page);await signIn(page);await page.goto('/Desk');await expect(page.locator('#watch-rows tr')).toHaveCount(3);
 await page.locator('#watch-source').selectOption('saved:one');await expect(page.locator('#watch-rows tr')).toHaveCount(1);await expect(page.locator('#watch-rows')).not.toContainText('AAPL');
 await page.locator('#watch-rows .watch-symbol-button').click();
 await expect(page.locator('#watch-buy')).toBeEnabled();
 await expect(page.locator('#watch-sell')).toBeEnabled();
 await showEvidence(page);
 await expect(page.locator('#watch-follow')).toBeVisible();
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
 await signIn(page);await page.goto('/Desk');await expect(page.locator('#watch-source')).toContainText('All followed stocks');await expect(page.locator('#watch-connection')).toContainText(/current quotes|no stocks in this view/);
 const result=await page.evaluate(async()=>{const csrf=await fetch('/api/auth/csrf').then(r=>r.json());const name='UI saved '+crypto.randomUUID();const payload={name,url:'https://elite.finviz.com/screener.ashx?f=ta_sma50_pa&auth=not-a-real-secret'};const rejected=await fetch('/api/desk/screens',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});const saved=await fetch('/api/desk/screens',{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':csrf.requestToken},body:JSON.stringify(payload)});const entry=await saved.json();const sources=await fetch('/api/desk/sources').then(r=>r.json());return {rejected:rejected.status,saved:saved.status,screen:sources.find(x=>x.key===entry.key)};});
 expect(result.rejected).toBe(400);expect(result.saved).toBe(200);expect(result.screen.query).toBe('ta_sma50_pa');expect(result.screen.group).toBe('Finviz lists');
});
// A signed-in reader follows the direct card link to the same empty-workflow setup controls.
test('Desk links directly to Preparation and preserves keyboard setup focus', async({page})=>{
 await fixture(page, workspace({preparation:{eveningState:'Evening collection',morningState:'Morning collection'}}));
 await page.route('**/api/v1/preparation',route=>route.fulfill({json:[]}));
 await signIn(page);await page.goto('/Desk');
 const action=page.getByRole('link',{name:'Set up preparation'});
 await expect(action).toHaveAttribute('href',/\/Preparation$/);
 await expect(page.locator('#prep-setup-new')).toHaveCount(0);
 await action.focus();await action.press('Enter');
 await expect(page).toHaveURL(/\/Preparation$/);
 await expect(page.locator('#prep-setup-new')).toBeEnabled();
 await page.locator('#prep-setup-new').click();
 await expect(page.locator('#prep-name')).toBeFocused();
});

// Assert the shared status read gates entry and exit independently while preserving holdings.
for (const mode of ['paused','entry stop','unknown']) test('Desk explains order access when runtime is '+mode, async({page})=>{
 const pause='Trading is paused. This host rejects order and position changes, including exits and protection changes.';
 const stop='Entry risk stop is active.';
 const status=workspace(mode==='paused'?{tradingRuntime:'Trading paused',marketSession:'Market closed',openingRestriction:pause,exitRestriction:pause}:
  mode==='entry stop'?{openingRestriction:stop}:{});
 await fixture(page,status);
 if(mode==='unknown')await page.route(workspaceRoute,route=>route.fulfill({status:503,json:{message:'Fixture observation unavailable'}}));
 const ticketRequests=[];
 await page.route('**/OrderTicket?**',route=>{
  ticketRequests.push(new URL(route.request().url()).searchParams.get('side'));
  return route.fulfill({contentType:'text/html',body:'<main data-order-review-unavailable>Review unavailable</main>'});
 });
 await signInAsAdmin(page);await page.goto('/Desk');
 const strip=page.locator('[data-workspace-status]');
 await expect(strip.locator('[data-workspace-runtime]')).toHaveText(mode==='paused'?'Trading paused':mode==='unknown'?'Trading state unknown':'Trading enabled');
 if(mode==='paused'){
  await expect(strip.locator('[data-workspace-market]')).toHaveText('Market closed');
  await expect(strip.locator('[data-workspace-feeds]')).toHaveText('Market feeds active');
 }
 if(mode==='unknown'){
  await strip.getByText('Source freshness',{exact:true}).click();
  await expect(strip.locator('[data-workspace-checked]')).toContainText('Status read failed');
 }
 await page.getByRole('button',{name:'AAPL',exact:true}).click();
 await expect(page.locator('#watch-buy')).toBeEnabled();
 await expect(page.locator('#watch-order-help')).toContainText(mode==='paused'?pause:mode==='entry stop'?stop:'Trading status is unavailable');
 await expect(page.locator('#watch-quantity')).toHaveText('10');
 await showEvidence(page);
 await expect(page.getByRole('link',{name:'View positions and protection'})).toBeVisible();
 if(mode==='entry stop'){
  await expect(page.getByRole('button',{name:'Review exit AAPL'})).toBeVisible();
  await expect(page.locator('#watch-sell')).toBeVisible();
 }else{
  await expect(page.locator('#watch-sell')).toBeEnabled();
  await expect(page.getByRole('button',{name:'Inspect AAPL'})).toBeVisible();
 }
 expect(ticketRequests).toEqual([]);
 await page.getByRole('button',{name:'Buy AAPL'}).click();
 await expect(page.frameLocator('#OrderPopupFrame').locator('[data-order-review-unavailable]')).toHaveText('Review unavailable');
 await page.locator('#OrderPopupClose').click();
 await page.getByRole('button',{name:'Sell AAPL'}).click();
 await expect(page.locator('#OrderPopupFrame')).toHaveAttribute('src',/side=sell/);
 await expect.poll(()=>ticketRequests).toEqual(['buy','sell']);
});


test('disconnect marks account observations stale and retains sell review',async({page})=>{
 await fixture(page);await signIn(page);await page.goto('/Desk');await page.getByRole('button',{name:'AAPL',exact:true}).click();await expect(page.locator('#watch-sell')).toBeVisible();
 paused=true;for(const res of connections)res.end();await expect(page.locator('#watch-quantity')).toHaveText('Stale snapshot');await expect(page.locator('#watch-holding-block')).toHaveAttribute('title',/^Stale/);await expect(page.locator('#watch-sell')).toBeEnabled();await expect(page.locator('#watch-rows tr[data-symbol=AAPL]')).toContainText('Last observed');
});
test('catalog startup failure offers working retry',async({page})=>{
 await fixture(page);await signIn(page);await page.goto('/Orders');
 let first=true;
 await page.route('**/api/desk/sources',r=>{if(first){first=false;return r.fulfill({status:503,body:'Unavailable'});}return r.fulfill({json:sources});});
 await page.goto('/Desk');await expect(page.locator('#watch-retry')).toBeVisible();
 await page.locator('#watch-retry').click();await expect(page.locator('#watch-rows tr')).toHaveCount(3);
});
test('dark theme and keyboard source selection remain readable',async({page},testInfo)=>{
 await page.setViewportSize({width:1600,height:1000});await fixture(page);await signIn(page);await page.goto('/Desk');await page.getByLabel('System menu',{exact:true}).click();await page.locator('#ThemeSelect').selectOption('dark');await page.getByLabel('System menu',{exact:true}).click();await page.getByRole('button',{name:'AAPL',exact:true}).click();await expect(page.locator('#watch-quantity')).toHaveText('10');await page.screenshot({path:testInfo.outputPath('stock-desk-dark.png'),fullPage:true});
 await page.locator('#watch-source').focus();await page.keyboard.press('ArrowDown');await page.keyboard.press('Enter');await expect(page.locator('#watch-title')).not.toHaveText('All followed stocks');
});

test('open stream without updates expires holdings evidence while retaining sell review',async({page})=>{
 await page.clock.install();await fixture(page);await signIn(page);await page.goto('/Desk');await page.getByRole('button',{name:'AAPL',exact:true}).click();await expect(page.locator('#watch-sell')).toBeVisible();
 paused=true;await page.clock.fastForward(31000);await expect(page.locator('#watch-quantity')).toHaveText('Stale snapshot');await expect(page.locator('#watch-sell')).toBeEnabled();await expect(page.locator('#watch-holding-block')).toHaveAttribute('title',/^Stale/);
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

test('news stays beside a scrolling stock list and unavailable quotes stay explicit',async({page})=>{
 await fixture(page);await signIn(page);
 await page.addInitScript(()=>{const Native=window.EventSource;window.EventSource=class extends Native{
  addEventListener(type,listener,options){return super.addEventListener(type,type==='snapshot'?event=>{const value=JSON.parse(event.data);value.rows=Array.from({length:32},(_,i)=>({...value.rows[0],membership:{...value.rows[0].membership,symbol:'STOCK'+i},quote:null,observation:{state:'loading',detail:'Loading completed daily history'}}));listener(new MessageEvent('snapshot',{data:JSON.stringify(value)}));}:listener,options);}
 };});
 await page.setViewportSize({width:1600,height:1000});await page.goto('/Desk');
 await expect(page.locator('#watch-rows tr')).toHaveCount(32);
 await expect(page.locator('#watch-connection')).toContainText('0/32 current quotes · 32 unavailable');
 await expect(page.locator('.watch-row-price').first()).toContainText('No quote');
 const news=await page.locator('.watch-news').boundingBox(), stocks=await page.locator('.watch-stocks').boundingBox();
 expect(news.x).toBeGreaterThanOrEqual(stocks.x+stocks.width);
 expect(news.y).toBeLessThan(stocks.y+stocks.height);
 expect(await page.locator('.watch-table-scroll').evaluate(node=>node.scrollHeight>node.clientHeight)).toBe(true);
 await assertColumns(page);
});


// An administrator reviews buy and sell through the shared popup; the page never changes.
test('administrator buy and sell open the shared order popup',async({page})=>{
 await page.setViewportSize({width:1440,height:1000});await fixture(page);await signInAsAdmin(page);await page.goto('/Desk');
 await expect(page.locator('.watch-desk')).toHaveAttribute('data-administrator','true');
 await expect(page.locator('#watch-rows tr')).toHaveCount(3);
 const aapl=page.locator('#watch-rows tr[data-symbol=AAPL]');
 const stock=aapl.getByRole('button',{name:'AAPL',exact:true});
 await stock.click();
 const buy=page.getByRole('link',{name:'Override buy $200.05'});
 await expect(buy).toBeVisible();await buy.click();
 await expect(page).toHaveURL(/\/Desk/);await expect(page.locator('#OrderPopup')).toBeVisible();
 await expect(page.locator('#OrderPopupTitle')).toHaveText('Buy AAPL');
 const frame=page.locator('#OrderPopupFrame');
 await expect(frame).toHaveAttribute('src',/\/OrderTicket\?.*embedded=1/);await expect(frame).toHaveAttribute('src',/side=buy/);await expect(frame).toHaveAttribute('src',/ticker=AAPL/);
 const ticket=page.frameLocator('#OrderPopupFrame');
 await expect(ticket.locator('#OrderWorkflowForm')).toBeVisible();await expect(ticket.locator('.topbar')).toHaveCount(0);
 await expect(ticket.locator('[name=symbol]')).toHaveValue('AAPL');await expect(ticket.locator('[name=limit]')).toHaveValue('200.05');
 // The held position can be sold from the same popup; selling uses the bid.
 await page.locator('#OrderPopupClose').click();
 await expect(buy).toBeFocused();
 const exit=aapl.getByRole('button',{name:'Review exit AAPL'});
 await exit.click();
 await expect(page.locator('#OrderPopupTitle')).toHaveText('Sell AAPL');await expect(frame).toHaveAttribute('src',/side=sell/);
 await expect(ticket.locator('[name=limit]')).toHaveValue('199.95');
 await page.locator('#OrderPopupClose').click();await expect(page.locator('#OrderPopup')).toBeHidden();await expect(exit).toBeFocused();
 await expect(page).toHaveURL(/\/Desk/);
});

test('a non-administrator can open buy review and receives the ticket authorization message',async({page})=>{
 await fixture(page);await signIn(page);await page.goto('/Desk');
 await expect(page.locator('.watch-desk')).toHaveAttribute('data-administrator','false');
 await page.locator('#watch-rows button').filter({hasText:'AAPL'}).click();
 await expect(page.locator('#watch-buy')).toBeEnabled();await expect(page.locator('#watch-order-help')).toContainText('Start a paper strategy run');
 await page.locator('#watch-buy').click();
 await expect(page.frameLocator('#OrderPopupFrame').locator('[data-order-status]')).toContainText('Manual override requires Administrator');
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
 await page.locator('#watch-buy').click();await expect(page).toHaveURL(/\/Desk/);await expect(page.locator('#OrderPopupFrame')).toHaveAttribute('src',/candidateId=44444444/);
 await page.locator('#OrderPopupClose').click();
 // The other stock still opens a manual review, without carrying over the recorded candidate.
 await page.locator('[data-view=all]').click();await page.locator('#watch-rows .watch-symbol-button').filter({hasText:'NVDA'}).click();
 await expect(page.locator('#watch-buy')).toHaveAttribute('href',/ticker=NVDA/);
 await expect(page.locator('#watch-order-help')).toContainText('Start a paper strategy run');
});

test('a locked live environment shows the lock and no order controls on the Desk',async({page})=>{
 await signIn(page);await page.goto('/Desk?env=live');
 await expect(page.locator('.environment-lock')).toBeVisible();await expect(page.locator('#watch-rows')).toHaveCount(0);await expect(page.locator('.watch-row-action, #watch-buy')).toHaveCount(0);
});

// Daily indicators are historical completed-bar evidence, independent of live-quote age and market closure.
test('daily SMA values stay dated and never substitute missing or future observations',async({page})=>{
 await fixture(page);await signIn(page);
 await page.addInitScript(()=>{const Native=window.EventSource;window.EventSource=class extends Native{
  addEventListener(type,listener,options){return super.addEventListener(type,type==='snapshot'?event=>{
   const value=JSON.parse(event.data);value.session='ClosedWeekend';
   for(const row of value.rows){
    if(row.membership.symbol==='AAPL')row.quote.timestamp=new Date(Date.now()-3*86400000).toISOString();
    if(row.membership.symbol==='MSFT'){row.technical=null;row.observation={state:'unavailable',detail:'Fixture completed daily history unavailable'};}
    if(row.membership.symbol==='NVDA')row.technical={...row.technical,timestamp:'2099-01-01T00:00:00Z'};
   }
   listener(new MessageEvent('snapshot',{data:JSON.stringify(value)}));
  }:listener,options);}
 };});
 await page.goto('/Desk');
 const aapl=page.locator('#watch-rows tr[data-symbol=AAPL]');
await expect(aapl).toContainText('Market closed');
 await aapl.locator('.watch-symbol-button').click();
 await expect(page.locator('#watch-sma10')).toHaveText('$197.25');
 await expect(page.locator('#watch-rvol')).toHaveText('1.3');
 await expect(page.locator('#watch-tech-time')).toContainText('2026-09-25T20:00:00.000Z');
 await page.getByRole('button',{name:'MSFT',exact:true}).click();
 await expect(page.locator('#watch-sma10')).toHaveText('—');
 await expect(page.locator('#watch-sma20')).toHaveText('—');
 await expect(page.locator('#watch-rvol')).toHaveText('—');
 await expect(page.locator('#watch-tech-time')).toContainText('completed daily history unavailable');
 await page.getByRole('button',{name:'NVDA',exact:true}).click();
 await expect(page.locator('#watch-sma10')).toHaveText('—');
 await expect(page.locator('#watch-rvol')).toHaveText('—');
});

// Inspect opens the selected stock and moves focus without requesting an order ticket.
test('an unavailable row action opens stock detail and explains the missing signal',async({page})=>{
 await fixture(page);await signIn(page);
 let ticketRequests=0;page.on('request',request=>{if(new URL(request.url()).pathname==='/OrderTicket')ticketRequests++;});
 await page.goto('/Desk');
 const trigger=page.getByRole('button',{name:'Inspect NVDA'});
 await trigger.focus();await trigger.press('Enter');
 await expect(page.locator('#watch-symbol')).toBeFocused();
 await expect(page.locator('#watch-symbol')).toHaveText('NVDA');
 await expect(page.locator('#watch-order-help')).toContainText('No executable strategy signal');
 await expect(page.locator('#OrderPopup')).toBeHidden();
 expect(ticketRequests).toBe(0);
});

// Inspecting another stock leaves the exact unresolved ticket available through Resume ticket.
test('stock inspection retains the exact unresolved ticket',async({page})=>{
 await fixture(page);await signInAsAdmin(page);await page.goto('/Desk');
 const trigger=page.getByRole('button',{name:'Review exit AAPL'});
 await trigger.click();
 await expect(page.frameLocator('#OrderPopupFrame').locator('#OrderWorkflowForm')).toBeVisible();
 const frame=page.locator('#OrderPopupFrame'), original=await frame.getAttribute('src');
 await frame.evaluate(element=>{element.contentWindow.tradingFlowOrderState=()=> 'uncertain';});
 await page.locator('#OrderPopupClose').click();
 await expect(trigger).toBeFocused();
 await page.getByRole('button',{name:'Inspect MSFT'}).click();
 await expect(page.locator('#watch-symbol')).toHaveText('MSFT');
 await expect(page.locator('#watch-sell')).toBeEnabled();
 await expect(frame).toHaveAttribute('src',original);
 await page.getByRole('button',{name:'Resume ticket'}).click();
 await expect(frame).toHaveAttribute('src',original);
});


// Session closure is authoritative only after missing/disconnected/future quote checks; unknown stays delayed.
test('closed and after-hours quote labels use the actual session names and retain timestamps',async({page})=>{
 await fixture(page);await signIn(page);
 await page.addInitScript(()=>{const Native=window.EventSource;window.EventSource=class extends Native{
  addEventListener(type,listener,options){return super.addEventListener(type,type==='snapshot'?event=>{
   const value=JSON.parse(event.data);value.session=window.fixtureSession||'Closed';
   value.rows[0].quote.timestamp=new Date(Date.now()-3*86400000).toISOString();
   value.rows[1].quote=null;value.rows[2].quote.timestamp='2099-01-01T00:00:00Z';
   // Deliver explicit session transitions immediately instead of waiting for the fixture interval.
   window.fixtureSetSession=session=>{window.fixtureSession=session;value.session=session;listener(new MessageEvent('snapshot',{data:JSON.stringify(value)}));};
   listener(new MessageEvent('snapshot',{data:JSON.stringify(value)}));
  }:listener,options);}
 };});
 await page.goto('/Desk');await expect(page.locator('#watch-rows tr[data-symbol=AAPL] .watch-row-price')).toContainText('Market closed');
 await expect(page.locator('#watch-rows tr[data-symbol=AAPL] .watch-row-price')).toHaveAttribute('title',/Market closed.*NY/);
 await expect(page.locator('#watch-rows tr[data-symbol=MSFT] .watch-row-price')).toContainText('No quote');
 await expect(page.locator('#watch-rows tr[data-symbol=NVDA] .watch-row-price')).toContainText('Clock mismatch');
 await page.evaluate(()=>window.fixtureSetSession('Unknown'));await expect(page.locator('#watch-rows tr[data-symbol=AAPL] .watch-row-price')).toContainText('Delayed');
 await page.evaluate(()=>window.fixtureSetSession('AfterHours'));await expect(page.locator('#watch-session')).toHaveText('After hours');
});

// Failed news reads show neither successful zero counts nor a successful empty-history message.
test('news read failure remains distinct from zero stored stories',async({page})=>{
 await fixture(page);await signIn(page);
 await page.addInitScript(()=>{const Native=window.EventSource;window.EventSource=class extends Native{
  addEventListener(type,listener,options){return super.addEventListener(type,type==='snapshot'?event=>{
   const value=JSON.parse(event.data);value.news=[];value.newsCoverage='News is unavailable. Retry when the connection recovers.';
   for(const item of value.rows){item.news=[];item.newsSentiment={...item.newsSentiment,available:false};}
   listener(new MessageEvent('snapshot',{data:JSON.stringify(value)}));
  }:listener,options);}
 };});
 await page.goto('/Desk');await expect(page.locator('#watch-news-list')).toContainText('Stored news could not be read');
 await expect(page.locator('#watch-rows tr[data-symbol=AAPL] .watch-news-summary')).toContainText('News read unavailable');
 await expect(page.locator('#watch-rows tr[data-symbol=AAPL] .watch-news-summary')).not.toContainText('0 positive');
 await page.locator('#watch-rows tr[data-symbol=AAPL] .watch-symbol-button').click();
 await expect(page.locator('#watch-news-list')).toContainText('Stored news could not be read');
 await expect(page.locator('#watch-news-list')).not.toContainText('No matched stories');
});

// Source freshness reports the provider attempt separately from stories already retained on the Desk.
test('retained headlines remain visible after a partial news refresh', async({page})=>{
 const read=new Date().toISOString();
 await fixture(page,workspace({news:{state:'partial',running:true,sources:[
  {source:'Fixture news',state:'partial',attemptedAtUtc:read,detail:'Only one source completed.'}
 ]}}));
 await signIn(page);await page.goto('/Desk');
 await expect(page.locator('#watch-news-coverage')).toContainText('Latest feed refresh was partial.');
 await expect(page.locator('#watch-news-list')).toContainText('AAPL company update');
 await page.getByText('Source freshness',{exact:true}).click();
 await expect(page.locator('[data-workspace-sources]')).toContainText('Only one source completed.');
});

// A recorded strategy keeps the fixed columns and displays its precise run identity.
for(const width of [1280,1600,2560])test('strategy selection keeps the stock evidence columns at '+width,async({page})=>{
 await page.setViewportSize({width,height:1100});await fixture(page);await signIn(page);
 const strategy={strategyId:'fixture.recorded',semanticVersion:'1.0.0',contentSha256:'a'.repeat(64)};
 const candidateRunId='33333333-3333-3333-3333-333333333333';
 await page.route('**/api/v1/desk/strategy-choices',route=>route.fulfill({json:[{candidateRunId,strategy,name:'Recorded fixture',mode:'paper',runStartedAtUtc:new Date().toISOString()}]}));
 await page.goto('/Desk');await expect(page.locator('#watch-rows tr')).toHaveCount(3);await assertColumns(page);
 await page.locator('#watch-strategy').selectOption('0');
 await expect(page.locator('#watch-strategy-status')).toContainText('Run '+candidateRunId);
 await expect(page.locator('#watch-rows tr')).toHaveCount(3);await assertColumns(page);
 await page.locator('#watch-strategy').selectOption('');
 await expect(page.locator('#watch-strategy-status')).toContainText('Select a strategy');
 await expect(page.locator('#watch-rows tr')).toHaveCount(3);await assertColumns(page);
});


// Daily RVOL and current session shares keep zero distinct from missing observations, with no technical-volume fallback.
test('daily RVOL and session volume distinguish valid zero from unavailable data',async({page})=>{
 await fixture(page);await signIn(page);
 await page.addInitScript(()=>{const Native=window.EventSource;window.EventSource=class extends Native{
  addEventListener(type,listener,options){return super.addEventListener(type,type==='snapshot'?event=>{
   const value=JSON.parse(event.data);
   const zero=value.rows.find(row=>row.membership.symbol==='MSFT');zero.technical.relativeVolume=0;zero.sessionVolume.volume=0;
   const missing=value.rows.find(row=>row.membership.symbol==='NVDA');missing.technical.relativeVolume=null;missing.technical.currentVolume=999999;missing.sessionVolume=null;
   listener(new MessageEvent('snapshot',{data:JSON.stringify(value)}));
  }:listener,options);}
 };});
 await page.goto('/Desk');
 await page.getByRole('button',{name:'MSFT',exact:true}).click();
 await expect(page.locator('#watch-rvol')).toHaveText('0');
 await expect(page.locator('#watch-sip-volume')).toContainText(/0 SIP shares.*observed/);
 await expect(page.locator('#watch-tech-time')).toContainText(/Completed 1d bar.*sip/);
 await page.getByRole('button',{name:'NVDA',exact:true}).click();
 await expect(page.locator('#watch-rvol')).toHaveText('—');
 await expect(page.locator('#watch-sip-volume')).toHaveText('SIP session volume unavailable.');
});

// Invalid or mismatched receipt provenance cannot produce a current-volume number from any other row field.
test('volume displays reject invalid numbers, future clocks and inconsistent source dates',async({page})=>{
 await fixture(page);await signIn(page);
 await page.addInitScript(()=>{const Native=window.EventSource;window.EventSource=class extends Native{
  addEventListener(type,listener,options){return super.addEventListener(type,type==='snapshot'?event=>{
   const value=JSON.parse(event.data),base=value.rows[0],future=new Date(Date.parse(value.readAtUtc)+1000).toISOString();
   const cases=[['MISSING',null],['NEGATIVE',{volume:-1}],['TEXT',{volume:'1250000'}],['INFINITE',{volume:null}],['NULLVALUE',{volume:null}],['WRONG',{ticker:'OTHER'}],['FUTUREBAR',{barTimestamp:future}],['FUTURERECEIPT',{asOfUtc:future}],['INVALIDTIME',{barTimestamp:'invalid'}],['BADDATE',{sessionDate:'2026-02-30'}],['DATECONFLICT',{sessionDate:'2001-01-01'}],['NON_SIP',{feed:'overnight'}],['NO_FEED',{feed:null}]];
   const invalidRvol={NEGATIVE:-1,TEXT:'1.3',INFINITE:'rvolFixtureOverflow'};
   value.rows=cases.map(([symbol,patch])=>({...base,membership:{...base.membership,symbol},technical:{...base.technical,currentVolume:999999,relativeVolume:invalidRvol[symbol]??base.technical.relativeVolume,ticker:symbol==='WRONG'?'OTHER':symbol},sessionVolume:patch===null?null:{...base.sessionVolume,ticker:symbol,...patch}}));
   // A JSON number can overflow on parse. Encode it directly so stringify cannot turn it into null.
   listener(new MessageEvent('snapshot',{data:JSON.stringify(value).replace('"rvolFixtureOverflow"','1e999')}));
  }:listener,options);}
 };});
 await page.goto('/Desk');await expect(page.locator('#watch-rows tr')).toHaveCount(13);
 for(const symbol of ['MISSING','NEGATIVE','TEXT','INFINITE','NULLVALUE','WRONG','FUTUREBAR','FUTURERECEIPT','INVALIDTIME','BADDATE','DATECONFLICT','NON_SIP','NO_FEED']){
  await page.getByRole('button',{name:symbol,exact:true}).click();
  await expect(page.locator('#watch-sip-volume')).toHaveText('SIP session volume unavailable.');
  if(['NEGATIVE','TEXT','INFINITE','WRONG'].includes(symbol))await expect(page.locator('#watch-rvol')).toHaveText('—');
 }
});

// Older provider day bars stay explicitly dated while RVOL still describes its completed daily snapshot.
test('past-dated session shares retain the exchange date and receipt',async({page})=>{
 await fixture(page);await signIn(page);
 await page.addInitScript(()=>{const Native=window.EventSource;window.EventSource=class extends Native{
  addEventListener(type,listener,options){return super.addEventListener(type,type==='snapshot'?event=>{
   const value=JSON.parse(event.data);value.rows[0].sessionVolume={...value.rows[0].sessionVolume,barTimestamp:'2026-09-25T04:00:00Z',sessionDate:'2026-09-25',asOfUtc:value.readAtUtc};
   listener(new MessageEvent('snapshot',{data:JSON.stringify(value)}));
  }:listener,options);}
 };});
 await page.goto('/Desk');await page.getByRole('button',{name:'AAPL',exact:true}).click();
 await expect(page.locator('#watch-sip-volume')).toContainText('1,250,000 SIP shares · 2026-09-25 NY · observed');
 await expect(page.locator('#watch-sip-volume')).toHaveAttribute('title',/includes regular and extended hours; excludes overnight-feed trades/);
 await expect(page.locator('#watch-rvol')).toHaveText('1.3');
});

// Receipt age and disconnection retain shares as observations without implying a live current-day count.
for(const disconnected of [false,true])test('session shares retain observed provenance when '+(disconnected?'disconnected':'stale'),async({page})=>{
 await fixture(page);await signIn(page);
 await page.addInitScript(disconnected=>{const Native=window.EventSource;window.EventSource=class extends Native{
  addEventListener(type,listener,options){return super.addEventListener(type,type==='snapshot'?event=>{
   const value=JSON.parse(event.data);
   if(!disconnected){const at=Date.parse(value.readAtUtc)-120000,bar=new Date(at-1000);value.rows[0].sessionVolume={...value.rows[0].sessionVolume,asOfUtc:new Date(at).toISOString(),barTimestamp:bar.toISOString(),sessionDate:new Intl.DateTimeFormat('en-CA',{timeZone:'America/New_York',year:'numeric',month:'2-digit',day:'2-digit'}).format(bar)};}
   listener(new MessageEvent('snapshot',{data:JSON.stringify(value)}));if(disconnected)this.dispatchEvent(new Event('error'));
  }:listener,options);}
 };},disconnected);
 await page.goto('/Desk');await page.getByRole('button',{name:'AAPL',exact:true}).click();
 await expect(page.locator('#watch-sip-volume')).toContainText(/1,250,000 SIP shares.*NY · observed.*NY/);
});

// Today follows the New York date at the snapshot time even when UTC has advanced to the next day.
test('session volume uses the New York calendar date',async({page})=>{
 await page.clock.install({time:new Date('2026-11-10T01:00:00Z')});await fixture(page);await signIn(page);
 await page.addInitScript(()=>{const Native=window.EventSource;window.EventSource=class extends Native{
  addEventListener(type,listener,options){return super.addEventListener(type,type==='snapshot'?event=>{
   const value=JSON.parse(event.data);value.readAtUtc='2026-11-10T01:00:00Z';value.rows[0].sessionVolume={ticker:'AAPL',volume:1250000,barTimestamp:'2026-11-09T05:00:00Z',sessionDate:'2026-11-09',asOfUtc:value.readAtUtc,feed:'sip'};
   listener(new MessageEvent('snapshot',{data:JSON.stringify(value)}));
  }:listener,options);}
 };});
 await page.goto('/Desk');await page.getByRole('button',{name:'AAPL',exact:true}).click();
 await expect(page.locator('#watch-sip-volume')).toContainText('2026-11-09 NY · observed');
});
