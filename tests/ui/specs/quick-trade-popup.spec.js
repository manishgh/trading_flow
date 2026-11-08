import {expect,test} from '@playwright/test';
import {signInAsAdmin} from './sign-in.js';

const strategy={strategyId:'popup-fixture',semanticVersion:'1.0.0',contentSha256:'a'.repeat(64)};
const wishlistId='33333333-3333-3333-3333-333333333333';
const json=(route,value,status=200)=>route.fulfill({status,json:value});

// Count only requests initiated by Trading or its ticket; sign-in can leave unrelated Desk catalog work in flight.
function countTicketRead(route,reads,key) {
    const referer=route.request().headers().referer;
    if(referer&&['/Orders','/OrderTicket'].includes(new URL(referer).pathname))reads[key]++;
}

// All ticket reads and writes use deterministic fixtures in the isolated browser-test host.
async function ticketFixtures(page,reads={context:0,catalog:0,sources:0}) {
    await page.route('**/api/v1/orders/context',route=>{countTicketRead(route,reads,'context');return json(route,{accountId:'popup-account',operatorOverrideAllowed:true});});
    await page.route('**/api/v1/strategies?*',route=>{countTicketRead(route,reads,'catalog');return json(route,{strategies:[{identity:strategy,name:'Popup strategy'}]});});
    await page.route('**/api/desk/sources',route=>{countTicketRead(route,reads,'sources');return json(route,[{key:'wishlist:'+wishlistId,name:'Popup stocks'}]);});
    await page.route('**/api/v1/universes/preview',route=>json(route,{universeSnapshotId:wishlistId}));
}

// Opening and closing must never submit an order or fetch the same ticket twice.
for(const width of [1440,412]) test('popup defers ticket reads and restores keyboard focus at '+width,async({page},testInfo)=>{
    await page.setViewportSize({width,height:760});
    await signInAsAdmin(page);
    const reads={context:0,catalog:0,sources:0};let writes=0;
    await ticketFixtures(page,reads);
    page.on('request',request=>{if(request.method()==='POST'&&/\/api\/v1\/(orders|positions)/.test(request.url()))writes++;});
    await page.goto('/Orders?filter=working&sort=symbol&dir=desc');
    await expect(page.locator('#OrderPopup')).toBeHidden();
    expect(reads).toEqual({context:0,catalog:0,sources:0});
    await page.locator('#OrdersNewTicker').fill('MSFT');
    const opener=page.getByRole('button',{name:'Open ticket',exact:true});
    await opener.click();
    const ticket=page.frameLocator('#OrderPopupFrame');
    // Await positive initialization evidence, rather than a missing attribute before deferred JS runs.
    await expect(ticket.locator('[data-order-status]')).toHaveText('Administrator operator override; normal risk checks still apply.');
    await expect.poll(()=>({...reads})).toEqual({context:1,catalog:1,sources:1});
    await expect(ticket.locator('#OrderWorkflowForm')).not.toHaveAttribute('inert');
    await expect(page.locator('#OrderPopupClose')).toBeFocused();
    await page.keyboard.press('Shift+Tab');
    await expect(ticket.locator('[data-order-candidates]')).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(page.locator('#OrderPopupClose')).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(ticket.locator('[name=symbol]')).toBeFocused();
    await page.keyboard.press('Shift+Tab');
    await expect(page.locator('#OrderPopupClose')).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(ticket.locator('[name=symbol]')).toBeFocused();
    await ticket.locator('[name=quantity]').fill('7');
    await page.keyboard.press('Escape');
    await expect(page.locator('#OrderPopup')).toBeHidden();
    await expect(opener).toBeFocused();
    await opener.click();
    await expect(ticket.locator('[name=quantity]')).toHaveValue('7');
    expect(reads).toEqual({context:1,catalog:1,sources:1});
    await expect(page).toHaveURL(/filter=working&sort=symbol&dir=desc$/);
    // The embedded workflow shares the shell's palette in both themes and keeps full-size controls.
    for(const scheme of ['light','dark']){
        await page.evaluate(value=>window.TradingFlowTheme.set(value),scheme);
        await ticket.locator('body').evaluate((_,value)=>window.TradingFlowTheme.set(value),scheme);
        const primary=ticket.locator('#OrderWorkflowForm button[type=submit]');
        const parentPrimary=page.locator('.orders-new-order button[type=submit]');
        expect(await primary.evaluate(node=>getComputedStyle(node).backgroundColor)).toBe(await parentPrimary.evaluate(node=>getComputedStyle(node).backgroundColor));
        expect(await primary.evaluate(node=>getComputedStyle(node).borderRadius)).toBe('8px');
        expect((await primary.boundingBox()).height).toBeGreaterThanOrEqual(44);
        expect(await ticket.locator('[name=quantity]').evaluate(node=>getComputedStyle(node).fontSize)).toBe('16px');
        await page.screenshot({path:testInfo.outputPath('popup-theme-'+scheme+'-'+width+'.png')});
    }
    // Native focus trapping wraps the final embedded link back to the close control.
    await ticket.locator('[data-order-candidates]').focus();
    await page.keyboard.press('Tab');
    await expect(page.locator('#OrderPopupClose')).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(page.locator('#OrderPopup')).toBeHidden();
    expect(writes).toBe(0);
});

