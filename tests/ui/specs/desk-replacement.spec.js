import { expect, test } from '@playwright/test';
import { signIn } from './sign-in.js';

test('unissued replacement closes cleanly and known undispatched rejection can end review',async({page})=>{
  await signIn(page); await page.goto('/Orders');
  await page.evaluate(()=>{
    for (const id of ['first-draft','second-draft']) {
      const button=document.createElement('button'); button.dataset.replacementOrder=id;
      button.textContent=id; document.querySelector('main').append(button);
    }
  });
  await page.getByRole('button',{name:'first-draft',exact:true}).click();
  await page.locator('#ReplacementClose').click();
  await expect(page.locator('#ReplacementResume')).toBeHidden();
  await page.getByRole('button',{name:'second-draft',exact:true}).click();
  await expect(page.locator('#ReplacementIdentity')).toContainText('second-draft');
  await page.route('**/api/v1/orders/replacement/cancel',route=>route.fulfill({json:{canReview:false,canRelease:true,state:'Rejected',detail:'Command expired before dispatch',reviewUrl:null}}));
  await page.locator('#ReplacementCancel').click();
  await expect(page.locator('#ReplacementStatus')).toContainText('expired before dispatch');
  await expect(page.locator('#ReplacementReview')).toBeHidden();
  await page.locator('#ReplacementRelease').click();
  await expect(page.locator('#ReplacementDialog')).not.toBeVisible();
  await expect(page.locator('#ReplacementResume')).toBeHidden();
});

test('desk pages bound selection and presets stay scoped to environment',async({page})=>{
  await signIn(page);
  const group=await page.evaluate(async()=>{
    const create=await window.tradingFlowFetch('/api/v1/wishlists',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:`Paged fixture ${Date.now()}`})});
    const group=await create.json();
    const response=await window.tradingFlowFetch('/api/v1/wishlists/bulk',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({commandId:crypto.randomUUID(),operation:'add',targetId:group.id,tickers:Array.from({length:61},(_,i)=>`PG${i}`)})});
    if(!response.ok) throw new Error('Fixture membership failed'); return group;
  });
  try {
    await page.goto(`/TradeDesk?id=${group.id}&pageSize=25&sort=ticker&tracked=all`);
    await expect(page.locator('.trade-desk-table [data-symbol-row]')).toHaveCount(25);
    await expect(page.getByRole('navigation',{name:'Market list pages'})).toContainText('Page 1 of 3');
    await page.locator('[data-bulk-toggle-all]').check();
    await expect(page.locator('[data-bulk-count]')).toHaveText('25');
    await page.getByRole('navigation',{name:'Market list pages'}).getByRole('link',{name:'Next'}).click();
    await expect(page.locator('[data-bulk-item]:checked')).toHaveCount(0);
    await expect(page.getByRole('navigation',{name:'Market list pages'})).toContainText('Page 2 of 3');
    await expect(page.locator('.trade-desk-table [data-symbol-row]')).toHaveCount(25);
    await page.locator('#DeskPresetName').fill('Fixture filters'); await page.locator('#DeskPresetSave').click();
    await expect(page.locator('#DeskPreset option')).toHaveCount(2);
    expect(await page.evaluate(()=>{
      const root=document.querySelector('[data-desk-root]');
      const key=`tradingflow.desk.${root.dataset.operatorId}.${root.dataset.environment}.filters`;
      return {environment:root.dataset.environment,presets:JSON.parse(localStorage.getItem(key))};
    })).toMatchObject({environment:'paper',presets:[{name:'Fixture filters'}]});
    await page.goto('/TradeDesk?env=live');
    await expect(page.locator('.environment-lock')).toBeVisible();
    await expect(page.locator('#DeskPreset')).toHaveCount(0);
    await page.goto(`/TradeDesk?id=${group.id}&pageSize=25`);
    await expect(page.locator('#DeskPreset option')).toHaveCount(2);
    await page.goto(`/TradeDesk?id=${group.id}&pageNumber=999999&pageSize=25`);
    await expect(page.getByRole('navigation',{name:'Market list pages'})).toContainText('Page 3 of 3');
    await expect(page.locator('.trade-desk-table [data-symbol-row]')).toHaveCount(11);
    const invalid=await page.request.get('/TradeDesk?minChange=2&maxChange=1'); expect(invalid.status()).toBe(400);
    expect((await page.request.get('/TradeDesk?minChange=not-a-number')).status()).toBe(400);
    expect((await page.request.get('/TradeDesk?id=not-a-guid')).status()).toBe(400);
  } finally { await page.goto('/Wishlists'); await page.evaluate(async id=>window.tradingFlowFetch(`/api/v1/wishlists/${id}`,{method:'DELETE'}),group.id); }
});

test('replacement waits for acknowledgment and retains cancel identity after reload',async({page})=>{
  await signIn(page); await page.goto('/Orders');
  await page.evaluate(()=>{const b=document.createElement('button'); b.dataset.replacementOrder='original-fixture';b.textContent='Fixture Replace';document.querySelector('main').append(b);});
  const requests=[];
  await page.route('**/api/v1/orders/replacement/cancel',route=>{
    requests.push(route.request().postDataJSON()); return route.fulfill({json:{canReview:false,state:'CancelPending',detail:'Waiting for acknowledgment',reviewUrl:null}});
  });
  await page.getByRole('button',{name:'Fixture Replace'}).click();
  await page.locator('#ReplacementCancel').click(); await expect(page.locator('#ReplacementStatus')).toContainText('CancelPending');
  await expect(page.locator('#ReplacementReview')).toBeHidden();
  await page.locator('#ReplacementClose').click(); await page.reload(); await page.locator('#ReplacementResume').click();
  await expect(page.locator('#ReplacementIdentity')).toContainText('original-fixture');
  await page.locator('#ReplacementCancel').click(); await expect.poll(()=>requests.length).toBe(2); expect(requests[1]).toEqual(requests[0]);
  await page.route('**/api/v1/orders/replacement/review?*',route=>route.fulfill({json:{canReview:true,state:'Canceled',detail:'Known zero fills; fresh account evidence',reviewUrl:'/TradeDesk?ticker=AAA&ticket=true'}}));
  await page.locator('#ReplacementRefresh').click(); await expect(page.locator('#ReplacementReview')).toBeVisible();
  await expect(page.locator('#ReplacementReview')).toHaveAttribute('href','/TradeDesk?ticker=AAA&ticket=true');
  await expect(page.locator('#ReplacementCancel')).toBeDisabled();
});

test('audit rejects oversized multi-run and path requests before comparing',async({page})=>{
  await signIn(page);
  expect((await page.request.get('/Audit/one?compareRuns=two,three,four,five')).status()).toBe(400);
  expect((await page.request.get('/Audit/one?compareRuns=../private')).status()).toBe(400);
  await page.goto('/Audit/one?compareRuns=two,three');
  await expect(page.getByRole('heading',{name:'Realised equity overlay'})).toBeVisible();
  await expect(page.getByText('one: no persisted audit decisions found; no curve inferred.',{exact:true})).toBeVisible();
});
