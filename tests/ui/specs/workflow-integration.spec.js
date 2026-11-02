import fs from "node:fs";
import path from "node:path";
import { expect, test } from "@playwright/test";
import { signIn } from "./sign-in.js";
const runId="11111111-1111-1111-1111-111111111111",candidateId="22222222-2222-2222-2222-222222222222",universeId="33333333-3333-3333-3333-333333333333";
const identity={strategyId:"swing-test",semanticVersion:"1.0.0",contentSha256:"a".repeat(64)};
const candidate={candidateId,version:3,symbol:"MSFT",strategy:identity,state:"triggered",readiness:"signal_ready",rejections:[]};
const snapshot=sequence=>({candidateRunId:runId,provenance:{mode:"paper_shadow",universeSnapshotId:universeId,strategies:[identity]},status:"running",latestEventSequence:sequence,candidates:[candidate]});
const json=(route,value,status=200)=>route.fulfill({status,contentType:"application/json",body:JSON.stringify(value)});
// Mirrors the plan API without starting jobs or mutating the isolated host's source lists.
const planPreview=run=>({planId:0,run:{...run,configurationSha256:'b'.repeat(64)},configurationSha256:'b'.repeat(64),executionCount:1,membershipDescription:'Reviewed source stocks.',
 cohorts:[{name:'Group 1',symbols:['MSFT'],startingCapital:run.startingCapital}]});
// Each case creates its own source in the harness-owned database; teardown deletes that database and all related files.
async function openBacktestFixture(page){
 await page.goto('/Wishlists');
 await page.locator('#CreateWishlistName').fill('Backtest fixture '+crypto.randomUUID());
 await page.getByRole('button',{name:'Add new',exact:true}).click();
 await page.waitForLoadState('domcontentloaded');
 await page.goto('/Backtests');
}
for(const viewport of [{width:1440,height:900},{width:412,height:915}]) {
 test("candidate replay preserves editing at "+viewport.width,async({page},testInfo)=>{
  await page.setViewportSize(viewport);await signIn(page);
  let reads=0;const consumed={...candidate,state:"consumed",readiness:"consumed"};
  await page.route("**/api/v1/strategies?*",route=>json(route,{strategies:[{identity,name:"Swing fixture"}]}));
  await page.route("**/api/v1/candidate-runs/"+runId,route=>json(route,snapshot(++reads===1?1:2)));
  await page.route("**/api/v1/candidate-runs/"+runId+"/events?*",route=>route.fulfill({status:200,contentType:"text/event-stream",
    body:"id: 3\nevent: candidate.changed\ndata: "+JSON.stringify({eventSequence:3,payload:{candidateRunId:runId,candidate:consumed}})+"\n\n"}));
  await page.goto("/TradeDesk?runId="+runId);
  await expect(page.locator("[data-candidate-rows] strong")).toContainText("consumed");
  await page.getByText("Capture or open a candidate run",{exact:true}).click();
  const input=page.locator('[data-candidate-open] input');await input.fill("unsaved run selection");await input.focus();
  await expect.poll(()=>reads,{timeout:15000}).toBeGreaterThan(1);
  await expect(page.locator("[data-candidate-rows] strong")).toContainText("consumed");
  await expect(input).toHaveValue("unsaved run selection");await expect(input).toBeFocused();
  await page.screenshot({path:testInfo.outputPath("desk-"+viewport.width+".png"),fullPage:true});
  await page.route("**/api/v1/candidates/"+candidateId+"/audit",route=>json(route,{candidate:consumed,transitions:[],gateEvaluations:[],marketEvidence:null,executionEvidence:null,
    catalystEvidence:{headline:"Attached earnings article",provider:"fixture",providerArticleId:"article-exact",url:"https://example.com/attached-article"}}));
  await page.getByRole("link",{name:"Audit and attached news"}).click();
  await expect(page.getByText("Market inputs incomplete.")).toBeVisible();
  await expect(page.getByRole("link",{name:"Open attached article"})).toHaveAttribute("href","https://example.com/attached-article");
  await page.screenshot({path:testInfo.outputPath("audit-"+viewport.width+".png"),fullPage:true});
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBeTruthy();
 });
}
test("lost override preview replays exact request and confirmation stays pending",async({page})=>{
 await signIn(page);let universeReads=0;const requests=[];
 await page.route("**/api/v1/orders/context",route=>json(route,{accountId:"fixture-account",operatorOverrideAllowed:true}));
 await page.route("**/api/v1/strategies?*",route=>json(route,{strategies:[{identity,name:"Swing fixture"}]}));
 await page.route("**/api/v1/universes/preview",route=>{universeReads++;return json(route,{universeSnapshotId:universeId});});
 await page.route("**/api/v1/orders/preview",route=>{
  const body=route.request().postDataJSON();requests.push(body);if(requests.length===1)return route.abort("failed");
  return json(route,{...body,previewId:candidateId,previewToken:"protected-fixture",estimatedNotional:100,maximumLoss:2,quoteTimestampUtc:"2026-09-27T12:00:00Z",expiresAtUtc:"2026-09-27T12:02:00Z",warnings:[]});
 });
 await page.route("**/api/v1/orders/confirm",route=>json(route,{state:"confirmation_pending",rejectionReason:"Awaiting reconciliation"},202));
 await page.goto("/OrderTicket?wishlistId="+universeId+"&ticker=MSFT");
 for(const [name,value] of [["limit","100"],["stop","98"],["target","106"]])await page.locator('[name="'+name+'"]').fill(value);
 await page.getByRole("button",{name:"Review order",exact:true}).click();await expect.poll(()=>requests.length).toBe(1);
 await expect(page.locator("#OrderWorkflowForm")).not.toHaveAttribute("inert");
 await page.getByRole("button",{name:"Review order",exact:true}).click();await expect(page.locator("[data-order-review]")).toBeVisible();
 expect(requests[1]).toEqual(requests[0]);expect(universeReads).toBe(1);
 await page.getByRole("button",{name:"Confirm paper order"}).click();
 await expect(page.locator("[data-order-status]")).toContainText("confirmation_pending");
 await expect(page.getByRole("button",{name:"Check same command"})).toBeEnabled();
 await expect(page.getByRole("button",{name:"Edit",exact:true})).toBeDisabled();
});
for(const width of [1440,412]) test("real decision replay audit at "+width,async({page},testInfo)=>{
 const fixture=JSON.parse(fs.readFileSync(path.resolve(process.cwd(),"../../src/TradingFlow.Tests/Fixtures/Contracts/decision_replay_news.json"),"utf8"));
 await page.setViewportSize({width,height:width===412?915:900});await signIn(page);
 await page.route("**/api/v1/candidates/"+fixture.candidate.candidateId+"/audit",route=>json(route,fixture));
 await page.goto("/CandidateAudit?candidateId="+fixture.candidate.candidateId);
 await expect(page.locator("[data-audit-status]")).toHaveText("RGTI · in trade");
 await expect(page.getByRole("link",{name:"Open attached article"})).toHaveAttribute("href",fixture.catalystEvidence.url);
 await expect(page.locator("[data-audit-content]")).toContainText("filled: 4");
 await expect(page.locator("[data-audit-content]")).toContainText("samples: 20");
 await expect(page.locator("[data-audit-content] details")).not.toHaveCount(0);
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBeTruthy();
 await page.screenshot({path:testInfo.outputPath("replay-audit-"+width+".png"),fullPage:true});
});

