(() => {
    'use strict';
    const el = id => document.getElementById('watch-' + id);
    if (!el('source')) return;
    let page = 0, searchTimer, strategyChoices = [], strategyCheckSignature = '', strategyLoadError = '';
    let currentJob = null, currentStocks = new Map(), currentSignature = '', currentSubmitting = false, currentFailure = '';
    const assessmentSubmissions = new Map();
    const isAdministrator = document.querySelector(".watch-desk").dataset.administrator === "true";
    let stream, generation = 0, lastStreamedQuoteAt = 0, snapshot, selected, newsForAll = true, catalog = [], newsSignature = '', connected = false;
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
            const optgroup=group ? document.createElement('optgroup') : document.createDocumentFragment();if(group)optgroup.label=group;
            for (const item of catalog.filter(x=>x.group===group)) {
                optgroup.append(new Option(item.name,item.key));
                if(item.key.startsWith('wishlist:')) el('follow-list').add(new Option(item.name,item.key.slice(9)));
            }
            el('source').append(optgroup);
        }
        el('source').value=catalog.some(x=>x.key===chosen)?chosen:'all';
    }
    // Load exact current strategies and separately labelled recorded runs.
    async function loadStrategies() {
        const response = await window.tradingFlowFetch('/api/v1/desk/strategy-choices');
        if (!response.ok) throw new Error('Strategy list unavailable. Retry to load recorded evaluations.');
        strategyChoices = await response.json();
        strategyLoadError = '';
        const query = new URLSearchParams(location.search);
        const picker = el('strategy');
        picker.replaceChildren(new Option('Select a strategy', ''));
        strategyChoices.forEach((choice, index) => {
            const context = choice.candidateRunId ? ' · recorded · ' + choice.mode + ' · ' + when(choice.runStartedAtUtc) : ' · current data';
            const option = new Option(choice.name + ' · ' + choice.strategy.semanticVersion + context, String(index));
            picker.add(option);
            if (query.get('strategyId') === choice.strategy.strategyId && query.get('semanticVersion') === choice.strategy.semanticVersion &&
                query.get('contentSha256') === choice.strategy.contentSha256 && (query.get('runId') || '') === (choice.candidateRunId || '')) picker.value = String(index);
        });
    }
    // Preserve the exact selected strategy/run in links and streamed requests.
    function strategyQuery(query) {
        const choice = el('strategy').value === '' ? null : strategyChoices[Number(el('strategy').value)];
        ['strategyId', 'semanticVersion', 'contentSha256', 'runId'].forEach(key => query.delete(key));
        if (choice) {
            Object.entries(choice.strategy).forEach(([key, value]) => query.set(key, value));
            if (choice.candidateRunId) query.set('runId', choice.candidateRunId);
        }
        text('strategy-status', strategyLoadError || (choice ? choice.name + ' · ' + choice.strategy.semanticVersion + ' · ' + choice.strategy.contentSha256 +
            (choice.candidateRunId ? ' · Run ' + choice.candidateRunId + ' · recorded evaluations, not current recommendations' : ' · Collecting current strategy evidence for this page.') :
            'Select a strategy to collect data and see its checks for each stock.'));
        return query;
    }
    // Reconnect observations with the selected exact strategy; old rows are cleared before a new response arrives.
    function connect() {
        if(currentJob && ['queued','running'].includes(currentJob.status)) cancelAssessment(currentJob.jobId).catch(()=>{});
        currentJob=null;currentStocks=new Map();currentSignature='';currentSubmitting=false;currentFailure='';el('strategy-refresh').disabled=false;
        stream?.close();const version=++generation;connected=false;snapshot=null;selected=null;newsForAll=true;newsSignature='';strategyCheckSignature='';rowNodes.clear();el('rows').replaceChildren();
        el('focus-content').hidden=true;text('symbol','Choose a stock');text('name','Select a row to inspect its news, technicals and holding.');
        el('retry').hidden=true;text('connection','Connecting · live quotes');el('warning').hidden=true;el('empty').hidden=false;el('empty').querySelector('strong').textContent='Loading stocks…';
        document.querySelector('.watch-table').classList.toggle('watch-no-strategy',!selectedStrategyChoice());
        text('count','— stocks');el('news-list').replaceChildren();
        const source=catalog.find(x=>x.key===el('source').value);text('title',source?.name||'All followed stocks');text('description',source?.description||'');
        const url=new URL(location.href);url.searchParams.set('source',el('source').value);strategyQuery(url.searchParams);history.replaceState(null,'',url);
        const streamQuery = strategyQuery(new URLSearchParams({source:el('source').value,page:String(page),search:el('search').value}));
        stream=new EventSource('/api/desk/stream?'+streamQuery);
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
            applyCurrentAssessments();render();observeCurrentStrategy(version);
            if(pendingOrder&&snapshot.rows.some(x=>x.membership.symbol===pendingOrder.symbol)){selected=pendingOrder.symbol;newsForAll=false;render();openOrder(pendingOrder.side);pendingOrder=null;}
        });
        const unavailable=()=>{if(version!==generation)return;connected=false;el('retry').hidden=false;text('connection','Disconnected · reconnecting; displayed observations may be stale');if(!snapshot){el('empty').querySelector('strong').textContent='Market view unavailable';el('empty').querySelector('p').textContent='Check your connection or select another source. Reconnecting automatically.';}ageRows();renderFocus();};
        stream.onerror=unavailable;stream.addEventListener('unavailable',unavailable);
        // Streamed SIP quotes arrive between snapshots; apply them to the rows already shown.
        stream.addEventListener('quotes',event=>{
            if(version!==generation||!snapshot)return;
            const bySymbol=new Map(JSON.parse(event.data).map(quote=>[quote.symbol,quote]));
            for(const row of snapshot.rows){const quote=bySymbol.get(row.membership.symbol);if(quote){row.quote={...row.quote,ticker:quote.symbol,bidPrice:quote.bidPrice,askPrice:quote.askPrice,midPrice:quote.midPrice,bidSize:quote.bidSize,askSize:quote.askSize,timestamp:quote.timestamp};row.quoteFreshness='current';}}
            lastStreamedQuoteAt=Date.now();render();
        });
    }
    // Keep API errors and durable progress explicit; all decision calculations stay on the server.
    async function assessmentApi(path, body) {
        const response=await window.tradingFlowFetch('/api/v1/desk/strategy-assessments'+path,body===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
        if(!response.ok){let message;try{message=(await response.json()).message;}catch{}throw new Error(message||'Strategy data collection failed. Retry or sign in again.');}
        return response.json();
    }
    // Cancels only this page's accepted observational work; no order cancellation is involved.
    function cancelAssessment(id){return assessmentApi('/'+encodeURIComponent(id)+'/cancel',{});}
    // An empty picker never selects the first catalog item implicitly.
    function selectedStrategyChoice(){return el('strategy').value===''?null:strategyChoices[Number(el('strategy').value)];}
    // Starts once per explicit strategy/source/page selection, then polls the existing durable job with quote updates.
    async function observeCurrentStrategy(version) {
        const choice=selectedStrategyChoice();
        const current=choice&&!choice.candidateRunId;
        el('strategy-refresh').hidden=!current;el('strategy-cancel').hidden=!current||!currentJob||!['queued','running'].includes(currentJob.status);
        if(!current||!snapshot?.rows.length||currentSubmitting)return;
        const symbols=snapshot.rows.map(row=>row.membership.symbol).sort();
        const signature=JSON.stringify({source:el('source').value,strategy:choice.strategy,symbols});
        currentSubmitting=true;
        try {
            currentSignature=signature;
            let submission=assessmentSubmissions.get(signature);
            if(!submission){
                submission={request:{contractVersion:'1.0',idempotencyKey:crypto.randomUUID(),strategy:choice.strategy,source:el('source').value,symbols},job:null};
                assessmentSubmissions.set(signature,submission);
            }
            // Keep the request before sending it: an uncertain response retries this same owner-scoped key.
            const job=!submission.job?await assessmentApi('',submission.request):
                ['queued','running'].includes(submission.job.status)?await assessmentApi('/'+encodeURIComponent(submission.job.jobId)):submission.job;
            if(JSON.stringify(job.strategy)!==JSON.stringify(choice.strategy))throw new Error('The strategy response does not match the selection.');
            submission.job=job;
            if(version!==generation){if(job&&['queued','running'].includes(job.status))cancelAssessment(job.jobId).catch(()=>{});return;}
            currentFailure='';currentJob=job;currentStocks=new Map((job.result?.stocks||[]).map(stock=>[stock.symbol,stock]));
            text('strategy-status',choice.name+' · '+choice.strategy.semanticVersion+' · '+job.status+' · '+job.detail+' · Profile '+job.profileSha256);
            el('strategy-refresh').disabled=['queued','running'].includes(job.status);
            el('strategy-cancel').hidden=!['queued','running'].includes(job.status);
            applyCurrentAssessments();render();
        }catch(error){if(version===generation){currentFailure=error.message;text('strategy-status',error.message);currentStocks=new Map();el('strategy-refresh').disabled=false;applyCurrentAssessments();render();}}
        finally{if(version===generation)currentSubmitting=false;}
    }
    // Associate results only with their exact stock and selected strategy; expiry is checked against the displayed clock.
    function applyCurrentAssessments(){
        const choice=selectedStrategyChoice();
        if(!choice||choice.candidateRunId||!snapshot)return;
        snapshot.rows=snapshot.rows.map(row=>{
            const result=currentStocks.get(row.membership.symbol);
            const assessment=result?{...result,expired:result.expired||!!result.expiresAtUtc&&Date.parse(result.expiresAtUtc)<=Date.now()}:
                {symbol:row.membership.symbol,strategy:choice.strategy,state:currentFailure?'failed':currentJob?.status||'pending',detail:currentFailure||currentJob?.detail||'Waiting for strategy data collection.',checks:[]};
            return {...row,strategyAssessment:assessment};
        });
    }
    // Where the quote came from and how old it is. The server already picks the newest quote across
    // SIP (pre-market, regular, after hours) and Alpaca's overnight feed, so an old quote is only
    // explained by a weekend or holiday; otherwise it is reported as delayed.
    function quoteAge(row) {
        const stamp=row.quote?.timestamp;if(!stamp)return 'No quote';
        if(!connected)return 'Disconnected';
        const seconds=Math.floor((Date.now()-Date.parse(stamp))/1000);
        if(seconds < -5)return 'Clock mismatch';
        const age=seconds<60?Math.max(seconds,0)+'s':seconds<3600?Math.floor(seconds/60)+'m':Math.floor(seconds/3600)+'h';
        const session=String(snapshot?.session||'').toLowerCase();
        const label=row.quote.feed==='overnight'?'Overnight':session.startsWith('pre')?'Pre-market':session.startsWith('post')?'After hours':'Live';
        if(seconds<=120)return label+' · '+age;
        return session.includes('weekend')||session.includes('holiday')?'Market closed · last '+age+' ago':'Delayed · '+age+' ago';
    }
    // Opens the selected stock's reviewed order workflow inside the detail panel instead of a new page.
    function openOrder(side){
        const link=el(side==='buy'?'buy':'sell');
        el('detail').scrollIntoView({behavior:'smooth',block:'nearest'});
        if(!link||link.hidden||!link.getAttribute('href'))return;
        openOrderUrl(link.getAttribute('href'),(side==='buy'?'Buy ':'Sell ')+(selected||''));
    }
    function openOrderUrl(href,title){
        const url=new URL(href,location.origin);
        if(url.pathname!=='/OrderTicket'){location.href=url;return;}
        url.searchParams.set('embedded','1');
        text('order-title',title);
        el('order-frame').src=url.pathname+url.search;el('order').hidden=false;el('order-frame').focus();
    }
    // Strategy-run cards link to the same reviewed order; keep it inside the Desk.
    document.getElementById('watch-runs')?.addEventListener('click',event=>{
        const link=event.target.closest('a[href^="/OrderTicket"]');if(!link)return;
        event.preventDefault();el('detail').scrollIntoView({behavior:'smooth',block:'nearest'});openOrderUrl(link.getAttribute('href'),link.textContent.trim()||'Strategy entry');
    });
    // View switch: all stocks, signals ready for entry, held positions, or stocks the strategy rejected.
    let view='all';
    const orderQuery=new URLSearchParams(location.search);
    let pendingOrder=['buy','sell'].includes(orderQuery.get('order'))&&orderQuery.get('search')?{symbol:orderQuery.get('search').toUpperCase(),side:orderQuery.get('order')}:null;
    for(const button of document.querySelectorAll('[data-view]'))button.onclick=()=>{view=button.dataset.view;for(const b of document.querySelectorAll('[data-view]'))b.setAttribute('aria-pressed',String(b===button));render();};
    function isSignal(row){const a=row.strategyAssessment;return !!a&&['recorded','evaluated'].includes(a.state)&&!a.expired&&a.checks.length>0&&a.checks.every(c=>c.passed);}
    function isRejected(row){const a=row.strategyAssessment;return !!a&&['recorded','evaluated'].includes(a.state)&&a.checks.some(c=>!c.passed);}
    const inView=row=>view==='signals'?isSignal(row):view==='held'?!!row.holding:view==='rejected'?isRejected(row):true;
    el('order-close').onclick=()=>{el('order').hidden=true;el('order-frame').removeAttribute('src');};
    for(const side of ['buy','sell'])el(side).addEventListener('click',event=>{event.preventDefault();openOrder(side);});
    // The embedded review reports its height so the panel grows with the form.
    window.addEventListener('message',event=>{if(event.origin===location.origin&&event.data?.type==='tradingflow:embedded-height')el('order-frame').style.height=Math.min(Math.max(event.data.height,200),1400)+'px';});
    function tone(node, value) {node.classList.toggle('watch-gain',value>0);node.classList.toggle('watch-loss',value<0);}
    // Render server-provided facts and recorded checks without introducing client-side strategy thresholds.
    function render() {
        if(!snapshot)return;
        const filter=el('search').value.toUpperCase().trim();
        for(const [name,test] of [['signals',isSignal],['held',r=>!!r.holding],['rejected',isRejected]]){const count=document.querySelector('[data-view-count="'+name+'"]');if(count)count.textContent=String(snapshot.rows.filter(test).length);}
        let rows=snapshot.rows.filter(x=>inView(x)&&(x.membership.symbol+' '+(x.membership.name||'')).toUpperCase().includes(filter));
        rows.sort((a,b)=>el('sort').value==='held'?(Number(!!b.holding)-Number(!!a.holding)||a.membership.symbol.localeCompare(b.membership.symbol)):el('sort').value==='news'?(Date.parse(b.news[0]?.publishedAtUtc||0)-Date.parse(a.news[0]?.publishedAtUtc||0)||a.membership.symbol.localeCompare(b.membership.symbol)):a.membership.symbol.localeCompare(b.membership.symbol));
        text('count',`${snapshot.selection.matchedCount??snapshot.selection.totalCount} stocks · ${rows.length} on this page`);
        const visible=new Set(rows.map(x=>x.membership.symbol));
        for(const [symbol,node]of rowNodes)if(!visible.has(symbol)){node.remove();rowNodes.delete(symbol);}
        for(const row of rows){
            const symbol=row.membership.symbol;let tr=rowNodes.get(symbol);
            if(!tr){tr=document.createElement('tr');tr.dataset.symbol=symbol;
                tr.innerHTML='<td><button type="button" class="watch-symbol-button"></button><small></small></td><td class="watch-num"><strong></strong><small></small></td><td class="watch-trade"><span class="watch-trade-buttons"><button type="button" class="watch-buy" data-side="buy"></button><button type="button" class="watch-sell" data-side="sell"></button></span><small></small></td><td class="watch-signal-col"><strong></strong><small></small></td><td class="watch-num"><strong></strong><small></small></td>';
                tr.querySelector('.watch-symbol-button').textContent=symbol;
                // Any click on the row selects it; the row's Buy and Sell buttons also open that side's order review.
                tr.onclick=event=>{if(event.target.closest('a'))return;selected=symbol;newsForAll=false;render();
                    const side=event.target.closest('[data-side]')?.dataset.side;if(side){openOrder(side);return;}
                    el('symbol').tabIndex=-1;el('symbol').focus({preventScroll:true});el('detail').scrollIntoView({behavior:'smooth',block:'nearest'});};rowNodes.set(symbol,tr);
            }
            const cells=tr.children;tr.dataset.selected=String(symbol===selected);tr.querySelector('.watch-symbol-button').setAttribute('aria-pressed',String(symbol===selected));
            const lists=row.membership.wishlists.map(x=>x.name).join(', ');cells[0].querySelector('small').textContent=(snapshot.selection.source.key.startsWith('earnings:')?row.membership.name:'')||lists||(row.membership.name==='Held position'?'Held position':'Screener discovery');cells[0].querySelector('small').title=lists;
            // Current price with yesterday's close directly under it, so the two can never be confused.
            const bid=row.quote?.bidPrice,ask=row.quote?.askPrice;
            const change = row.previousClose?.close > 0 && row.quote?.midPrice != null ? (row.quote.midPrice / row.previousClose.close - 1) * 100 : null;
            cells[1].querySelector('strong').textContent=money(row.quote?.midPrice);
            const previous=cells[1].querySelector('small');previous.textContent=row.previousClose?'Prev close '+money(row.previousClose.close)+(change==null?'':' · '+(change>0?'+':'')+number(change)+'%'):'Prev close unavailable';tone(previous,change);
            cells[1].title=row.previousClose?'Previous regular-session close, '+row.previousClose.sessionDate:'Previous close unavailable';
            // Buy fills at the ask and sell at the bid; the spread is the cost of crossing between them.
            const buy=cells[2].querySelector('.watch-buy'),sell=cells[2].querySelector('.watch-sell');
            buy.textContent='Buy '+money(ask);sell.textContent='Sell '+money(bid);buy.disabled=!(ask>0);sell.disabled=!(bid>0);
            cells[2].querySelector('small').textContent=spreadDetail(row);cells[2].title=when(row.quote?.timestamp);
            cells[3].querySelector('strong').textContent=signalLabel(row);cells[3].querySelector('small').textContent=signalReason(row);cells[3].title=row.strategyAssessment?.detail||'Select a strategy at the top';
            cells[4].querySelector('strong').textContent=!row.holdingsKnown?'Unknown':row.holding?number(row.holding.quantity)+' sh':'—';
            const pl=cells[4].querySelector('small');pl.textContent=row.holding?money(row.holding.unrealizedPl):'';tone(pl,row.holding?.unrealizedPl);

        }
        // Move only rows whose position changed, preserving keyboard focus during price updates.
        rows.forEach((row,index)=>{const node=rowNodes.get(row.membership.symbol),at=el('rows').children[index];if(at!==node)el('rows').insertBefore(node,at||null);});
        el('empty').hidden=rows.length>0;
        if(!rows.length){el('empty').querySelector('strong').textContent=snapshot.selection.error?'Screen unavailable':filter?'No matching stocks':'No stocks in this view';el('empty').querySelector('p').textContent=snapshot.selection.error?'The failed screen is not being shown as a successful empty result.':filter?'Try a different symbol or clear the search.':'Add stocks to a wishlist or select a screener to discover stocks.';}
        renderFocus();renderNews();ageRows();updateHealth();
    }
    // Summarize only checks actually recorded by the shared engine; missing and expired evidence stays explicit.
    function observationLabel(row){const assessment=row.strategyAssessment;if(!assessment)return 'Select a strategy';if(assessment.state==='missing')return 'Missing strategy data';if(!['recorded','evaluated'].includes(assessment.state))return assessment.state==='failed'?'Evaluation failed':assessment.state==='cancelled'?'Evaluation cancelled':'Collecting strategy data';return assessment.expired?'Evaluation expired':assessment.checks.some(check=>!check.passed)?'Strategy conditions failed':'Evaluated checks passed';}
    // Short outcome of the selected strategy for a row and the first failed check behind it.
    function signalLabel(row){const a=row.strategyAssessment;if(!a)return '—';if(!['recorded','evaluated'].includes(a.state))return a.state==='missing'?'No data':a.state==='failed'?'Failed':a.state==='cancelled'?'Cancelled':'Warming';return a.expired?'Expired':a.checks.some(c=>!c.passed)?'Rejected':'Passed';}
    function signalReason(row){const a=row.strategyAssessment;if(!a)return 'Choose a strategy at the top';const failed=a.checks?.find(c=>!c.passed);return failed?(failed.rejectionCode||failed.gateName.replace(/^strategy:/,'').replaceAll('_',' ')):a.detail||'';}
    function spreadDetail(row){const bid=row.quote?.bidPrice,ask=row.quote?.askPrice;return (ask>0&&bid>0?number((ask-bid)/((ask+bid)/2)*100)+'% · ':'')+quoteAge(row);}
    function updateHealth(){
        if(!snapshot||!connected)return;
        const current=snapshot.rows.filter(r=>r.quote?.timestamp&&Date.now()-Date.parse(r.quote.timestamp)<=20000&&Date.parse(r.quote.timestamp)<=Date.now()+5000).length;
        const available=snapshot.rows.filter(r=>r.quote?.timestamp).length;
        text('connection',snapshot.rows.length?`${current}/${snapshot.rows.length} current quotes · ${snapshot.rows.length-available} unavailable · ${Date.now()-lastStreamedQuoteAt<10000?'live SIP stream':'polled every 5s'}`:'Connected · no stocks in this view');
    }
    function reviewLink(id, enabled, row, side){
        const link=el(id);link.hidden=!enabled;
        if(!enabled){link.removeAttribute('href');return;}
        const query=new URLSearchParams({ticker:row.membership.symbol,side,env:'paper'});
        if(row.membership.wishlists.length)query.set('wishlistId',row.membership.wishlists[0].id);
        const price=side==='buy'?row.quote?.askPrice:row.quote?.bidPrice;if(price>0)query.set('limitPrice',price);
        const a=row.strategyAssessment;
        if(side==='buy'&&isSignal(row)&&a.candidateId&&a.candidateRunId){link.textContent='Buy signal '+money(price);link.href='/OrderTicket?'+new URLSearchParams({candidateId:a.candidateId,runId:a.candidateRunId,env:'paper'});return;}
        if(side==='buy'&&!isAdministrator){link.hidden=true;link.removeAttribute('href');return;}
        link.textContent=(side==='buy'?(isAdministrator?'Override buy ':'Buy '):'Sell ')+money(price);link.href='/OrderTicket?'+query;
    }
    // Keep quote/account rendering separate from the selected strategy's recorded evidence.
    function renderFocus(){
        const row=snapshot?.rows.find(x=>x.membership.symbol===selected);
        el('focus-content').hidden=!row;
        if(!row){text('symbol','Choose a stock');text('name','Select a row to see its price, holding and news.');text('mid','—');text('prev-close','');return;}
        text('symbol',row.membership.symbol);text('name',row.membership.name||row.membership.wishlists.map(x=>x.name).join(' · ')||'Finviz discovery');
        const change=row.previousClose?.close>0&&row.quote?.midPrice!=null?(row.quote.midPrice/row.previousClose.close-1)*100:null;
        text('mid',money(row.quote?.midPrice));text('prev-close',row.previousClose?'Prev close '+money(row.previousClose.close)+(change==null?'':' · '+(change>0?'+':'')+number(change)+'%'):'');tone(el('prev-close'),change);
        text('quote-detail',quoteAge(row)+' · spread '+(row.quote?.askPrice>0&&row.quote?.bidPrice>0?money(row.quote.askPrice-row.quote.bidPrice):'—'));el('quote-detail').title=when(row.quote?.timestamp);
        reviewLink('buy',row.membership.wishlists.length>0,row,'buy');reviewLink('sell',connected&&row.holdingsKnown&&Date.now()-Date.parse(snapshot.account.readAtUtc)<30000&&row.holding?.quantity>0&&Number.isInteger(row.holding.quantity),row,'sell');
        // Only explain when buying is unavailable; the buttons speak for themselves otherwise.
        const help=!row.membership.wishlists.length?'Follow this stock in a wishlist before buying. Screener membership does not authorize an order.':
            !isSignal(row)&&!isAdministrator?'No signal for this stock. Start a paper strategy run under Strategy runs to get entry signals.':'';
        text('order-help',help);el('order-help').hidden=!help;
        el('follow').hidden=row.membership.wishlists.length>0||!el('follow-list').options.length;
        const accountStale=!connected||Date.now()-Date.parse(snapshot.account.readAtUtc)>=30000;
        // Holding appears only for a held stock; account provenance moves into the tooltip.
        el('holding-block').hidden=!row.holding&&row.holdingsKnown;
        text('quantity',accountStale?'Stale snapshot':row.holdingsKnown?row.holding?number(row.holding.quantity):'Not held':'Unknown');
        text('entry',money(row.holding?.averageEntry));text('value',money(row.holding?.marketValue));text('pl',money(row.holding?.unrealizedPl));tone(el('pl'),row.holding?.unrealizedPl);
        el('holding-block').title=(accountStale?'Stale: last observed values. ':'')+snapshot.account.detail+' · cost basis '+money(row.holding?.costBasis)+' · '+when(snapshot.account.readAtUtc);
        text('rsi',number(row.technical?.rsi));text('ema',money(row.technical?.ema20)+' / '+money(row.technical?.ema50));text('ema200',money(row.technical?.ema200));text('atr',number(row.technical?.atr));text('rvol',number(row.technical?.relativeVolume));
        // Strategy checks appear only once a strategy is chosen at the top.
        el('strategy-block').hidden=!selectedStrategyChoice();text('observation',observationLabel(row));renderStrategyChecks(row);
        el('rsi').parentElement.parentElement.title=row.technical?'Completed '+row.technical.timeframe+' bar · '+when(row.technical.timestamp)+' · '+(row.technical.dataFeed||'Feed unavailable'):row.observation?.detail||'Daily history unavailable.';
    }
    // Display original rule names, outcomes and inputs; unvisited rules are never inferred to have passed.
    function renderStrategyChecks(row) {
        const assessment = row.strategyAssessment;
        const signature = JSON.stringify(assessment || null);
        if (signature === strategyCheckSignature) return;
        strategyCheckSignature = signature;
        el('strategy-checks').replaceChildren();
        if (!assessment) return;
        // The evaluation's identity and timing stay available on hover rather than as another paragraph.
        el('strategy-block').title = assessment.strategy.strategyId + ' · ' + assessment.strategy.semanticVersion + ' · ' + assessment.detail +
            (assessment.evaluatedAtUtc ? ' · Evaluated ' + when(assessment.evaluatedAtUtc) : '') +
            (assessment.latestCompletedCandleAtUtc ? ' · Completed candle ' + when(assessment.latestCompletedCandleAtUtc) : '');
        for (const check of assessment.checks) {
            const entry = document.createElement('details');
            const title = document.createElement('summary');
            title.textContent = check.gateName.replace(/^strategy:/, '').replaceAll('_', ' ') + ': ' + (check.passed ? 'passed' : 'failed');
            entry.append(title);
            if (check.rejectionCode) { const reason = document.createElement('p'); reason.textContent = check.rejectionCode; entry.append(reason); }
            const values = document.createElement('pre');
            values.textContent = JSON.stringify(check.inputs, null, 2);
            entry.append(values);el('strategy-checks').append(entry);
        }
    }
    function renderNews(){
        const articles=newsForAll?snapshot.news:snapshot.rows.find(x=>x.membership.symbol===selected)?.news||[];
        text('news-title',newsForAll?'News for this view':selected+' news');el('news-all').hidden=newsForAll;text('news-coverage',snapshot.newsCoverage);
        const signature=JSON.stringify(articles);if(signature===newsSignature)return;newsSignature=signature;el('news-list').replaceChildren();
        if(!articles.length){const p=document.createElement('p');p.className='watch-fine';p.textContent='No matched stories in the stored 72-hour window. This does not establish complete news coverage.';el('news-list').append(p);return;}
        for(const article of articles) el('news-list').append(window.TradingFlowNews.create(article,{allowGroup:false}));
    }
    function ageRows(){if(!snapshot)return;for(const row of snapshot.rows){const node=rowNodes.get(row.membership.symbol)?.children[2];if(node){node.querySelector('small').textContent=spreadDetail(row);const staleAccount=!connected||Date.now()-Date.parse(snapshot.account.readAtUtc)>=30000;const cells=node.parentElement.children;if(staleAccount&&row.holding){cells[4].querySelector('strong').textContent='Stale holding';cells[4].querySelector('small').textContent='Last observed P&L';}node.classList.toggle('watch-stale',!connected||Date.now()-Date.parse(row.quote?.timestamp)>120000);}}if(selected){const row=snapshot.rows.find(x=>x.membership.symbol===selected);if(row)text('quote-detail',quoteAge(row)+' · '+when(row.quote?.timestamp));}}
    el('source').onchange=()=>{page=0;el('search').value='';connect();};el('search').oninput=()=>{clearTimeout(searchTimer);searchTimer=setTimeout(()=>{page=0;connect();},300);};
    el('strategy').onchange=connect;
    // A refresh requests new evidence explicitly; completed observations do not silently become continuous background trading jobs.
    el('strategy-refresh').onclick=()=>{
        const submission=assessmentSubmissions.get(currentSignature);
        // An explicit refresh starts new collection only after the previous attempt is known to be terminal.
        if(submission?.job&&['completed','failed','cancelled'].includes(submission.job.status))assessmentSubmissions.delete(currentSignature);
        currentJob=null;currentStocks=new Map();currentSignature='';currentFailure='';applyCurrentAssessments();render();observeCurrentStrategy(generation);
    };
    el('strategy-cancel').onclick=async()=>{const version=generation,id=currentJob?.jobId;if(!id)return;try{const job=await cancelAssessment(id);if(version===generation){currentJob=job;text('strategy-status',job.detail);applyCurrentAssessments();render();}}catch(error){if(version===generation)text('strategy-status',error.message);}};
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
    // Load both catalogs before connecting; a strategy-list error must not hide otherwise available quotes.
    // A link to a recorded strategy run opens the Strategy runs section with that run loaded.
    if(new URLSearchParams(location.search).get('runId')&&document.getElementById('watch-runs'))document.getElementById('watch-runs').open=true;
    // A link can name the stock to show, e.g. /Desk?search=MSFT&order=buy from an order replacement.
    el('search').value=new URLSearchParams(location.search).get('search')||'';
    async function start(){try{await sources(new URLSearchParams(location.search).get('source')||'all');try{await loadStrategies();}catch(error){strategyLoadError=error.message;strategyChoices=[];el('strategy').replaceChildren(new Option('Select a strategy',''));}connect();}catch(e){text('connection',e.message);el('empty').querySelector('strong').textContent='Sources unavailable';el('retry').hidden=false;}}
    el('retry').onclick=start;start();
    document.addEventListener('preparation-changed',event=>{const count=event.detail.count;text('prep-summary',count?count+' workflow'+(count===1?'':'s')+' · open review →':'Set up your workflow →');});
    window.tradingFlowFetch('/api/v1/preparation').then(r=>r.ok?r.json():null).then(rows=>{if(Array.isArray(rows))text('prep-summary',rows.length?rows.length+' workflow'+(rows.length===1?'':'s')+' · open review →':'Set up your workflow →');}).catch(()=>{});
})();
