import {expect,test} from '@playwright/test';
import {signIn} from './sign-in.js';
const workflow='11111111-1111-1111-1111-111111111111',occurrence='22222222-2222-2222-2222-222222222222',revision=occurrence;
const json=(route,data,status=200)=>route.fulfill({status,contentType:'application/json',body:JSON.stringify(data)});
const identity={strategyId:'fixture',semanticVersion:'1.0.0',contentSha256:'a'.repeat(64)};
async function fixture(page){
 await signIn(page);
 const meta=await (await page.request.get('/api/v1/preparation/editor')).json();
 const definition={workflowId:workflow,revision:1,contentSha256:'b'.repeat(64),effectiveAtUtc:new Date().toISOString(),settings:{...meta.defaults,name:'Morning fixture',sources:[{kind:'finviz',key:'fixture',required:true}]}};
 const symbol={symbol:'MSFT',change:'new',readiness:'complete',reasons:[],dailyDate:'2026-09-25',historyCount:126,close:100,feed:'sip',adjustment:'all',evidenceSha256:'c'.repeat(64),news:[{headline:'Captured article',url:'https://example.com/captured',publishedAtUtc:new Date().toISOString()}],context:['Same-time volume incomplete.']};
 const snapshot={revisionId:revision,cutoffUtc:new Date().toISOString(),publishedAtUtc:new Date().toISOString(),expiresAtUtc:new Date(Date.now()+120000).toISOString(),readiness:'complete',outcome:'completed',reasons:[],sources:[],symbols:[symbol],removedSymbols:[]};
 const row={id:occurrence,workflowId:workflow,definitionRevision:1,slot:{phase:'morning',targetDate:'2026-09-28',scheduledAtUtc:new Date().toISOString()},status:'completed',snapshot};
 await page.route('**/api/v1/strategies?*',r=>json(r,{strategies:[{identity,name:'Research fixture'}]}));
 await page.route('**/api/v1/preparation',r=>json(r,r.request().method()==='GET'?[definition]:definition));
 await page.route('**/api/v1/preparation/'+workflow,r=>json(r,{definition,upcoming:[],history:[row]}));
 await page.route('**/api/v1/preparation/events',r=>json(r,[{eventSequence:1,occurredAtUtc:new Date().toISOString(),payloadJson:JSON.stringify({workflowId:workflow,occurrenceId:occurrence,revisionId:revision,readiness:'complete'})}]));
 await page.goto('/Preparation');await expect(page.locator('#prep-status')).not.toHaveText('Loading workflows…');return {definition,row};
}
for(const width of [1440,412])test('preparation configuration and exact alert review at '+width,async({page})=>{
 await page.setViewportSize({width,height:915});await fixture(page);await page.locator('#prep-open').click();
 await page.getByRole('button',{name:'Workflow settings',exact:true}).click();await expect(page.locator('#prep-name')).toHaveValue('Morning fixture');await page.locator('#prep-name').fill('Unsaved draft');await page.locator('#prep-refresh').click();await expect(page.locator('#prep-name')).toHaveValue('Unsaved draft');
 await page.reload();await page.getByRole('button',{name:'Workflow settings',exact:true}).click();await expect(page.locator('#prep-name')).toHaveValue('Unsaved draft');
 await page.getByRole('button',{name:'History',exact:true}).click();await page.getByRole('button',{name:'Review snapshot',exact:true}).click();await expect(page.locator('#prep-audit')).toContainText(revision);
 await page.locator('#prep-members summary',{hasText:'MSFT'}).click();await expect(page.getByRole('link',{name:'Open captured article'})).toHaveAttribute('href','https://example.com/captured');
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBeTruthy();
});
test('preparation handoff retry preserves exact candidate command',async({page})=>{
 await fixture(page);await page.locator('#prep-open').click();await page.getByRole('button',{name:'History',exact:true}).click();await page.getByRole('button',{name:'Review snapshot',exact:true}).click();await page.locator('#prep-reason').fill('Reviewed evidence');
 const commands=[];await page.route('**/api/v1/preparation/**/handoff',r=>json(r,{reviewId:workflow,universeSnapshotId:occurrence,expiresAtUtc:new Date(Date.now()+120000).toISOString()}));
 await page.route('**/api/v1/candidate-runs',r=>{commands.push(r.request().postDataJSON());return commands.length===1?r.abort('failed'):json(r,{candidateRunId:workflow,candidates:[]});});
 await page.locator('#prep-handoff').click();await expect.poll(()=>commands.length).toBe(1);await page.locator('#prep-handoff').click();await expect.poll(()=>commands.length).toBe(2);expect(commands[0]).toEqual(commands[1]);
});
test('paper review carries accepted preparation without starting a job',async({page})=>{
 await fixture(page);await page.locator('#prep-open').click();await page.getByRole('button',{name:'History',exact:true}).click();await page.getByRole('button',{name:'Review snapshot',exact:true}).click();await page.locator('#prep-reason').fill('Paper review');let jobs=0;
 await page.route('**/api/v1/preparation/**/handoff',r=>json(r,{reviewId:workflow,universeSnapshotId:occurrence}));
 await page.route('**/api/v1/preparation/accepted?*',r=>json(r,{universeSnapshotId:occurrence,symbols:['MSFT'],expiresAtUtc:new Date(Date.now()+120000).toISOString()}));
 await page.route('**/api/v1/paper/runs',r=>{jobs++;return json(r,{});});await page.locator('#prep-paper').click();await expect(page).toHaveURL(/Paper\?preparationKey=/);await expect(page.getByText(/Accepted preparation snapshot/)).toContainText(occurrence);expect(jobs).toBe(0);
});
test('curation queues fresh collection with parent and reason',async({page})=>{
 await fixture(page);await page.locator('#prep-open').click();await page.getByRole('button',{name:'History',exact:true}).click();await page.getByRole('button',{name:'Review snapshot',exact:true}).click();await page.locator('#prep-reason').fill('Add after new observation');await page.getByText('Adjust reviewed membership',{exact:true}).click();await page.locator('#prep-additions').fill('AAPL');let body;
 await page.route('**/api/v1/preparation/**/curate',r=>{body=r.request().postDataJSON();return json(r,{id:workflow,status:'queued'});});await page.locator('#prep-curate').click();await expect.poll(()=>body?.parentRevisionId).toBe(revision);expect(body.addSymbols).toEqual(['AAPL']);
});