// Keeps normal empty-exclusion behavior and captured window identity across a failed preview transport.
for(const exactWindow of [false,true]) test("backtest retry preserves the "+(exactWindow?'custom UTC':'lookback')+" window and immutable universe",async({page})=>{
 await signIn(page);await openBacktestFixture(page);
 const strategy=page.locator('input[name="StrategyPaths"]').first();await strategy.check();
 const selectedPath=await strategy.inputValue();
 let captures=0;const requests=[];
 await page.route("**/api/v1/catalog",route=>json(route,{strategies:[{path:selectedPath,horizon:"swing",artifactStrategyId:identity.strategyId,semanticVersion:identity.semanticVersion,contentSha256:identity.contentSha256}]}));
 await page.route("**/api/v1/universes/preview",route=>{captures++;return json(route,{universeSnapshotId:universeId});});
 await page.locator('[name="GroupSize"]').fill('5');
 await page.route("**/api/v1/backtests/plans/preview",route=>{
  requests.push(route.request().postDataJSON());
  return requests.length===1?route.abort("failed"):json(route,planPreview(requests.at(-1).run));
 });
 if(exactWindow){
  await page.getByLabel('From UTC',{exact:true}).fill('2026-01-15T09:30');
  await page.getByLabel('To UTC',{exact:true}).fill('2026-06-15T16:00');
 }
 await page.getByRole("button",{name:"Preview groups",exact:true}).click();
 await expect.poll(()=>requests.length).toBe(1);
 await expect(page.locator("[data-run-workflow-status]")).toContainText("Retry keeps the same command");
 await page.getByRole("button",{name:"Preview groups",exact:true}).click();
 await expect(page.locator('[data-backtest-plan-preview]')).toContainText('Group 1: MSFT');
 expect(requests[1]).toEqual(requests[0]);expect(captures).toBe(1);
 expect(requests[0].excludedSymbols).toEqual([]);
 await expect(page.locator('[data-backtest-plan-preview]')).toContainText('Excluded stocks: None');
 expect(requests[0].run.universeSnapshotId).toBe(universeId);
 expect(requests[0].run.strategies).toEqual([identity]);
 if(exactWindow){expect(requests[0].run.testWindowStartUtc).toBe('2026-01-15T09:30:00.000Z');expect(requests[0].run.testWindowEndUtc).toBe('2026-06-15T16:00:00.000Z');}
 else expect(Date.parse(requests[0].run.testWindowEndUtc)-Date.parse(requests[0].run.testWindowStartUtc)).toBe(180*86400000);
});

