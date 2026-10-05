import {expect,test} from '@playwright/test';
import {signIn} from './sign-in.js';

test('550 symbols stay paged, preserve new filters and keep unknown evidence out of ranges',async({page})=>{
  test.setTimeout(90000);
  await signIn(page);
  const post=(url,body)=>page.evaluate(async({url,body})=>{const response=await window.tradingFlowFetch(url,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});if(!response.ok)throw new Error(`Fixture ${response.status}`);return response.json();},{url,body});
  const group=await post('/api/v1/wishlists',{name:`Scale550 ${Date.now()}`});
  try {
    const symbols=Array.from({length:550},(_,i)=>`SCL${String(i).padStart(3,'0')}`);
    for(let offset=0;offset<symbols.length;offset+=250)await post('/api/v1/wishlists/bulk',{commandId:crypto.randomUUID(),operation:'add',targetId:group.id,tickers:symbols.slice(offset,offset+250)});
    await page.goto(`/TradeDesk?id=${group.id}&view=market&pageSize=100&sort=ticker&dir=asc`);
    await expect(page.locator('.trade-desk-table [data-symbol-row]')).toHaveCount(100);
    await expect(page.getByRole('navigation',{name:'Market list pages'})).toContainText('Page 1 of 6');
    await page.getByRole('navigation',{name:'Market list pages'}).getByRole('link',{name:'Next'}).click();
    await expect(page.locator('.trade-desk-table [data-symbol-row]').first()).toHaveAttribute('data-symbol','SCL100');
    await page.goto(`/TradeDesk?id=${group.id}&view=market&ticker=SCL100`);
    await page.waitForFunction(()=>Boolean(window.TradingFlowDesk));
    await page.locator('#Search').focus(); await page.keyboard.press('b');
    await expect(page).not.toHaveURL(/\/Orders/);
    await page.locator('#main-content').focus(); await page.keyboard.press('Control+b');
    await expect(page).not.toHaveURL(/\/Orders/);
    await page.keyboard.press('b');
    await expect(page).toHaveURL(/\/Orders.*ticker=SCL100/);
    expect(new URL(page.url()).searchParams.get('side')).toBe('buy');
    await page.goto(`/TradeDesk?id=${group.id}&view=market&ticker=SCL100`);
    await page.locator('#main-content').focus(); await page.keyboard.press('s');
    await expect(page).not.toHaveURL(/\/Orders/);
    await expect(page.locator('[data-order-shortcut=sell]')).toHaveCount(0);
    await page.getByRole('button',{name:'Add SCL100 to group',exact:true}).click();
    await page.locator('#groupActionClose').focus(); await page.keyboard.press('b');
    await expect(page).not.toHaveURL(/\/Orders/);
    await page.locator('#groupActionClose').click();
    await page.goto(`/TradeDesk?id=${group.id}&view=market`);
    for(const column of ['sector','cap','volume','rvol','atr','gap','vwap','rsi','catalyst','earnings'])await expect(page.locator(`[data-desk-column-toggle][value="${column}"]`)).toHaveCount(1);
    for(const width of [320,390,768,1440]){
      await page.setViewportSize({width,height:900});
      expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),`No page overflow at ${width}`).toBe(true);
    }
    await page.goto(`/TradeDesk?id=${group.id}&view=market&sector=Technology&minRvol=2&minVolume=100000&maxEarningsDays=3&minRsi=40&maxRsi=60`);
    await expect(page.locator('.trade-desk-table [data-symbol-row]')).toHaveCount(0);
    await page.locator('#DeskPresetName').fill('Evidence550'); await page.locator('#DeskPresetSave').click();
    await expect(page.locator('#DeskPresetStatus')).toContainText('Filters saved');
    await page.goto(`/TradeDesk?id=${group.id}&view=market`);
    await page.locator('#DeskPreset').selectOption({label:'Evidence550'});
    await expect(page).toHaveURL(/minRvol=2/);
    for(const [name,value]of [['Sector','Technology'],['MinRvol','2'],['MinVolume','100000'],['MaxEarningsDays','3'],['MinRsi','40'],['MaxRsi','60']])await expect(page.locator(`#${name}`)).toHaveValue(value);
    expect((await page.request.get(`/TradeDesk?id=${group.id}&maxRsi=101`)).status()).toBe(400);
  }finally{await page.evaluate(async id=>window.tradingFlowFetch(`/api/v1/wishlists/${id}`,{method:'DELETE'}),group.id);}
});