test('paper review survives mode change, refresh and environment navigation',async({page})=>{
 await fixture(page);await page.locator('#prep-reason').fill('Preserve membership');let jobs=0;
 await page.route('**/api/v1/preparation/**/handoff',r=>json(r,{reviewId:workflow,universeSnapshotId:occurrence}));
 await page.route('**/api/v1/preparation/accepted?*',r=>json(r,{universeSnapshotId:occurrence,symbols:['MSFT'],expiresAtUtc:new Date(Date.now()+120000).toISOString()}));
 await page.route('**/api/v1/paper/runs',r=>{jobs++;return json(r,{});});
 await page.locator('#prep-paper').click();await expect(page).toHaveURL(/Paper\?preparationKey=/);const key=new URL(page.url()).searchParams.get('preparationKey');expect(key).toBeTruthy();
 await page.locator('#PaperExecutionMode').selectOption('experiment');await expect(page).toHaveURL(/paperExecutionMode=experiment/);
 expect(new URL(page.url()).searchParams.get('preparationKey')).toBe(key);
 await page.getByRole('link',{name:'Refresh',exact:true}).click();await expect(page.getByText(/Accepted preparation snapshot/)).toContainText(occurrence);
 expect(new URL(page.url()).searchParams.get('preparationKey')).toBe(key);
 // Reloading on the current environment keeps the accepted preparation; only one environment badge is shown.
 await expect(page.locator('.env-badge')).toHaveText('PAPER');await page.reload();await expect(page.getByText(/Accepted preparation snapshot/)).toContainText(occurrence);
 expect(new URL(page.url()).searchParams.get('preparationKey')).toBe(key);expect(jobs).toBe(0);
});
test('daily workspace opens latest evidence and keeps new workflow separate',async({page})=>{
 await fixture(page);await expect(page.locator('#prep-review')).toBeVisible();await expect(page.locator('#prep-editor')).not.toBeVisible();
 await page.locator('#prep-members summary',{hasText:'MSFT'}).click();await expect(page.getByRole('link',{name:'Open market detail'})).toHaveAttribute('href','/Desk?search=MSFT');
 await page.locator('#prep-new').click();await expect(page.locator('#prep-editor')).toBeVisible();await expect(page.locator('#prep-review')).not.toBeVisible();
 await page.getByRole('button',{name:'Latest review',exact:true}).click();await expect(page.locator('#prep-empty')).toBeVisible();
});