test.describe('research validation in a non-UTC device timezone',()=>{
 test.use({timezoneId:'America/New_York',viewport:{width:390,height:844}});
 test('errors preserve values and prevent a universe capture, then UTC dates submit unchanged',async({page})=>{
  await signIn(page);await openBacktestFixture(page);
  const strategy=page.locator('input[name="StrategyPaths"]').first();await strategy.check();const selectedPath=await strategy.inputValue();
  let captures=0,request;
  await page.route('**/api/v1/catalog',route=>json(route,{strategies:[{path:selectedPath,horizon:"swing",artifactStrategyId:identity.strategyId,semanticVersion:identity.semanticVersion,contentSha256:identity.contentSha256}]}));
  await page.route('**/api/v1/universes/preview',route=>{captures++;return json(route,{universeSnapshotId:universeId});});
  await page.locator('[name="GroupSize"]').fill('5');
  await page.route('**/api/v1/backtests/plans/preview',route=>{request=route.request().postDataJSON().run;return json(route,planPreview(request));});
  await page.getByLabel('From UTC',{exact:true}).fill('2026-06-16T09:30');await page.getByLabel('To UTC',{exact:true}).fill('2026-06-15T16:00');
  await page.getByLabel('Account risk (% of equity)',{exact:true}).fill('101');
  await page.getByLabel('Run name',{exact:true}).fill('My research run');
  await page.getByRole('button',{name:'Preview groups',exact:true}).click();
  const summary=page.locator('[data-run-validation]');await expect(summary).toBeVisible();await expect(summary).toBeFocused();
  await expect(summary).toContainText('To UTC must be later');await expect(summary).toContainText('at most 100%');
  await expect(summary).toContainText('letters, digits');await expect(page.locator('#RunName')).toHaveAttribute('aria-invalid','true');
  await expect(page.locator('#AccountRiskBudgetPct')).toHaveAttribute('aria-invalid','true');await expect(page.locator('#AccountRiskBudgetPct')).toHaveValue('101');expect(captures).toBe(0);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBeTruthy();
  await page.getByLabel('From UTC',{exact:true}).fill('2099-06-16T09:30');await page.getByLabel('To UTC',{exact:true}).fill('2099-06-15T16:00');
  await page.getByRole('button',{name:'Preview groups',exact:true}).click();await expect(summary).toContainText('cannot be in the future');
  const describedIds=(await page.locator('#TestWindowEndUtc').getAttribute('aria-describedby')).split(' ');
  expect(new Set(describedIds).size).toBe(describedIds.length);for(const id of describedIds)await expect(page.locator('[id="'+id+'"]').first()).toBeAttached();
  await page.getByLabel('From UTC',{exact:true}).fill('2026-01-15T09:30');await page.getByLabel('Account risk (% of equity)',{exact:true}).fill('1');
  await page.getByLabel('To UTC',{exact:true}).fill('2026-06-15T16:00');
  await page.getByLabel('Run name',{exact:true}).fill(' reviewed-research ');
  await page.getByRole('button',{name:'Preview groups',exact:true}).click();await expect(page.locator('[data-backtest-plan-preview]')).toContainText('Group 1: MSFT');
  expect(request.runName).toBe('reviewed-research');expect(request.testWindowStartUtc).toBe('2026-01-15T09:30:00.000Z');expect(request.testWindowEndUtc).toBe('2026-06-15T16:00:00.000Z');expect(captures).toBe(1);
 });
});

