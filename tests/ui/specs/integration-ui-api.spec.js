import {test,expect} from "@playwright/test";
import {signIn} from "./sign-in.js";
const identity={strategyId:"fixture-exact",semanticVersion:"2.0.0",contentSha256:"b".repeat(64)};
const json=(route,value)=>route.fulfill({contentType:"application/json",body:JSON.stringify(value)});
test("authenticated read APIs and plan degrade honestly without providers",async({page})=>{
 await signIn(page);await page.goto("/Orders?ticker=MSFT");
 await expect(page.locator("[data-order-status]")).toContainText("authorized paper candidate");
 await expect(page.locator("#OrderWorkflowForm")).toBeHidden();
 await expect(page.locator("[data-order-candidates]")).toBeVisible();
 const result=await page.evaluate(async()=>{
  const read=async(path)=>{const r=await window.tradingFlowFetch(path);return {status:r.status,body:await r.json()};};
  const r=await window.tradingFlowFetch("/api/v1/orders/plan",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({contractVersion:"1.0",symbol:"MSFT",strategy:null})});
  return {plan:{status:r.status,body:await r.json()},portfolio:await read("/api/v1/portfolio"),journal:await read("/api/v1/orders/journal")};
 });
 expect(result.plan.status).toBe(200);expect(result.plan.body.stop).toBeNull();expect(result.plan.body.target).toBeNull();expect(result.plan.body.stopNote).toBeTruthy();
 expect(result.portfolio.status).toBe(200);expect(result.portfolio.body.available).toBe(false);expect(result.portfolio.body.positions).toEqual([]);
 expect(result.journal.status).toBe(200);expect(result.journal.body).toEqual([]);
});
for(const width of [1440,412])test("shared strategy prices and layout at "+width,async({page},testInfo)=>{
 await page.setViewportSize({width,height:width===412?915:900});await signIn(page);
 await page.goto("/Desk");await expect(page.locator(".watch-desk")).toBeVisible();
 await page.screenshot({path:testInfo.outputPath("stock-desk-"+width+".png"),fullPage:true});
 await page.route("**/api/v1/orders/context",route=>json(route,{accountId:"fixture",operatorOverrideAllowed:true}));
 await page.route("**/api/v1/strategies?*",route=>json(route,{strategies:[{identity,name:"Exact fixture"}]}));
 let request;
 await page.route("**/api/v1/orders/plan",route=>{request=route.request().postDataJSON();return json(route,{contractVersion:"1.0",symbol:"MSFT",strategy:identity,limit:100,stop:97,target:109,riskBudget:30,stopNote:"Exact strategy completed-bar stop",targetNote:"Configured reward-to-risk target"});});
 await page.goto("/Orders?ticker=MSFT");await expect(page.locator("#OrderWorkflowForm")).not.toHaveAttribute("inert");
 await page.getByRole("button",{name:"Use strategy prices"}).click();
 await expect(page.locator('[name=stop]')).toHaveValue("97");await expect(page.locator('[name=target]')).toHaveValue("109");
 expect(request.strategy).toEqual(identity);
 await page.getByRole("button",{name:"Size to 1R"}).click();await expect(page.locator('[name=quantity]')).toHaveValue("10");
 await page.evaluate(()=>window.scrollTo(0,0));
 await expect(page.locator(".skip-link")).not.toBeInViewport();
 await page.screenshot({path:testInfo.outputPath("orders-"+width+".png"),fullPage:true});
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBeTruthy();
});
