(() => {
    'use strict';
    const el = id => document.getElementById('watch-' + id);
    if (!el('source')) return;
    let page = 0, searchTimer;
    const isAdministrator = document.querySelector(".watch-desk").dataset.administrator === "true";
    let stream, generation = 0, snapshot, selected, newsForAll = true, catalog = [], newsSignature = '', connected = false;
    const rowNodes = new Map();
    const money = value => value == null ? '—' : new Intl.NumberFormat(undefined, {style:'currency', currency:'USD', maximumFractionDigits:2}).format(value);
    const number = value => value == null ? '—' : new Intl.NumberFormat(undefined, {maximumFractionDigits:2}).format(value);
    const text = (id, value) => { el(id).textContent = value; };
    const when = value => value ? new Date(value).toLocaleString(undefined, {timeZone:'America/New_York'}) + ' NY · ' + new Date(value).toISOString() : 'Time unavailable';
    const safeUrl = value => { try { const u = new URL(value); return ['https:','http:'].includes(u.protocol) ? u.href : null; } catch { return null; } };
    async function api(path, body) {
        const response = await window.tradingFlowFetch('/api/desk/' + path, body === undefined ? {} : {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
        if (!response.ok) { let message; try { message=(await response.json()).message; } catch {} throw new Error(message || 'Unable to complete this request. Sign in again or retry.'); }
        return response.json();
    }
    async function sources(chosen) {
        catalog = await api('sources');
        el('source').replaceChildren();el('follow-list').replaceChildren();
        for (const group of [...new Set(catalog.map(x=>x.group))]) {
            const optgroup=document.createElement('optgroup');optgroup.label=group;
            for (const item of catalog.filter(x=>x.group===group)) {
                optgroup.append(new Option(item.name,item.key));
                if(item.key.startsWith('wishlist:')) el('follow-list').add(new Option(item.name,item.key.slice(9)));
            }
            el('source').append(optgroup);
        }
        el('source').value=catalog.some(x=>x.key===chosen)?chosen:'all';
    }
    function connect() {
        stream?.close();const version=++generation;connected=false;snapshot=null;selected=null;newsForAll=true;newsSignature='';rowNodes.clear();el('rows').replaceChildren();
        el('focus-content').hidden=true;text('symbol','Choose a stock');text('name','Select a row to inspect its news, technicals and holding.');
        el('retry').hidden=true;text('connection','Connecting · quotes refresh every 5 seconds');el('warning').hidden=true;el('empty').hidden=false;el('empty').querySelector('strong').textContent='Loading stocks…';
        text('count','— stocks');el('news-list').replaceChildren();
        const source=catalog.find(x=>x.key===el('source').value);text('title',source?.name||'All followed stocks');text('description',source?.description||'');
        const url=new URL(location.href);url.searchParams.set('source',el('source').value);history.replaceState(null,'',url);
        stream=new EventSource('/api/desk/stream?source='+encodeURIComponent(el('source').value)+'&page='+page+'&search='+encodeURIComponent(el('search').value));
        stream.addEventListener('snapshot',event=>{
            if(version!==generation)return;
            snapshot=JSON.parse(event.data);connected=true;updateHealth();
            text('session',({Regular:'Regular market',RegularMarket:'Regular market',Premarket:'Premarket',PreMarket:'Premarket',Postmarket:'Postmarket',PostMarket:'Postmarket',Closed:'Market closed'})[snapshot.session]||snapshot.session.replace(/([a-z])([A-Z])/g,'$1 $2'));
            const warnings=[];
            if(snapshot.selection.error)warnings.push(snapshot.selection.error);
            page=snapshot.selection.page||0;text('page',`Page ${page+1} of ${snapshot.selection.pageCount||1}`);el('prev').disabled=page===0;el('next').disabled=page+1>=(snapshot.selection.pageCount||1);
            if(!snapshot.account.available)warnings.push(snapshot.account.detail);
            text('warning',warnings.join(' '));el('warning').hidden=!warnings.length;
            const screened=snapshot.selection.screenedAtUtc?' · Finviz snapshot '+when(snapshot.selection.screenedAtUtc)+' · refreshes each minute':'';
            text('description',snapshot.selection.source.description+screened);
            if(selected&&!snapshot.rows.some(x=>x.membership.symbol===selected)) selected=null;
            render();
        });
        const unavailable=()=>{if(version!==generation)return;connected=false;el('retry').hidden=false;text('connection','Disconnected · reconnecting; displayed observations may be stale');if(!snapshot){el('empty').querySelector('strong').textContent='Market view unavailable';el('empty').querySelector('p').textContent='Check your connection or select another source. Reconnecting automatically.';}ageRows();renderFocus();};
        stream.onerror=unavailable;stream.addEventListener('unavailable',unavailable);
    }
    function quoteAge(row) {
        const stamp=row.quote?.timestamp;if(!stamp)return 'Unavailable';
        const seconds=Math.floor((Date.now()-Date.parse(stamp))/1000);
        if(seconds < -5)return 'Clock mismatch';
        const age=seconds<60?Math.max(seconds,0)+'s':seconds<3600?Math.floor(seconds/60)+'m':Math.floor(seconds/3600)+'h';
        return (!connected?'Disconnected · ':seconds>120?'Stale · ':seconds>20?'Delayed · ':'')+age;
    }
    function tone(node, value) {node.classList.toggle('watch-gain',value>0);node.classList.toggle('watch-loss',value<0);}
    function render() {
        if(!snapshot)return;
        const filter=el('search').value.toUpperCase().trim();
        let rows=snapshot.rows.filter(x=>(x.membership.symbol+' '+(x.membership.name||'')).toUpperCase().includes(filter));
        rows.sort((a,b)=>el('sort').value==='held'?(Number(!!b.holding)-Number(!!a.holding)||a.membership.symbol.localeCompare(b.membership.symbol)):el('sort').value==='news'?(Date.parse(b.news[0]?.publishedAtUtc||0)-Date.parse(a.news[0]?.publishedAtUtc||0)||a.membership.symbol.localeCompare(b.membership.symbol)):a.membership.symbol.localeCompare(b.membership.symbol));
        text('count',`${snapshot.selection.matchedCount??snapshot.selection.totalCount} stocks · ${rows.length} on this page`);
        const visible=new Set(rows.map(x=>x.membership.symbol));
        for(const [symbol,node]of rowNodes)if(!visible.has(symbol)){node.remove();rowNodes.delete(symbol);}
        for(const row of rows){
            const symbol=row.membership.symbol;let tr=rowNodes.get(symbol);
            if(!tr){tr=document.createElement('tr');tr.dataset.symbol=symbol;
                tr.innerHTML='<td><button type="button"></button><small></small></td><td><strong></strong><small></small></td><td><span></span></td><td><strong></strong><small></small></td><td><strong></strong><small></small></td><td><strong></strong><small></small></td><td><strong></strong><small></small></td>';
                tr.querySelector('button').textContent=symbol;tr.querySelector('button').onclick=()=>{selected=symbol;newsForAll=false;render();{el('symbol').tabIndex=-1;el('symbol').focus({preventScroll:true});el('detail').scrollIntoView({behavior:'smooth',block:'start'});}};rowNodes.set(symbol,tr);
            }
            const cells=tr.children;tr.dataset.selected=String(symbol===selected);tr.querySelector('button').setAttribute('aria-pressed',String(symbol===selected));
            const lists=row.membership.wishlists.map(x=>x.name).join(', ');cells[0].querySelector('small').textContent=lists||'Screener discovery';cells[0].querySelector('small').title=lists;
            cells[1].querySelector('strong').textContent=money(row.quote?.midPrice);cells[1].querySelector('small').textContent=money(row.quote?.bidPrice)+' · '+money(row.quote?.askPrice);
            cells[2].querySelector('span').textContent=quoteAge(row);cells[2].title=when(row.quote?.timestamp);
            cells[3].querySelector('strong').textContent=row.technical?'RSI '+number(row.technical.rsi):row.observation?.state==='loading'?'Loading daily history':'Daily history unavailable';
            cells[3].querySelector('small').textContent=row.technical?'EMA20 '+money(row.technical.ema20):row.observation?.detail||'Completed bars required';cells[3].title=when(row.technical?.timestamp);
            cells[4].querySelector('strong').textContent=!row.holdingsKnown?'Unknown':row.holding?number(row.holding.quantity)+' shares':'Not held';
            cells[4].querySelector('small').textContent=row.holding?money(row.holding.marketValue)+' value':'';
            const pl=cells[5].querySelector('strong');pl.textContent=money(row.holding?.unrealizedPl);tone(pl,row.holding?.unrealizedPl);
            cells[5].querySelector('small').textContent=row.news.length?row.news.length+' recent '+(row.news.length===1?'story':'stories'):'';
            cells[6].querySelector('strong').textContent=observationLabel(row);
            cells[6].querySelector('small').textContent=row.news[0]?.headline||'No matched news in 72h';
            cells[6].title=(row.observation?.evaluation?.reason||row.observation?.detail||'Daily observation unavailable')+' · '+(row.news[0]?.headline||'No matched news');
        }
        // Move only rows whose position changed, preserving keyboard focus during price updates.
        rows.forEach((row,index)=>{const node=rowNodes.get(row.membership.symbol),at=el('rows').children[index];if(at!==node)el('rows').insertBefore(node,at||null);});
        el('empty').hidden=rows.length>0;
        if(!rows.length){el('empty').querySelector('strong').textContent=snapshot.selection.error?'Screen unavailable':filter?'No matching stocks':'No stocks in this view';el('empty').querySelector('p').textContent=snapshot.selection.error?'The failed screen is not being shown as a successful empty result.':filter?'Try a different symbol or clear the search.':'Add stocks to a wishlist or select a screener to discover stocks.';}
        renderFocus();renderNews();ageRows();updateHealth();
    }
    function observationLabel(row){const o=row.observation;if(!o)return 'Observation unavailable';return o.state==='ready'?(o.evaluation?.shouldAlert?'Watch setup':'Not ready'):o.state==='loading'?'Loading daily history':o.state==='insufficient'?'More history needed':'Observation unavailable';}
    function updateHealth(){
        if(!snapshot||!connected)return;
        const current=snapshot.rows.filter(r=>r.quote?.timestamp&&Date.now()-Date.parse(r.quote.timestamp)<=20000&&Date.parse(r.quote.timestamp)<=Date.now()+5000).length;
        const available=snapshot.rows.filter(r=>r.quote?.timestamp).length;
        text('connection',snapshot.rows.length?`${current}/${snapshot.rows.length} current quotes · ${snapshot.rows.length-available} unavailable · refresh every 5s`:'Connected · no stocks in this view');
    }
    function reviewLink(id, enabled, row, side){
        const link=el(id);link.hidden=!enabled;
        if(!enabled){link.removeAttribute('href');return;}
        const query=new URLSearchParams({ticker:row.membership.symbol,side,env:'paper'});
        if(row.membership.wishlists.length)query.set('wishlistId',row.membership.wishlists[0].id);
        const price=side==='buy'?row.quote?.askPrice:row.quote?.bidPrice;if(price>0)query.set('limitPrice',price);
        if(side==='buy'&&!isAdministrator){link.textContent='Review candidates';link.href='/TradeDesk?wishlistId='+encodeURIComponent(row.membership.wishlists[0].id);}else {link.textContent=side==='buy'?'Review admin buy':'Review sell';link.href='/OrderTicket?'+query;}
    }
    function renderFocus(){
        const row=snapshot?.rows.find(x=>x.membership.symbol===selected);
        el('focus-content').hidden=!row;
        if(!row){text('symbol','Choose a stock');text('name','Select a row to inspect its news, technicals and holding.');return;}
        text('symbol',row.membership.symbol);text('name',row.membership.name||row.membership.wishlists.map(x=>x.name).join(' · ')||'Finviz discovery');
        text('mid',money(row.quote?.midPrice));text('bid',money(row.quote?.bidPrice));text('ask',money(row.quote?.askPrice));text('quote-detail',quoteAge(row)+' · '+when(row.quote?.timestamp));
        reviewLink('buy',row.membership.wishlists.length>0,row,'buy');reviewLink('sell',connected&&row.holdingsKnown&&Date.now()-Date.parse(snapshot.account.readAtUtc)<30000&&row.holding?.quantity>0,row,'sell');
        text('order-help',row.membership.wishlists.length?(isAdministrator?'Admin buy uses the explicit override review. Strategy entries remain available through candidate review.':'Review candidates to select an exact strategy-authorized entry. Wishlist membership alone cannot authorize a buy.'): 'Follow this stock in a wishlist before reviewing a buy. Screener membership does not authorize an order.');
        el('follow').hidden=row.membership.wishlists.length>0||!el('follow-list').options.length;
        const accountStale=!connected||Date.now()-Date.parse(snapshot.account.readAtUtc)>=30000;
        text('quantity',accountStale?'Stale snapshot':row.holdingsKnown?row.holding?number(row.holding.quantity):'Not held':'Unknown');
        text('entry',money(row.holding?.averageEntry));text('cost',money(row.holding?.costBasis));text('value',money(row.holding?.marketValue));text('pl',money(row.holding?.unrealizedPl));tone(el('pl'),row.holding?.unrealizedPl);
        text('account-detail',(accountStale?'STALE · last observed values; current holding unknown. ':'')+snapshot.account.detail+' · '+when(snapshot.account.readAtUtc));
        text('rsi',number(row.technical?.rsi));text('ema',money(row.technical?.ema20)+' / '+money(row.technical?.ema50));text('ema200',money(row.technical?.ema200));text('atr',number(row.technical?.atr));text('rvol',number(row.technical?.relativeVolume));
        text('observation',observationLabel(row));text('observation-detail',(row.observation?.evaluation?.reason||row.observation?.detail||'Daily observation unavailable')+' · '+when(row.observation?.evaluation?.sourceBarTimestampUtc));text('monitoring',row.membership.wishlists.some(x=>x.observed)?'Wishlist alerts enabled · server observes completed daily bars':'Live quotes and news on this desk · background wishlist alerts off');
        text('technical-detail',row.technical?'Completed '+row.technical.timeframe+' bar · '+when(row.technical.timestamp)+' · '+(row.technical.dataFeed||'Feed unavailable'):row.observation?.detail||'Daily history unavailable. Missing indicators are not neutral signals.');
    }
    function renderNews(){
        const articles=newsForAll?snapshot.news:snapshot.rows.find(x=>x.membership.symbol===selected)?.news||[];
        text('news-title',newsForAll?'News for this view':selected+' news');el('news-all').hidden=newsForAll;text('news-coverage',snapshot.newsCoverage);
        const signature=JSON.stringify(articles);if(signature===newsSignature)return;newsSignature=signature;el('news-list').replaceChildren();
        if(!articles.length){const p=document.createElement('p');p.className='watch-fine';p.textContent='No matched stories in the stored 72-hour window. This does not establish complete news coverage.';el('news-list').append(p);return;}
        for(const article of articles){const item=document.createElement('article');const url=safeUrl(article.url);const title=document.createElement(url?'a':'strong');title.textContent=article.headline;if(url){title.href=url;title.target='_blank';title.rel='noopener noreferrer';}const meta=document.createElement('small');meta.textContent=article.symbols.join(' · ')+' / '+article.provider+' / '+when(article.publishedAtUtc);item.append(title,meta);el('news-list').append(item);}
    }
    function ageRows(){if(!snapshot)return;for(const row of snapshot.rows){const node=rowNodes.get(row.membership.symbol)?.children[2];if(node){node.querySelector('span').textContent=quoteAge(row);const staleAccount=!connected||Date.now()-Date.parse(snapshot.account.readAtUtc)>=30000;const cells=node.parentElement.children;if(staleAccount){cells[4].querySelector('strong').textContent='Stale holding';cells[5].querySelector('small').textContent='Last observed P&L';}node.classList.toggle('watch-stale',!connected||row.quoteFreshness!=='current'||Date.now()-Date.parse(row.quote?.timestamp)>20000);}}if(selected){const row=snapshot.rows.find(x=>x.membership.symbol===selected);if(row)text('quote-detail',quoteAge(row)+' · '+when(row.quote?.timestamp));}}
    el('source').onchange=()=>{page=0;el('search').value='';connect();};el('search').oninput=()=>{clearTimeout(searchTimer);searchTimer=setTimeout(()=>{page=0;connect();},300);};
    el('prev').onclick=()=>{page=Math.max(0,page-1);connect();};el('next').onclick=()=>{page++;connect();};el('sort').onchange=render;
    el('news-all').onclick=()=>{newsForAll=true;renderNews();};
    el('import-open').onclick=()=>{el('import').hidden=false;el('import').elements.name.focus();};el('import-close').onclick=()=>{el('import').hidden=true;el('import-open').focus();};
    el('import').onsubmit=async event=>{event.preventDefault();const form=event.currentTarget,button=form.querySelector('[type=submit]');button.disabled=true;try{const saved=await api('screens',{name:form.elements.name.value,url:form.elements.url.value});await sources(saved.key);el('import').hidden=true;form.reset();connect();}catch(e){text('import-status',e.message);}finally{button.disabled=false;}};
    el('follow').onsubmit=async event=>{event.preventDefault();const button=event.currentTarget.querySelector('button');button.disabled=true;try{await api('follow',{source:el('source').value,symbol:selected,wishlistId:el('follow-list').value});text('follow-status','Added. Membership refreshes with the next update.');}catch(e){text('follow-status',e.message);}finally{button.disabled=false;}};
    function loadPreparation(){if(document.querySelector('[data-preparation-script]'))return;const script=document.createElement('script');script.src=el('preparation').dataset.scriptUrl;script.dataset.preparationScript='true';script.onerror=()=>{script.remove();document.getElementById('prep-status').textContent='Preparation controls could not load. Retry to reconnect.';const retry=document.getElementById('prep-retry');retry.hidden=false;retry.onclick=()=>{retry.hidden=true;loadPreparation();};};document.body.append(script);}
    el('preparation').addEventListener('toggle',()=>{if(el('preparation').open)loadPreparation();});
    window.addEventListener('pagehide',()=>{stream?.close();generation++;});
    window.addEventListener('pageshow',event=>{if(event.persisted)connect();});
    setInterval(()=>{ageRows();updateHealth();if(snapshot&&selected)renderFocus();},1000);
    async function start(){try{await sources(new URLSearchParams(location.search).get('source')||'all');connect();}catch(e){text('connection',e.message);el('empty').querySelector('strong').textContent='Sources unavailable';el('retry').hidden=false;}}
    el('retry').onclick=start;start();
    document.addEventListener('preparation-changed',event=>{const count=event.detail.count;text('prep-summary',count?count+' workflow'+(count===1?'':'s')+' · open review →':'Set up your workflow →');});
    window.tradingFlowFetch('/api/v1/preparation').then(r=>r.ok?r.json():null).then(rows=>{if(Array.isArray(rows))text('prep-summary',rows.length?rows.length+' workflow'+(rows.length===1?'':'s')+' · open review →':'Set up your workflow →');}).catch(()=>{});
})();
