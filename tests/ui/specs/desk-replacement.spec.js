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
  await page.route('**/api/v1/orders/replacement/review?*',route=>route.fulfill({json:{canReview:true,state:'Canceled',detail:'Known zero fills; fresh account evidence',reviewUrl:'/Desk?search=AAA&order=buy'}}));
  await page.locator('#ReplacementRefresh').click(); await expect(page.locator('#ReplacementReview')).toBeVisible();
  await expect(page.locator('#ReplacementReview')).toHaveAttribute('href','/Desk?search=AAA&order=buy');
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
