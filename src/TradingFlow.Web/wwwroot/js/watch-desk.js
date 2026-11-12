// Render live stock observations and delegate order entry to the shared reviewed popup.
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
            text('session',({Regular:'Regular market',RegularMarket:'Regular market',Premarket:'Premarket',PreMarket:'Premarket',Postmarket:'Postmarket',PostMarket:'Postmarket',AfterHours:'After hours',Overnight:'Overnight',ClosedWeekend:'Market closed',Closed:'Market closed'})[snapshot.session]||snapshot.session.replace(/([a-z])([A-Z])/g,'$1 $2'));
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
    // SIP (pre-market, regular, after hours) and Alpaca's overnight feed. Only an authoritative
    // closed session explains market closure; unknown sessions continue to show quote age.
    function quoteAge(row) {
        const stamp=row.quote?.timestamp;if(!stamp)return 'No quote';
        if(!connected)return 'Disconnected';
        const seconds=Math.floor((Date.now()-Date.parse(stamp))/1000);
        if(!Number.isFinite(seconds))return 'No quote';
        if(seconds < -5)return 'Clock mismatch';
        const age=seconds<60?Math.max(seconds,0)+'s':seconds<3600?Math.floor(seconds/60)+'m':Math.floor(seconds/3600)+'h';
        const session=String(snapshot?.session||'').toLowerCase();
        if(['closed','closedweekend','closed_weekend'].includes(session))return 'Market closed';
        const label=row.quote.feed==='overnight'?'Overnight':session.startsWith('pre')?'Pre-market':session.startsWith('post')||session==='afterhours'?'After hours':'Live';
        if(seconds<=120)return label+' · '+age;
        return 'Delayed · '+age+' ago';
    }
    // Explain unavailable actions immediately without creating a ticket or moving the stock workspace.
    function openOrder(side,trigger){
        const row=snapshot?.rows.find(item=>item.membership.symbol===selected);
        const reason=orderUnavailableReason(row,side);
        const label=(side==='buy'?'Buy ':'Sell ')+(selected||'');
        if(reason){window.tradingFlowOrderPopup.unavailable(reason,label,trigger);return;}
        const link=el(side==='buy'?'buy':'sell');
        if(!link||link.hidden||!link.getAttribute('href')){
            window.tradingFlowOrderPopup.unavailable('This order action is unavailable. Refresh the stock and account observations before retrying.',label,trigger);return;
        }
        openOrderUrl(link.getAttribute('href'),label,trigger);
    }
    // These explanations mirror the existing link guards; server preview remains the trading authority.
    function orderUnavailableReason(row,side){
        if(!row)return 'Select a stock before reviewing an order.';
        if(side==='buy'){
            if(!row.membership.wishlists.length)return 'Follow this stock in a stock group before buying. Screener membership does not authorize an order.';
            if(!isAdministrator&&!(isSignal(row)&&row.strategyAssessment?.candidateId&&row.strategyAssessment?.candidateRunId))
                return 'No executable strategy signal for this stock. Start a paper strategy run under Strategy runs to get entry signals.';
        }else{
            if(!connected)return 'Selling is unavailable while disconnected. Reconnect to refresh the account.';
            if(!row.holdingsKnown||!snapshot.account.available)return 'Selling is unavailable because holdings could not be read. Refresh the account and retry.';
            const accountTime=Date.parse(snapshot.account.readAtUtc);
            if(!Number.isFinite(accountTime)||Date.now()-accountTime>=30000)return 'Selling is unavailable because the account snapshot is stale. Refresh the account and retry.';
            if(!(row.holding?.quantity>0))return 'No held shares are available to sell for this stock.';
            if(!Number.isInteger(row.holding.quantity))return 'This holding has fractional shares. Whole-share order review is required on this Desk.';
        }
        const price=side==='buy'?row.quote?.askPrice:row.quote?.bidPrice;
        return price>0?'':'The '+(side==='buy'?'ask':'bid')+' quote is unavailable. Refresh market observations before reviewing an order.';
    }
    // Keep candidate IDs and exact strategy-run context in the existing workflow URL.
    function openOrderUrl(href,title,trigger){
        window.tradingFlowOrderPopup.open(href,title,trigger);
    }
    // Strategy-run cards use the same retained popup as the stock's Buy and Sell actions.
    document.getElementById('watch-runs')?.addEventListener('click',event=>{
        const link=event.target.closest('a[href^="/OrderTicket"]');if(!link)return;
        event.preventDefault();openOrderUrl(link.getAttribute('href'),link.textContent.trim()||'Strategy entry',link);
    });
    // View switch: all stocks, signals ready for entry, held positions, or stocks the strategy rejected.
    let view='all';
    const orderQuery=new URLSearchParams(location.search);
    let pendingOrder=['buy','sell'].includes(orderQuery.get('order'))&&orderQuery.get('search')?{symbol:orderQuery.get('search').toUpperCase(),side:orderQuery.get('order')}:null;
    for(const button of document.querySelectorAll('[data-view]'))button.onclick=()=>{view=button.dataset.view;for(const b of document.querySelectorAll('[data-view]'))b.setAttribute('aria-pressed',String(b===button));render();};
    function isSignal(row){const a=row.strategyAssessment;return !!a&&['recorded','evaluated'].includes(a.state)&&!a.expired&&a.checks.length>0&&a.checks.every(c=>c.passed);}
    function isRejected(row){const a=row.strategyAssessment;return !!a&&['recorded','evaluated'].includes(a.state)&&a.checks.some(c=>!c.passed);}
    const inView=row=>view==='signals'?isSignal(row):view==='held'?!!row.holding:view==='rejected'?isRejected(row):true;
    for(const side of ['buy','sell'])el(side).addEventListener('click',event=>{event.preventDefault();openOrder(side,event.currentTarget);});
    function tone(node, value) {node.classList.toggle('watch-gain',value>0);node.classList.toggle('watch-loss',value<0);}
    // Keep freshness visible in a narrow row; its complete wording and timestamp stay in the trade-cell title.
    function rowQuoteAge(row){
        return quoteAge(row).replace('Market closed · last ','Closed ').replace('Delayed · ','Delayed ')
            .replace('Pre-market · ','Pre ').replace('After hours · ','After ').replace('Overnight · ','ON ')
            .replace('Live · ','Live ').replace(' ago','');
    }
    // Use only the server's completed daily observation; never substitute quotes, another ticker or future bars.
    function dailyTechnical(row){
        const technical=row.technical,stamp=Date.parse(technical?.timestamp),readAt=Date.parse(snapshot?.readAtUtc);
        return technical&&['ready','insufficient'].includes(row.observation?.state)&&technical.timeframe==='1d'&&
            (!technical.ticker||technical.ticker.toUpperCase()===row.membership.symbol.toUpperCase())&&
            Number.isFinite(stamp)&&Number.isFinite(readAt)&&stamp<=readAt?technical:null;
    }
    // Keep the bar/feed source visible in detail and explain retained history when the stream disconnects.
    function dailyTechnicalDetail(row){
        const technical=dailyTechnical(row);
        return technical?(connected?'':'Disconnected · last observed ')+ 'Completed 1d bar · '+when(technical.timestamp)+' · '+(technical.dataFeed||'Feed unavailable'):
            'Daily indicators unavailable · '+(row.observation?.detail||'No verified completed daily snapshot.');
    }
    // Refresh compact daily lines; missing or invalid observations clear every earlier indicator value.
    function renderDailyCells(cells,row){
        const technical=dailyTechnical(row),detail=dailyTechnicalDetail(row),cell=cells[6];
        for(const field of ['sma10','sma20','sma50','sma200','rsi']){
            const node=cell.querySelector('[data-technical="'+field+'"]');
            node.textContent=field==='rsi'?number(technical?.[field]):money(technical?.[field]);node.title=detail;
        }
        cell.title=detail;
        const relativeVolume=technical?.relativeVolume;
        cells[7].querySelector('strong').textContent=typeof relativeVolume==='number'&&Number.isFinite(relativeVolume)&&relativeVolume>=0?
            new Intl.NumberFormat(undefined,{minimumFractionDigits:2,maximumFractionDigits:2}).format(relativeVolume)+'×':'—';
        cells[7].title=detail;
        renderSessionVolume(cells[8],row);
    }
    // Derive New York calendar dates from a verified UTC observation time, independently of the browser timezone.
    function exchangeDate(value){
        const stamp=Date.parse(value);if(!Number.isFinite(stamp))return null;
        const parts=new Intl.DateTimeFormat('en-US',{timeZone:'America/New_York',year:'numeric',month:'2-digit',day:'2-digit'}).formatToParts(stamp);
        const values=Object.fromEntries(parts.map(part=>[part.type,part.value]));
        return values.year+'-'+values.month+'-'+values.day;
    }
    // Accept only the server's matching daily volume receipt; completed technical-bar volume is never a substitute.
    function sessionVolume(row){
        const value=row.sessionVolume,readAt=Date.parse(snapshot?.readAtUtc),asOf=Date.parse(value?.asOfUtc),barAt=Date.parse(value?.barTimestamp);
        const date=value?.sessionDate,today=exchangeDate(snapshot?.readAtUtc);
        const validDate=typeof date==='string'&&/^\d{4}-\d{2}-\d{2}$/.test(date)&&Number.isFinite(Date.parse(date+'T00:00:00Z'))&&new Date(date+'T00:00:00Z').toISOString().slice(0,10)===date;
        return value&&String(value.ticker||'').toUpperCase()===row.membership.symbol.toUpperCase()&&
            typeof value.volume==='number'&&Number.isFinite(value.volume)&&value.volume>=0&&
            Number.isFinite(readAt)&&Number.isFinite(asOf)&&Number.isFinite(barAt)&&barAt<=asOf&&asOf<=readAt&&
            validDate&&today&&date<=today&&date===exchangeDate(value.barTimestamp)&&String(value.feed||'').toLowerCase()==='sip'?value:null;
    }
    // Keep observed SIP daily shares and their receipt/session provenance explicit, including old and disconnected observations.
    function renderSessionVolume(cell,row){
        const value=sessionVolume(row),amount=cell.querySelector('strong'),label=cell.querySelector('small');
        amount.textContent=value?new Intl.NumberFormat(undefined,{notation:'compact',maximumFractionDigits:2}).format(value.volume):'—';
        if(!value){label.textContent='Unavailable';cell.title='Session volume unavailable · no valid matching SIP daily-volume observation.';return;}
        const asOf=Date.parse(value.asOfUtc),stale=!connected||Date.now()-asOf>90000||asOf>Date.now()+5000;
        label.textContent=stale?'Last observed':value.sessionDate===exchangeDate(snapshot.readAtUtc)?'Today':'Last session';
        cell.title=new Intl.NumberFormat(undefined,{maximumFractionDigits:20}).format(value.volume)+' shares · '+value.sessionDate+' New York calendar day · '+
            (value.feed||'Feed unavailable')+' daily aggregate · includes regular and extended hours; excludes overnight-feed trades · bar '+when(value.barTimestamp)+' · observed '+when(value.asOfUtc);
    }
    // Show stored-news counts as context, with unscored stories and the exact partial window in the tooltip.
    function renderNewsSentiment(cell,row){
        const summary=row.newsSentiment,counts=cell.querySelector('.watch-news-counts'),positive=cell.querySelector('.watch-news-positive'),negative=cell.querySelector('.watch-news-negative'),windowLabel=cell.querySelector('.watch-news-window');
        const positiveText=summary?.available?summary.positive+' positive':'News unavailable';
        const negativeText=summary?.available?summary.negative+' negative':'';
        const asOf=Date.parse(summary?.asOfUtc),now=Date.now();
        const stale=!connected||!Number.isFinite(asOf)||asOf>now+5000||now-asOf>30000;
        const windowText=summary?.available?(stale?'Observed 1h · partial':'Last 1h · partial'):'1h window unavailable';
        const detail=summary?.available?'Stored news · '+summary.positive+' positive · '+summary.negative+' negative · '+summary.neutral+' neutral · '+summary.unscored+' unscored · '+
            when(summary.windowStartUtc)+' → '+when(summary.asOfUtc)+' · partial coverage · advisory only':
            'Stored news could not be read. Counts are unavailable; this is not a zero-story result.';
        if(positive.textContent!==positiveText)positive.textContent=positiveText;
        if(negative.textContent!==negativeText)negative.textContent=negativeText;
        const hideNegative=!summary?.available;
        if(negative.hidden!==hideNegative)negative.hidden=hideNegative;
        if(windowLabel.textContent!==windowText)windowLabel.textContent=windowText;
        for(const node of [cell,counts,windowLabel])if(node.title!==detail)node.title=detail;
    }
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
                tr.innerHTML='<td><button type="button" class="watch-symbol-button"><span class="watch-symbol-name"></span></button></td><td class="watch-num"><span class="watch-quote-line"><strong></strong></span></td><td class="watch-trade"><span class="watch-trade-line"><span class="watch-trade-buttons"><button type="button" class="watch-buy" data-side="buy"><span>Buy</span><span class="watch-order-price"></span></button><button type="button" class="watch-sell" data-side="sell"><span>Sell</span><span class="watch-order-price"></span></button></span><small></small></span></td><td class="watch-signal-col"><strong></strong><small></small></td><td class="watch-num"><span class="watch-holding-line"><strong></strong><small></small></span></td><td class="watch-news-summary"><span class="watch-news-counts"><span class="watch-news-positive"></span> <span class="watch-news-negative"></span></span><small class="watch-news-window"></small></td><td class="watch-technical"><div>SMA 10/20 <span data-technical="sma10" data-daily-sma="10"></span> / <span data-technical="sma20" data-daily-sma="20"></span></div><div>SMA 50/200 <span data-technical="sma50"></span> / <span data-technical="sma200"></span></div><div>RSI <span data-technical="rsi"></span></div></td><td class="watch-rvol"><strong></strong><small>Daily</small></td><td class="watch-volume"><strong></strong><small></small></td>';
                tr.querySelector('.watch-symbol-name').textContent=symbol;
                const symbolButton=tr.querySelector('.watch-symbol-button');
                symbolButton.setAttribute('aria-label',symbol);
                // Any click on the row selects it; the row's Buy and Sell buttons also open that side's order review.
                tr.onclick=event=>{if(event.target.closest('a'))return;selected=symbol;newsForAll=false;render();
                    const trigger=event.target.closest('[data-side]'),side=trigger?.dataset.side;if(side){openOrder(side,trigger);return;}
                    el('symbol').tabIndex=-1;el('symbol').focus({preventScroll:true});el('detail').scrollIntoView({behavior:'smooth',block:'nearest'});};rowNodes.set(symbol,tr);
            }
            const cells=tr.children;tr.dataset.selected=String(symbol===selected);tr.querySelector('.watch-symbol-button').setAttribute('aria-pressed',String(symbol===selected));
            renderNewsSentiment(cells[5],row);
            const lists=row.membership.wishlists.map(x=>x.name).join(', ');tr.querySelector('.watch-symbol-button').title=[row.membership.name,lists||'Screener discovery'].filter(Boolean).join(' · ');
            // Show change from the prior close here; executable ask/bid prices stay inside the Buy and Sell buttons.
            const bid=row.quote?.bidPrice,ask=row.quote?.askPrice;
            const change = row.previousClose?.close > 0 && row.quote?.midPrice != null ? (row.quote.midPrice / row.previousClose.close - 1) * 100 : null;
            const previous=cells[1].querySelector('strong');previous.textContent=change==null?'—':(change>0?'+':'')+number(change)+'%';tone(previous,change);
            cells[1].title=row.previousClose?'Previous close '+money(row.previousClose.close)+' · '+row.previousClose.sessionDate:'Previous close unavailable';
            // Buy fills at the ask and sell at the bid; the spread is the cost of crossing between them.
            const buy=cells[2].querySelector('.watch-buy'),sell=cells[2].querySelector('.watch-sell');
            buy.querySelector('.watch-order-price').textContent=money(ask);sell.querySelector('.watch-order-price').textContent=money(bid);buy.setAttribute('aria-label','Buy '+money(ask));sell.setAttribute('aria-label','Sell '+money(bid));buy.title='Buy at ask '+money(ask);sell.title='Sell at bid '+money(bid);buy.disabled=false;sell.disabled=false;buy.title=orderUnavailableReason(row,'buy')||buy.title;sell.title=orderUnavailableReason(row,'sell')||sell.title;
            cells[2].querySelector('small').textContent=rowQuoteAge(row);cells[2].title=spreadDetail(row)+' · '+when(row.quote?.timestamp);
            cells[3].querySelector('strong').textContent=signalLabel(row);cells[3].querySelector('small').textContent=signalReason(row);cells[3].title=signalReason(row);
            cells[4].querySelector('strong').textContent=!row.holdingsKnown?'Unknown':row.holding?number(row.holding.quantity)+' sh':'—';
            const pl=cells[4].querySelector('small');pl.textContent=row.holding?money(row.holding.unrealizedPl):'';tone(pl,row.holding?.unrealizedPl);
            renderDailyCells(cells,row);
        }
        // Move only rows whose position changed, preserving keyboard focus during price updates.
        rows.forEach((row,index)=>{const node=rowNodes.get(row.membership.symbol),at=el('rows').children[index];if(at!==node)el('rows').insertBefore(node,at||null);});
        el('empty').hidden=rows.length>0;
        if(!rows.length){el('empty').querySelector('strong').textContent=snapshot.selection.error?'Screen unavailable':filter?'No matching stocks':'No stocks in this view';el('empty').querySelector('p').textContent=snapshot.selection.error?'The failed screen is not being shown as a successful empty result.':filter?'Try a different symbol or clear the search.':'Add stocks to a stock group or select a screener to discover stocks.';}
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
        const help=!row.membership.wishlists.length?'Follow this stock in a stock group before buying. Screener membership does not authorize an order.':
            !isSignal(row)&&!isAdministrator?'No signal for this stock. Start a paper strategy run under Strategy runs to get entry signals.':'';
        text('order-help',help);el('order-help').hidden=!help;
        el('follow').hidden=row.membership.wishlists.length>0||!el('follow-list').options.length;
        const accountStale=!connected||Date.now()-Date.parse(snapshot.account.readAtUtc)>=30000;
        // Holding appears only for a held stock; account provenance moves into the tooltip.
        el('holding-block').hidden=!row.holding&&row.holdingsKnown;
        text('quantity',accountStale?'Stale snapshot':row.holdingsKnown?row.holding?number(row.holding.quantity):'Not held':'Unknown');
        text('entry',money(row.holding?.averageEntry));text('value',money(row.holding?.marketValue));text('pl',money(row.holding?.unrealizedPl));tone(el('pl'),row.holding?.unrealizedPl);
        el('holding-block').title=(accountStale?'Stale: last observed values. ':'')+snapshot.account.detail+' · cost basis '+money(row.holding?.costBasis)+' · '+when(snapshot.account.readAtUtc);
        const technical=dailyTechnical(row);text('sma10',money(technical?.sma10));text('sma20',money(technical?.sma20));text('sma50',money(technical?.sma50));text('sma200',money(technical?.sma200));text('tech-time',dailyTechnicalDetail(row));
        text('rsi',number(technical?.rsi));text('ema',money(technical?.ema20)+' / '+money(technical?.ema50));text('ema200',money(technical?.ema200));text('atr',number(technical?.atr));text('rvol',number(technical?.relativeVolume));
        // Strategy checks appear only once a strategy is chosen at the top.
        el('strategy-block').hidden=!selectedStrategyChoice();text('observation',observationLabel(row));renderStrategyChecks(row);
        el('rsi').parentElement.parentElement.title=dailyTechnicalDetail(row);
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
        const unavailable=!newsForAll&&snapshot.rows.find(row=>row.membership.symbol===selected)?.newsSentiment?.available===false||snapshot.newsCoverage.startsWith('News is unavailable');
        const signature=JSON.stringify([articles,unavailable]);if(signature===newsSignature)return;newsSignature=signature;el('news-list').replaceChildren();
        if(!articles.length){const p=document.createElement('p');p.className='watch-fine';p.textContent=unavailable?'Stored news could not be read. Retry when the connection recovers.':'No matched stories in the stored 72-hour window. This does not establish complete news coverage.';el('news-list').append(p);return;}
        for(const article of articles) el('news-list').append(window.TradingFlowNews.create(article,{allowGroup:false}));
    }
    // Age quote/account labels without treating a closed-market quote as invalid daily history.
    function ageRows(){if(!snapshot)return;for(const row of snapshot.rows){const node=rowNodes.get(row.membership.symbol)?.children[2];if(node){node.querySelector('small').textContent=rowQuoteAge(row);node.title=spreadDetail(row)+' · '+when(row.quote?.timestamp);const staleAccount=!connected||Date.now()-Date.parse(snapshot.account.readAtUtc)>=30000;const cells=node.parentElement.children;renderDailyCells(cells,row);renderNewsSentiment(cells[5],row);if(staleAccount&&row.holding){cells[4].querySelector('strong').textContent='Stale holding';cells[4].querySelector('small').textContent='Last observed P&L';}node.classList.toggle('watch-stale',!connected||Date.now()-Date.parse(row.quote?.timestamp)>120000);}}if(selected){const row=snapshot.rows.find(x=>x.membership.symbol===selected);if(row)text('quote-detail',quoteAge(row)+' · '+when(row.quote?.timestamp));}}
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