test('refresh advances latest but retains an edited review until explicitly replaced',async({page})=>{
 const {definition,row}=await fixture(page);
 const newer={...row,id:'33333333-3333-3333-3333-333333333333',snapshot:{...row.snapshot,revisionId:'33333333-3333-3333-3333-333333333333'}};
 await page.route('**/api/v1/preparation/'+workflow,r=>json(r,{definition,upcoming:[],history:[newer,row]}));
 await page.locator('#prep-refresh').click();await expect(page.locator('#prep-audit')).toContainText(newer.id);
 await page.locator('#prep-reason').fill('Keep my review');
 const newest={...newer,id:'44444444-4444-4444-4444-444444444444',snapshot:{...row.snapshot,revisionId:'44444444-4444-4444-4444-444444444444'}};
 await page.route('**/api/v1/preparation/'+workflow,r=>json(r,{definition,upcoming:[],history:[newest,newer,row]}));
 await page.locator('#prep-refresh').click();await expect(page.locator('#prep-update')).toContainText('newer result');await expect(page.locator('#prep-reason')).toHaveValue('Keep my review');
 await page.locator('#prep-latest').click();await expect(page.locator('#prep-audit')).toContainText(newest.id);
});
test('missing exact notification does not substitute latest',async({page})=>{
 await fixture(page);await page.goto('/Preparation?workflow='+workflow+'&occurrence=missing&revision=missing');
 await expect(page.locator('#prep-status')).toContainText('Requested revision unavailable');await expect(page.locator('#prep-review')).not.toBeVisible();
});

test('empty setup guides a paused save, prevents duplicate sources and explains closed collection',async({page})=>{
 await signIn(page);const meta=await(await page.request.get('/api/v1/preparation/editor')).json();let saved=null;
 await page.route('**/api/v1/wishlists',r=>json(r,[{id:workflow,name:'Core wishlist',items:[]}]));
 await page.route('**/api/v1/preparation/events',r=>json(r,[]));
 await page.route('**/api/v1/preparation',r=>{if(r.request().method()==='GET')return json(r,saved?[saved]:[]);const body=r.request().postDataJSON();expect(body.settings.enabled).toBe(false);saved={workflowId:workflow,revision:1,contentSha256:'a'.repeat(64),settings:body.settings};return json(r,saved);});
 await page.route('**/api/v1/preparation/'+workflow,r=>json(r,{definition:saved,history:[],upcoming:[{phase:'nightly',targetDate:'2026-09-29',scheduledAtUtc:'2026-09-28T20:45:00Z'}],collectionWindows:[],readAtUtc:new Date().toISOString()}));
 await page.goto('/Desk');await page.locator('#watch-preparation>summary').click();await expect(page.locator('#prep-status')).toContainText('not configured');
 await expect(page.locator('#prep-open')).toBeDisabled();await expect(page.locator('#prep-refresh')).toBeDisabled();await expect(page.locator('#prep-schedule')).toBeHidden();
 await page.locator('#prep-setup-new').click();await expect(page.locator('#prep-editor')).toBeVisible();await page.locator('#prep-save').click();await expect(page.locator('#prep-status')).toContainText('at least one');
 await page.locator('#prep-add-wishlist').click();await page.locator('#prep-add-wishlist').click();await expect(page.locator('#prep-status')).toContainText('already selected');await expect(page.locator('#prep-sources .prep-card')).toHaveCount(1);await expect(page.locator('#prep-sources')).toContainText('Core wishlist');
 await page.locator('#prep-save').click();await expect(page.locator('#prep-empty')).toContainText('Workflow saved');await expect(page.locator('#prep-collection-status')).toContainText('outside');await expect(page.locator('#prep-nightly')).toBeDisabled();await expect(page.locator('#prep-upcoming')).toContainText('evening collection');await expect(page.locator('#watch-prep-summary')).toContainText('1 workflow');
 await page.reload();await page.locator('#watch-preparation>summary').click();await expect(page.locator('#prep-empty')).toContainText('Workflow saved');await expect(page.locator('#prep-editor')).toBeHidden();
});

test('startup failure is retryable without requiring a strategy catalog',async({page})=>{
 await signIn(page);let fail=true;await page.route('**/api/v1/preparation/editor',r=>fail?json(r,{message:'Editor temporarily unavailable'},503):r.continue());
 await page.route('**/api/v1/strategies?*',r=>json(r,{message:'Catalog unavailable'},503));await page.route('**/api/v1/preparation',r=>json(r,[]));
 await page.goto('/Preparation');await expect(page.locator('#prep-retry')).toBeVisible();await expect(page.locator('#prep-new')).toBeDisabled();fail=false;await page.locator('#prep-retry').click();await expect(page.locator('#prep-setup-new')).toBeEnabled();await page.locator('#prep-setup-new').click();await expect(page.locator('#prep-editor')).toBeVisible();
});

