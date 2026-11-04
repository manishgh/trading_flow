import {expect,test} from '@playwright/test';
import {signIn} from './sign-in.js';
// Three top tabs; the signed-in Trading Desk is the landing page and owns the Desk, wishlist, order and research pages.
for(const width of [412,1280,1440]) test('trading desk navigation stays available at '+width,async({page},testInfo)=>{
 await page.setViewportSize({width,height:915});await signIn(page);await page.goto('/');await expect(page).toHaveURL(/Desk/);
 await expect(page.locator('#watch-source')).toContainText('All wishlist stocks');await page.screenshot({path:testInfo.outputPath('workspace-'+width+'.png'),fullPage:true});
 const tabs=()=>page.getByRole('navigation',{name:'Primary navigation'});
 const sections=()=>page.getByRole('navigation',{name:'Trading Desk pages'});
 await expect(tabs().getByRole('link')).toHaveText(['News','Earnings','Trading Desk']);
 await expect(tabs().getByRole('link',{name:'Trading Desk',exact:true})).toHaveAttribute('aria-current','page');
 await expect(sections().getByRole('link')).toHaveText(['Desk','Manage wishlists','Orders','Research']);
 for(const name of ['Desk','Manage wishlists','Orders','Research']){
  const link=sections().getByRole('link',{name,exact:true});await expect(link).toBeInViewport();await link.click();
  await expect(sections().getByRole('link',{name,exact:true})).toHaveAttribute('aria-current','page');
  await expect(page.getByRole('heading',{level:1})).toHaveCount(1);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBeTruthy();
 }
 for(const name of ['News','Earnings']){
  await tabs().getByRole('link',{name,exact:true}).click();
  await expect(tabs().getByRole('link',{name,exact:true})).toHaveAttribute('aria-current','page');
  await expect(sections()).toHaveCount(0);
 }
});

test('audit return keeps its candidate run',async({page})=>{
 await signIn(page);
 const run='11111111-1111-1111-1111-111111111111';
 await page.goto('/CandidateAudit?candidateId=22222222-2222-2222-2222-222222222222&runId='+run+'&env=paper');
 const link=page.getByRole('link',{name:'Return to candidates'});
 await expect(link).toHaveAttribute('href','/TradeDesk?runId='+run+'&env=paper');
});