// Lost confirmation responses retain the exact command through close, resume and target-switch attempts.
test('pending confirmation survives close and refuses a different ticket',async({page})=>{
    await signInAsAdmin(page);await ticketFixtures(page);
    const previews=[],confirms=[];
    await page.route('**/api/v1/orders/preview',route=>{
        const body=route.request().postDataJSON();previews.push(body);
        return json(route,{...body,previewToken:'popup-preview-'+previews.length,estimatedNotional:200,maximumLoss:4,quoteTimestampUtc:'2026-11-08T10:00:00Z',expiresAtUtc:'2026-11-08T10:02:00Z',warnings:['Fixture warning remains visible']});
    });
    await page.route('**/api/v1/orders/confirm',route=>{
        confirms.push(route.request().postDataJSON());
        if(confirms.length===1)return route.abort('failed');
        return json(route,{state:'confirmation_pending',rejectionReason:'Awaiting reconciliation'},202);
    });
    await page.goto('/Orders?ticker=MSFT&wishlistId='+wishlistId);
    const ticket=page.frameLocator('#OrderPopupFrame');
    await expect(ticket.locator('#OrderWorkflowForm')).not.toHaveAttribute('inert');
    await ticket.locator('[name=limit]').fill('100');
    await ticket.getByText('Strategy & protection',{exact:true}).click();
    await ticket.locator('[name=stop]').fill('98');await ticket.locator('[name=target]').fill('106');
    await ticket.getByRole('button',{name:'Review order',exact:true}).click();
    await expect(ticket.locator('[data-order-review]')).toBeVisible();
    await expect(ticket.locator('[data-order-summary]')).toContainText('Expires 2026-11-08T10:02:00Z');
    await expect(ticket.locator('[data-order-summary]')).toContainText('Fixture warning remains visible');
    await expect(ticket.locator('[data-order-technical-summary]')).toBeHidden();
    await page.locator('#OrderPopupClose').click();
    await page.locator('#OrderPopupResume').click();
    await expect(ticket.locator('[data-order-review]')).toBeVisible();
    expect(previews).toHaveLength(1);
    await ticket.getByRole('button',{name:'Confirm paper order',exact:true}).click();
    await expect(ticket.locator('[data-order-status]')).toContainText('Retry checks the same command');
    await page.locator('#OrderPopupClose').click();
    await page.locator('#OrdersNewTicker').fill('NVDA');await page.getByRole('button',{name:'Open ticket',exact:true}).click();
    await expect(page.locator('#OrderPopupNotice')).toContainText('unresolved request');
    await expect(page.locator('#OrderPopupTitle')).toHaveText('Buy MSFT');
    await ticket.getByRole('button',{name:'Confirm paper order',exact:true}).click();
    await expect(ticket.getByRole('button',{name:'Check same command',exact:true})).toBeEnabled();
    expect(confirms).toHaveLength(2);expect(confirms[1]).toEqual(confirms[0]);
    await expect(ticket.getByRole('button',{name:'Edit',exact:true})).toBeDisabled();
    await page.locator('#OrderPopupClose').click();await page.locator('#OrderPopupResume').click();
    await expect(ticket.getByRole('button',{name:'Check same command',exact:true})).toBeEnabled();
    expect(previews).toHaveLength(1);expect(confirms).toHaveLength(2);
});

// A rejected command may be edited; a later uncertain confirmation must lock the new command again.
test('rejected edit starts a fresh guarded review',async({page})=>{
    await signInAsAdmin(page);await ticketFixtures(page);
    let confirms=0;
    await page.route('**/api/v1/positions/close',route=>json(route,++confirms===1?{state:'rejected',rejectionReason:'Fixture expiry'}:{state:'confirmation_pending'},confirms===1?200:202));
    await page.goto('/Orders?ticker=MSFT&side=sell&limitPrice=100');
    const ticket=page.frameLocator('#OrderPopupFrame');
    await expect(ticket.locator('#OrderWorkflowForm')).not.toHaveAttribute('inert');
    await ticket.getByRole('button',{name:'Review order',exact:true}).click();
    await ticket.getByRole('button',{name:'Confirm paper order',exact:true}).click();
    await expect(ticket.getByRole('button',{name:'Edit',exact:true})).toBeEnabled();
    await ticket.getByRole('button',{name:'Edit',exact:true}).click();
    await ticket.getByRole('button',{name:'Review order',exact:true}).click();
    await ticket.getByRole('button',{name:'Confirm paper order',exact:true}).click();
    await expect(ticket.getByRole('button',{name:'Check same command',exact:true})).toBeEnabled();
    await page.locator('#OrderPopupClose').click();
    await page.locator('#OrdersNewTicker').fill('NVDA');await page.getByRole('button',{name:'Open ticket',exact:true}).click();
    await expect(page.locator('#OrderPopupTitle')).toHaveText('Sell MSFT');
    await expect(page.locator('#OrderPopupNotice')).toBeVisible();
    expect(confirms).toBe(2);
});

// Invalid fields inside disclosures must be exposed before browser focus and without an API write.
test('invalid collapsed protection reveals its field before review',async({page})=>{
    await signInAsAdmin(page);await ticketFixtures(page);
    await page.goto('/Orders?ticker=MSFT&wishlistId='+wishlistId);
    const ticket=page.frameLocator('#OrderPopupFrame');
    await expect(ticket.locator('#OrderWorkflowForm')).not.toHaveAttribute('inert');
    await ticket.locator('[name=limit]').fill('100');
    await ticket.getByText('Strategy & protection',{exact:true}).click();
    await ticket.locator('[name=stop]').fill('-1');
    await ticket.getByText('Strategy & protection',{exact:true}).click();
    await ticket.getByRole('button',{name:'Review order',exact:true}).click();
    await expect(ticket.locator('[name=stop]')).toBeVisible();
    await expect(ticket.locator('[name=stop]')).toBeFocused();
    await expect(ticket.locator('[data-order-review]')).toBeHidden();
});
