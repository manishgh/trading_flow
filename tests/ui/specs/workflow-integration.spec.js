import fs from "node:fs";
import path from "node:path";
import { expect, test } from "@playwright/test";
import { signIn } from "./sign-in.js";
const runId="11111111-1111-1111-1111-111111111111",candidateId="22222222-2222-2222-2222-222222222222",universeId="33333333-3333-3333-3333-333333333333";
const identity={strategyId:"swing-test",semanticVersion:"1.0.0",contentSha256:"a".repeat(64)};
const candidate={candidateId,version:3,symbol:"MSFT",strategy:identity,state:"triggered",readiness:"signal_ready",rejections:[]};
const snapshot=sequence=>({candidateRunId:runId,provenance:{mode:"paper_shadow",universeSnapshotId:universeId,strategies:[identity]},status:"running",latestEventSequence:sequence,candidates:[candidate]});
const json=(route,value,status=200)=>route.fulfill({status,contentType:"application/json",body:JSON.stringify(value)});
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

for(const exactWindow of [false,true]) test("backtest retry preserves the "+(exactWindow?'custom UTC':'lookback')+" window and immutable universe",async({page})=>{
 await signIn(page);await page.goto("/Backtests");
 const strategy=page.locator('input[name="StrategyPaths"]').first();await strategy.check();
 const selectedPath=await strategy.inputValue();
 let captures=0;const requests=[];
 await page.route("**/api/v1/catalog",route=>json(route,{strategies:[{path:selectedPath,artifactStrategyId:identity.strategyId,semanticVersion:identity.semanticVersion,contentSha256:identity.contentSha256}]}));
 await page.route("**/api/v1/universes/preview",route=>{captures++;return json(route,{universeSnapshotId:universeId});});
 await page.route("**/api/v1/backtests/runs",route=>{
  requests.push(route.request().postDataJSON());
  return requests.length===1?route.abort("failed"):json(route,{jobId:runId,runName:"fixture",status:"queued"});
 });
 if(exactWindow){
  await page.getByLabel('From UTC',{exact:true}).fill('2026-01-15T09:30');
  await page.getByLabel('To UTC',{exact:true}).fill('2026-06-15T16:00');
 }
 await page.getByRole("button",{name:"Run backtest",exact:true}).click();
 await expect.poll(()=>requests.length).toBe(1);
 await expect(page.locator("[data-run-workflow-status]")).toContainText("Retry keeps the same command");
 await page.getByRole("button",{name:"Run backtest",exact:true}).click();
 await expect(page.getByRole("link",{name:"Open run",exact:true})).toHaveAttribute("href","/Backtests?jobId="+runId);
 expect(requests[1]).toEqual(requests[0]);expect(captures).toBe(1);
 expect(requests[0].universeSnapshotId).toBe(universeId);
 expect(requests[0].strategies).toEqual([identity]);
 if(exactWindow){expect(requests[0].testWindowStartUtc).toBe('2026-01-15T09:30:00.000Z');expect(requests[0].testWindowEndUtc).toBe('2026-06-15T16:00:00.000Z');}
 else expect(Date.parse(requests[0].testWindowEndUtc)-Date.parse(requests[0].testWindowStartUtc)).toBe(180*86400000);
});

test.describe('research validation in a non-UTC device timezone',()=>{
 test.use({timezoneId:'America/New_York',viewport:{width:390,height:844}});
 test('errors preserve values and prevent a universe capture, then UTC dates submit unchanged',async({page})=>{
  await signIn(page);await page.goto('/Backtests');
  const strategy=page.locator('input[name="StrategyPaths"]').first();await strategy.check();const selectedPath=await strategy.inputValue();
  let captures=0,request;
  await page.route('**/api/v1/catalog',route=>json(route,{strategies:[{path:selectedPath,artifactStrategyId:identity.strategyId,semanticVersion:identity.semanticVersion,contentSha256:identity.contentSha256}]}));
  await page.route('**/api/v1/universes/preview',route=>{captures++;return json(route,{universeSnapshotId:universeId});});
  await page.route('**/api/v1/backtests/runs',route=>{request=route.request().postDataJSON();return json(route,{jobId:runId,runName:'validated',status:'queued'});});
  await page.getByLabel('From UTC',{exact:true}).fill('2026-06-16T09:30');await page.getByLabel('To UTC',{exact:true}).fill('2026-06-15T16:00');
  await page.getByLabel('Account risk (% of equity)',{exact:true}).fill('101');
  await page.getByLabel('Run name',{exact:true}).fill('My research run');
  await page.getByRole('button',{name:'Run backtest',exact:true}).click();
  const summary=page.locator('[data-run-validation]');await expect(summary).toBeVisible();await expect(summary).toBeFocused();
  await expect(summary).toContainText('To UTC must be later');await expect(summary).toContainText('at most 100%');
  await expect(summary).toContainText('letters, digits');await expect(page.locator('#RunName')).toHaveAttribute('aria-invalid','true');
  await expect(page.locator('#AccountRiskBudgetPct')).toHaveAttribute('aria-invalid','true');await expect(page.locator('#AccountRiskBudgetPct')).toHaveValue('101');expect(captures).toBe(0);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBeTruthy();
  await page.getByLabel('From UTC',{exact:true}).fill('2099-06-16T09:30');await page.getByLabel('To UTC',{exact:true}).fill('2099-06-15T16:00');
  await page.getByRole('button',{name:'Run backtest',exact:true}).click();await expect(summary).toContainText('cannot be in the future');
  const describedIds=(await page.locator('#TestWindowEndUtc').getAttribute('aria-describedby')).split(' ');
  expect(new Set(describedIds).size).toBe(describedIds.length);for(const id of describedIds)await expect(page.locator('[id="'+id+'"]').first()).toBeAttached();
  await page.getByLabel('From UTC',{exact:true}).fill('2026-01-15T09:30');await page.getByLabel('Account risk (% of equity)',{exact:true}).fill('1');
  await page.getByLabel('To UTC',{exact:true}).fill('2026-06-15T16:00');
  await page.getByLabel('Run name',{exact:true}).fill(' reviewed-research ');
  await page.getByRole('button',{name:'Run backtest',exact:true}).click();await expect(page.getByRole('link',{name:'Open run',exact:true})).toBeVisible();
  expect(request.runName).toBe('reviewed-research');expect(request.testWindowStartUtc).toBe('2026-01-15T09:30:00.000Z');expect(request.testWindowEndUtc).toBe('2026-06-15T16:00:00.000Z');expect(captures).toBe(1);
 });
});

test('retired optimization form cannot create a YAML artifact',async({page})=>{
 await signIn(page);await page.goto('/Backtests');
 const root=path.resolve(process.cwd(),'../../configs/optimization/ui-runs');
 const files=()=>fs.existsSync(root)?fs.readdirSync(root).sort():[];const before=files();
 const token=await page.locator('input[name="__RequestVerificationToken"]').first().inputValue();
 const response=await page.request.post('/Backtests?handler=Optimize',{form:{__RequestVerificationToken:token,RunName:'retired-form-check',TargetRMultipleCsv:'1\nunsafe: true'}});
 expect(response.status()).not.toBe(500);expect(response.headers().location||'').not.toContain('/OptimizationJob');expect(files()).toEqual(before);
});