test('saved and new drafts restore only genuine unsaved editor changes',async({page})=>{
 await fixture(page);await page.reload();await expect(page.locator('#prep-review')).toBeVisible();await expect(page.locator('#prep-editor')).toBeHidden();
 await page.locator('#prep-new').click();await page.locator('#prep-name').fill('My unsaved preparation');await page.reload();await expect(page.locator('#prep-name')).toBeVisible();await expect(page.locator('#prep-name')).toHaveValue('My unsaved preparation');await expect(page.locator('#prep-status')).toContainText('Unsaved workflow');
});

test('a removed saved workflow becomes a recoverable draft without a failed detail call',async({page})=>{
 await fixture(page);await page.route('**/api/v1/preparation',r=>json(r,[]));await page.route('**/api/v1/preparation/'+workflow,r=>r.fulfill({status:404,body:''}));await page.reload();await expect(page.locator('#prep-status')).toContainText('no longer available');await expect(page.locator('#prep-name')).toBeVisible();await expect(page.locator('#prep-toggle')).toBeDisabled();
});

test('queued collection automatically becomes a review and edited reviews survive updates',async({page})=>{
 await page.clock.install();const {definition,row}=await fixture(page);let complete=false;
 await page.route('**/api/v1/preparation/'+workflow,r=>json(r,{definition,history:[complete?row:{...row,status:'queued',snapshot:null}],upcoming:[],collectionWindows:[],readAtUtc:new Date().toISOString()}));
 await page.locator('#prep-open').click();await expect(page.locator('#prep-review')).toBeHidden();complete=true;await page.clock.fastForward(10000);await expect(page.locator('#prep-review')).toBeVisible();
 await page.locator('#prep-reason').fill('Preserve my review');const newer={...row,id:'33333333-3333-3333-3333-333333333333',snapshot:{...row.snapshot,revisionId:'33333333-3333-3333-3333-333333333333'}};
 await page.route('**/api/v1/preparation/'+workflow,r=>json(r,{definition,history:[newer,row],upcoming:[],collectionWindows:[],readAtUtc:new Date().toISOString()}));await page.clock.fastForward(10000);await expect(page.locator('#prep-update')).toContainText('newer result');await expect(page.locator('#prep-reason')).toHaveValue('Preserve my review');
});

for(const target of ['handoff','paper'])test('pending '+target+' retains its exact revision during automatic refresh',async({page})=>{
 await page.clock.install();const {definition,row}=await fixture(page);let release,started=false;const gate=new Promise(resolve=>release=resolve);let candidate;
 await page.route('**/api/v1/preparation/**/handoff',async r=>{started=true;await gate;return json(r,{reviewId:workflow,universeSnapshotId:occurrence});});
 await page.route('**/api/v1/candidate-runs',r=>{candidate=r.request().postDataJSON();return json(r,{candidateRunId:workflow});});
 await page.route('**/api/v1/preparation/accepted?*',r=>json(r,{universeSnapshotId:occurrence,symbols:['MSFT'],expiresAtUtc:new Date(Date.now()+120000).toISOString()}));
 await page.locator('#prep-'+target).click();await expect.poll(()=>started).toBe(true);
 const newer={...row,id:'33333333-3333-3333-3333-333333333333',snapshot:{...row.snapshot,revisionId:'33333333-3333-3333-3333-333333333333',cutoffUtc:new Date(Date.now()+10000).toISOString()}};
 await page.route('**/api/v1/preparation/'+workflow,r=>json(r,{definition,history:[newer,row],upcoming:[]}));await page.clock.fastForward(10000);await expect(page.locator('#prep-audit')).toContainText(revision);release();
 if(target==='handoff'){await expect(page).toHaveURL(/Desk\?runId=/);expect(candidate.asOfUtc).toBe(row.snapshot.cutoffUtc);expect(candidate.universeSnapshotId).toBe(occurrence);}else await expect(page).toHaveURL(/Paper\?preparationKey=/);
});