// Exercises the deliberate save/start boundary and a lost start response on the same saved comparison.
test('saving groups starts nothing and retrying start retains its command',async({page})=>{
 await signIn(page);
 let saved=[],starts=[];
 await page.route('**/api/v1/backtests/plans',route=>{
  if(route.request().method()==='GET')return json(route,saved);
  const run=route.request().postDataJSON().run;
  saved=[{...planPreview(run),planId:17,savedAtUtc:'2026-10-01T12:00:00Z'}];
  return json(route,saved[0]);
 });
 await openBacktestFixture(page);
 const strategy=page.locator('input[name="StrategyPaths"]').first();await strategy.check();
 const selectedPath=await strategy.inputValue();
 await page.route('**/api/v1/catalog',route=>json(route,{strategies:[{path:selectedPath,horizon:"swing",artifactStrategyId:identity.strategyId,semanticVersion:identity.semanticVersion,contentSha256:identity.contentSha256}]}));
 await page.route('**/api/v1/universes/preview',route=>json(route,{universeSnapshotId:universeId}));
 await page.route('**/api/v1/backtests/plans/preview',route=>json(route,planPreview(route.request().postDataJSON().run)));
 await page.route('**/api/v1/backtests/plans/17/start',route=>{
  starts.push(route.request().postDataJSON());
  return starts.length===1?route.abort('failed'):json(route,{jobs:[{jobId:runId,status:'queued',cohort:{cohortName:'Group 1',strategy:identity}}]});
 });
 await page.locator('[name="GroupSize"]').fill('5');
 await expect(page.getByRole('button',{name:'Save plan',exact:true})).toBeDisabled();
 await page.getByRole('button',{name:'Preview groups',exact:true}).click();
 await expect(page.getByRole('button',{name:'Save plan',exact:true})).toBeEnabled();
 await page.getByRole('button',{name:'Save plan',exact:true}).click();
 await expect(page.locator('[data-run-workflow-status]')).toContainText('No jobs have started');
 expect(starts).toHaveLength(0);
 const start=page.getByRole('button',{name:'Start saved plan',exact:true});await expect(start).toBeEnabled();
 await start.click();await expect.poll(()=>starts.length).toBe(1);await expect(start).toBeEnabled();
 await start.click();await expect(page.getByRole('link',{name:'Group 1 · swing-test · queued'})).toHaveAttribute('href','/Backtests?jobId='+runId);
 expect(starts[0]).toEqual(starts[1]);
});

// Exercises per-plan exclusions across retry, save, reopen and edit without changing a source list.
test('excluded stocks retain retry identity and reopen with the effective groups',async({page})=>{
 await signIn(page);
 let saved=[],captures=0;const previews=[],saves=[];
 await page.route('**/api/v1/backtests/plans',route=>{
  if(route.request().method()==='GET')return json(route,saved);
  const request=route.request().postDataJSON();saves.push(request);
  saved=[{...planPreview(request.run),excludedSymbols:['SOXL'],planId:19,savedAtUtc:'2026-10-01T12:00:00Z'}];
  return json(route,saved[0]);
 });
 await openBacktestFixture(page);
 const strategy=page.locator('input[name="StrategyPaths"]').first();await strategy.check();
 const selectedPath=await strategy.inputValue();
 await page.route('**/api/v1/catalog',route=>json(route,{strategies:[{path:selectedPath,horizon:"swing",artifactStrategyId:identity.strategyId,semanticVersion:identity.semanticVersion,contentSha256:identity.contentSha256}]}));
 await page.route('**/api/v1/universes/preview',route=>{captures++;return json(route,{universeSnapshotId:universeId});});
 await page.route('**/api/v1/backtests/plans/preview',route=>{
  const request=route.request().postDataJSON();previews.push(request);
  if(previews.length===1)return route.abort('failed');
  const excludedSymbols=request.excludedSymbols.map(symbol=>symbol.toUpperCase());
  const effective=planPreview(request.run);
  if(excludedSymbols.includes('MSFT'))effective.cohorts[0].symbols=['SOXL'];
  return json(route,{...effective,excludedSymbols});
 });
 await page.locator('[name="GroupSize"]').fill('5');
 await page.getByLabel('Exclude stocks',{exact:true}).fill(' soxl, ');
 const preview=page.getByRole('button',{name:'Preview groups',exact:true});
 await preview.click();await expect(page.locator('[data-run-workflow-status]')).toContainText('Retry keeps the same command');
 await preview.click();
 const groups=page.locator('[data-backtest-plan-preview]');
 await expect(groups).toContainText('Excluded stocks: SOXL');await expect(groups).toContainText('Group 1: MSFT');
 expect(previews[0].excludedSymbols).toEqual(['soxl']);expect(previews[1]).toEqual(previews[0]);expect(captures).toBe(1);
 await page.getByRole('button',{name:'Save plan',exact:true}).click();
 const start=page.getByRole('button',{name:'Start saved plan',exact:true});await expect(start).toBeEnabled();
 expect(saves[0].excludedSymbols).toEqual(['soxl']);expect(saves[0].run.idempotencyKey).toBe(previews[0].run.idempotencyKey);
 expect(saves[0].run.configurationSha256).toBe('b'.repeat(64));expect(captures).toBe(1);
 await page.getByLabel('Exclude stocks',{exact:true}).fill('MSFT');
 await expect(start).toBeDisabled();await expect(page.locator('[data-saved-backtest-plan]')).toHaveValue('');await expect(groups).toBeEmpty();
 await preview.click();await expect(groups).toContainText('Excluded stocks: MSFT');await expect(groups).toContainText('Group 1: SOXL');
 expect(previews[2].excludedSymbols).toEqual(['MSFT']);expect(previews[2].run.idempotencyKey).not.toBe(previews[0].run.idempotencyKey);expect(captures).toBe(2);
 await page.goto('/Backtests');await page.locator('[data-saved-backtest-plan]').selectOption('19');
 await expect(start).toBeEnabled();await expect(groups).toContainText('Excluded stocks: SOXL');await expect(groups).toContainText('Group 1: MSFT');
});

