import {expect,test} from '@playwright/test';
import {signIn} from './sign-in.js';
for(const width of [412,1280,1440]) test('workspace navigation stays available at '+width,async({page},testInfo)=>{
 await page.setViewportSize({width,height:915});await signIn(page);await page.goto('/');await expect(page).toHaveURL(/Desk/);
 await expect(page.locator('#watch-source')).toContainText('All followed stocks');await page.screenshot({path:testInfo.outputPath('workspace-'+width+'.png'),fullPage:true});
 const nav=page.getByRole('navigation',{name:'Primary navigation'});
 await expect(nav.getByRole('link')).toHaveText(['Desk','Orders','Research','System']);
 for(const name of ['Desk','Orders','Research','System']){
  const link=nav.getByRole('link',{name,exact:true});await expect(link).toBeInViewport();await link.click();
  await expect(nav.getByRole('link',{name,exact:true})).toHaveAttribute('aria-current','true');
  await expect(page.getByRole('heading',{level:1})).toHaveCount(1);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBeTruthy();
 }
 await nav.getByRole('link',{name:'Desk',exact:true}).click();
 await page.getByRole('link',{name:'Open candidate review ↗'}).click();
 await expect(page.locator('[data-candidate-workflow]')).toBeVisible();await expect(page.locator('#DeskFilterForm')).not.toBeVisible();
 await page.getByRole('button',{name:'Market explorer',exact:true}).click();await expect(page.locator('#DeskFilterForm')).toBeVisible();
 await page.goto('/TradeDesk?ticker=MSFT');await expect(page.getByRole('button',{name:'Market explorer',exact:true})).toHaveAttribute('aria-pressed','true');
});

test('audit return keeps its candidate run',async({page})=>{
 await signIn(page);
 const run='11111111-1111-1111-1111-111111111111';
 await page.goto('/CandidateAudit?candidateId=22222222-2222-2222-2222-222222222222&runId='+run+'&env=paper');
 const link=page.getByRole('link',{name:'Return to candidates'});
 await expect(link).toHaveAttribute('href','/TradeDesk?runId='+run+'&env=paper');
});