test('real API saves and reopens a paused preparation workflow',async({page})=>{
 await signIn(page);await page.goto('/Preparation');await expect(page.locator('#prep-new')).toBeEnabled();
 const wishlist=await page.evaluate(async()=>{const csrf=await fetch('/api/auth/csrf').then(r=>r.json());const r=await fetch('/api/v1/wishlists',{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':csrf.requestToken},body:JSON.stringify({name:'Preparation API '+crypto.randomUUID(),isObserved:false})});if(!r.ok)throw new Error('Wishlist setup failed');return r.json();});
 await page.reload();await expect(page.locator('#prep-new')).toBeEnabled();
 // With no saved workflow the setup card is the way in; once one exists the toolbar offers New workflow.
 await page.locator(await page.locator('#prep-new').isVisible()?'#prep-new':'#prep-setup-new').click();const name='Paused preparation '+Date.now();await page.locator('#prep-name').fill(name);await page.locator('#prep-wishlist').selectOption(wishlist.id);await page.locator('#prep-add-wishlist').click();await page.locator('#prep-save').click();await expect(page.locator('#prep-status')).toContainText('Workflow paused');await expect(page.locator('#prep-list')).toContainText(name);
 const definitions=await(await page.request.get('/api/v1/preparation')).json();const stored=definitions.find(x=>x.settings.name===name);expect(stored.settings.enabled).toBe(false);expect(stored.settings.sources[0].wishlistId).toBe(wishlist.id);
 await page.reload();await expect(page.locator('#prep-empty')).toContainText('Workflow saved');await expect(page.locator('#prep-editor')).toBeHidden();
});

test('lost candidate response retains retry identity across a newer automatic result',async({page})=>{
 await page.clock.install();const {definition,row}=await fixture(page);const commands=[];
 await page.route('**/api/v1/preparation/**/handoff',r=>json(r,{reviewId:workflow,universeSnapshotId:occurrence}));
 await page.route('**/api/v1/candidate-runs',r=>{commands.push(r.request().postDataJSON());return commands.length===1?r.abort('failed'):json(r,{candidateRunId:workflow});});
 await page.locator('#prep-handoff').click();await expect.poll(()=>commands.length).toBe(1);await expect(page.locator('.prep-page')).not.toHaveAttribute('aria-busy','true');
 const newer={...row,id:'33333333-3333-3333-3333-333333333333',snapshot:{...row.snapshot,revisionId:'33333333-3333-3333-3333-333333333333',cutoffUtc:new Date(Date.now()+10000).toISOString()}};
 await page.route('**/api/v1/preparation/'+workflow,r=>json(r,{definition,history:[newer,row],upcoming:[]}));await page.clock.fastForward(10000);await expect(page.locator('#prep-update')).toContainText('newer result');await page.locator('#prep-handoff').click();await expect.poll(()=>commands.length).toBe(2);expect(commands[1]).toEqual(commands[0]);
});

test('embedded preparation uses a versioned script and recovers a failed script load',async({page})=>{
 await signIn(page);let failed=false;await page.route('**/js/preparation.js*',r=>{if(!failed){failed=true;return r.abort('failed');}return r.continue();});
 await page.goto('/Desk');await expect(page.locator('#watch-preparation')).toHaveAttribute('data-script-url',/preparation\.js\?v=/);
 await page.locator('#watch-preparation>summary').click();await expect(page.locator('#prep-retry')).toBeVisible();await expect(page.locator('#prep-status')).toContainText('could not load');await page.locator('#prep-retry').click();await expect(page.locator('#prep-new')).toBeEnabled();await expect(page.locator('#prep-status')).not.toContainText('could not load');
});

test('the page explains itself: status tiles, one time format, and why continuing is unavailable',async({page})=>{
 await fixture(page);
 await expect(page.getByRole('heading',{name:'Morning preparation'})).toBeVisible();
 await expect(page.locator('#prep-tiles')).toBeVisible();await expect(page.locator('#prep-tile-latest')).toHaveText('Ready · 1 stock');
 await expect(page.locator('#prep-audit')).toContainText('New York');await expect(page.locator('#prep-audit')).not.toContainText(' local');
 await expect(page.locator('#prep-handoff')).toBeEnabled();await expect(page.locator('#prep-handoff-reason')).toBeHidden();
 await page.getByRole('checkbox',{name:'Include MSFT'}).uncheck();
 await expect(page.locator('#prep-handoff')).toBeDisabled();await expect(page.locator('#prep-handoff-reason')).toHaveText('Tick at least one ready stock to continue.');
 await page.getByRole('button',{name:'Workflow settings',exact:true}).click();
 await expect(page.locator('#prep-basic-fields input')).toHaveCount(4);await expect(page.locator('#prep-fields input').first()).toBeHidden();
 await page.getByText('Advanced settings',{exact:true}).click();await expect(page.locator('#prep-fields input').first()).toBeVisible();
});