test('retired optimization form cannot create a YAML artifact',async({page})=>{
 await signIn(page);await page.goto('/Backtests');
 const root=path.resolve(process.cwd(),'../../configs/optimization/ui-runs');
 const files=()=>fs.existsSync(root)?fs.readdirSync(root).sort():[];const before=files();
 const token=await page.locator('input[name="__RequestVerificationToken"]').first().inputValue();
 const response=await page.request.post('/Backtests?handler=Optimize',{form:{__RequestVerificationToken:token,RunName:'retired-form-check',TargetRMultipleCsv:'1\nunsafe: true'}});
 expect(response.status()).not.toBe(500);expect(response.headers().location||'').not.toContain('/OptimizationJob');expect(files()).toEqual(before);
});

// Uses a saved screen only in the harness database, then verifies its exact filters reach shared source capture.
test('saved Finviz selection captures its filters and retains the reviewed profile hash',async({page})=>{
 await signIn(page);await page.goto('/Desk');
 const screen=await page.evaluate(async()=>{
  const response=await window.tradingFlowFetch('/api/desk/screens',{method:'POST',headers:{'Content-Type':'application/json'},
   body:JSON.stringify({name:'Source fixture '+crypto.randomUUID(),url:'https://elite.finviz.com/screener.ashx?f=sh_price_o20,ta_sma50_pa'})});
  if(!response.ok)throw new Error('Could not save isolated screen');return response.json();
 });
 await page.goto('/Backtests');
 const option=page.locator('#StockSource option').filter({hasText:'Source fixture'}).last();
 const source=await option.getAttribute('value');await page.locator('#StockSource').selectOption(source);
 const strategy=page.locator('input[name="StrategyPaths"]').first();await strategy.check();const selectedPath=await strategy.inputValue();
 await page.route('**/api/v1/catalog',route=>json(route,{strategies:[{path:selectedPath,horizon:"swing",artifactStrategyId:identity.strategyId,semanticVersion:identity.semanticVersion,contentSha256:identity.contentSha256}]}));
 let capture,save;
 await page.route('**/api/v1/universes/preview',route=>{capture=route.request().postDataJSON();return json(route,{universeSnapshotId:universeId});});
 await page.route('**/api/v1/backtests/plans/preview',route=>json(route,planPreview(route.request().postDataJSON().run)));
 await page.route('**/api/v1/backtests/plans',route=>{
  if(route.request().method()==='GET')return json(route,[]);
  save=route.request().postDataJSON();return json(route,{...planPreview(save.run),planId:18});
 });
 await page.locator('[name="GroupSize"]').fill('5');
 await page.getByRole('button',{name:'Preview groups',exact:true}).click();
 await expect(page.locator('[data-backtest-plan-preview]')).toContainText('Group 1: MSFT');
 expect(capture.sources).toHaveLength(1);expect(capture.sources[0].sourceKind).toBe('finviz');
 expect(capture.sources[0].sourceKey).toBe(screen.key);expect(capture.sources[0].query).toBe('sh_price_o20,ta_sma50_pa');
 await page.getByRole('button',{name:'Save plan',exact:true}).click();
 await expect.poll(()=>save?.run.configurationSha256).toBe('b'.repeat(64));
});
