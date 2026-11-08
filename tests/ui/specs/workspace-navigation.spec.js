import {expect,test} from '@playwright/test';
import {signIn} from './sign-in.js';
// One persistent destination list works on public screens, operator screens and phones.
for(const width of [320,412,1280,1440]) test('workspace navigation stays available at '+width,async({page},testInfo)=>{
 await page.setViewportSize({width,height:915});await signIn(page);await page.goto('/');await expect(page).toHaveURL(/Desk/);
 await expect(page.locator('#watch-source')).toContainText('All wishlist stocks');
 const navigation=()=>page.getByRole('navigation',{name:'Primary navigation'});
 const destinations=[['Desk','/Desk'],['Trading','/Orders'],['Strategies','/Paper'],['Earnings','/Earnings'],['News','/News'],['Stock groups','/Wishlists']];
 await expect(navigation().getByRole('link')).toHaveText(destinations.map(([name])=>name));
 for(const [name,path] of destinations){
  const link=navigation().getByRole('link',{name,exact:true});await expect(link).toBeInViewport();await expect(link).toHaveAttribute('href',path);await link.click();
  await expect(navigation().getByRole('link')).toHaveText(destinations.map(([label])=>label));
  await expect(navigation().getByRole('link',{name,exact:true})).toHaveAttribute('aria-current','page');
  await expect(navigation().locator('[aria-current="page"]')).toHaveCount(1);
  await expect(page.getByRole('heading',{level:1})).toHaveCount(1);
  await expect(page.locator('.section-nav')).toHaveCount(0);
  if(name==='Strategies'){await page.evaluate(()=>window.scrollTo(0,0));await page.screenshot({path:testInfo.outputPath('strategy-runs-'+width+'.png'),fullPage:true});}
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBeTruthy();
 }
 await navigation().getByRole('link',{name:'Strategies',exact:true}).click();
 const strategyViews=page.getByRole('navigation',{name:'Strategy views'});
 await expect(strategyViews.getByRole('link')).toHaveText(['Strategy runs','Backtests']);
 await expect(strategyViews.getByRole('link',{name:'Strategy runs',exact:true})).toHaveAttribute('aria-current','page');
 await strategyViews.getByRole('link',{name:'Backtests',exact:true}).click();
 await expect(page).toHaveURL(/\/Backtests$/);
 await page.evaluate(()=>window.scrollTo(0,0));await page.screenshot({path:testInfo.outputPath('backtests-'+width+'.png'),fullPage:true});
 await expect(strategyViews.getByRole('link',{name:'Backtests',exact:true})).toHaveAttribute('aria-current','page');
 await expect(navigation().getByRole('link',{name:'Strategies',exact:true})).toHaveAttribute('aria-current','page');
 for(const [path,name] of [['/RunningTrades','Trading'],['/Backtests','Strategies']]){
  await page.goto(path);await expect(navigation().getByRole('link',{name,exact:true})).toHaveAttribute('aria-current','page');
 }
 await page.goto('/Desk');await page.screenshot({path:testInfo.outputPath('workspace-'+width+'.png'),fullPage:true});
});

test('audit return keeps its candidate run',async({page})=>{
 await signIn(page);
 const run='11111111-1111-1111-1111-111111111111';
 await page.goto('/CandidateAudit?candidateId=22222222-2222-2222-2222-222222222222&runId='+run+'&env=paper');
 const link=page.getByRole('link',{name:'Return to candidates'});
 await expect(link).toHaveAttribute('href','/Desk?runId='+run+'&env=paper');
});
