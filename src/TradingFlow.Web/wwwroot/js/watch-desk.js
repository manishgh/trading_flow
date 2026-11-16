// Render live stock observations and delegate order entry to the shared reviewed popup.
(() => {
    'use strict';
    const el = id => document.getElementById('watch-' + id);
    if (!el('source')) return;
    let page = 0, searchTimer, strategyChoices = [], strategyCheckSignature = '', strategyLoadError = '';
    let currentJob = null, currentStocks = new Map(), currentSignature = '', currentSubmitting = false, currentFailure = '';
    const assessmentSubmissions = new Map();
    const desk = document.querySelector(".watch-desk");
    const isAdministrator = desk.dataset.administrator === "true";
    const operator = desk.dataset.operator;
    let stream, generation = 0, lastStreamedQuoteAt = 0, snapshot, selected, newsForAll = true, catalog = [], newsSignature = '', connected = false;
    const rowNodes = new Map();
    const returnKey = 'tradingflow.desk.return.v1';
    const incoming = new URLSearchParams(location.search);
    const environment = (incoming.get('env') || 'paper').toLowerCase();
    const strategyKeys = ['strategyId','semanticVersion','contentSha256','runId'];
    const viewKeys = ['source','search','page','sort','view','symbol','order','preparationKey',...strategyKeys];
    const rememberedEntry = history.state?.deskObserved===true;
    if(rememberedEntry&&history.state.deskOperator!==operator){
        for(const key of viewKeys)incoming.delete(key);
        const clean=new URL(location.href);
        clean.search=incoming.toString();
        history.replaceState(null,'',clean);
    }
    const explicit = !rememberedEntry&&viewKeys.some(key=>incoming.has(key));
    const savedDesk = explicit ? null : readDeskReturn();
    const reviewKey = savedDesk?.preparationKey || incoming.get('preparationKey');
    let started = false, restoredView = false;
    let deskChart, chartSymbol;
    const moneyFormat = new Intl.NumberFormat(undefined, {style:'currency', currency:'USD', maximumFractionDigits:2});
    const numberFormat = new Intl.NumberFormat(undefined, {maximumFractionDigits:2});
    const volumeFormat = new Intl.NumberFormat(undefined, {notation:'compact', maximumFractionDigits:2});
    const exchangeDateFormat = new Intl.DateTimeFormat('en-US',{timeZone:'America/New_York',year:'numeric',month:'2-digit',day:'2-digit'});
    // Format available prices and measures without treating a missing value as zero.
    const money = value => value == null ? '—' : moneyFormat.format(value);
    const number = value => value == null ? '—' : numberFormat.format(value);
    const text = (id, value) => { el(id).textContent = value; };
    const when = value => value ? new Date(value).toLocaleString(undefined, {timeZone:'America/New_York'}) + ' NY · ' + new Date(value).toISOString() : 'Time unavailable';
    const safeUrl = value => { try { const u = new URL(value); return ['https:','http:'].includes(u.protocol) ? u.href : null; } catch { return null; } };

    // Bound the saved preferences to the same authenticated operator and environment in this tab.
    function readDeskReturn(){
        try{
            const json=sessionStorage.getItem(returnKey);
            if(!json||json.length>4096)return null;
            const value=JSON.parse(json);
            if(!operator||!value||value.operator!==operator||value.environment!==environment)return null;
            const state={};
            for(const [key,limit] of [['source',256],['search',80],['sort',32],['view',32],['selected',20],
                ['strategyId',128],['semanticVersion',64],['contentSha256',64],['runId',64],['preparationKey',512]]){
                if(typeof value[key]==='string'&&value[key].length<=limit)state[key]=value[key];
            }
            for(const key of ['page','scrollY','tableLeft','tableTop','newsTop']){
                const limit=key==='page'?10000:10000000;
                if(Number.isFinite(value[key])&&value[key]>=0&&value[key]<=limit)state[key]=Math.floor(value[key]);
            }
            state.newsForAll=value.newsForAll!==false;
            state.evidenceOpen=value.evidenceOpen===true;
            return state;
        }catch{return null;}
    }

    // Retain navigation and display selections without serializing observations or order workflow state.
    function captureDeskReturn(){
        const choice=selectedStrategyChoice(), table=document.querySelector('.watch-table-scroll');
        const url='/Desk'+(incoming.has('env')?'?env='+encodeURIComponent(environment):'');
        return {url,operator,environment,source:el('source').value,search:el('search').value,sort:el('sort').value,view,page,selected,newsForAll,
            strategyId:choice?.strategy.strategyId,semanticVersion:choice?.strategy.semanticVersion,
            contentSha256:choice?.strategy.contentSha256,runId:choice?.candidateRunId,
            preparationKey:el('source').value==='prepared'?reviewKey:undefined,evidenceOpen:el('evidence')?.open,
            scrollY:window.scrollY,tableLeft:table.scrollLeft,tableTop:table.scrollTop,newsTop:el('news').scrollTop};
    }

    // Write only view state when leaving this document, including links handled before pagehide.
    function saveDeskReturn(){
        if(!started||!operator)return;
        try{sessionStorage.setItem(returnKey,JSON.stringify(captureDeskReturn()));}catch{/* Storage may be unavailable. */}
    }

    // Apply a saved selected symbol only after the current page and view prove that it is present.
    function restoreDeskSelection(state){
        const row=snapshot.rows.find(item=>item.membership.symbol===state.selected);
        if(!row||!inView(row)||!matchesSearch(row,el('search').value.toUpperCase().trim()))return;
        selected=row.membership.symbol;
        newsForAll=state.newsForAll!==false;
        if(el('evidence'))el('evidence').open=state.evidenceOpen===true;
    }

    // Restore scroll once the first accepted snapshot has laid out the stock list and selected panel.
    function restoreDeskScroll(state, version){
        requestAnimationFrame(()=>{
            if(version!==generation)return;
            const table=document.querySelector('.watch-table-scroll');
            table.scrollLeft=state.tableLeft||0;
            table.scrollTop=state.tableTop||0;
            el('news').scrollTop=state.newsTop||0;
            window.scrollTo(0,state.scrollY||0);
        });
    }
    async function api(path, body) {
        const response = await window.tradingFlowFetch('/api/desk/' + path, body === undefined ? {} : {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
        if (!response.ok) { let message; try { message=(await response.json()).message; } catch {} throw new Error(message || 'Unable to complete this request. Sign in again or retry.'); }
        return response.json();
    }
    // Load normal sources; expose a separate prepared source only after the owner-scoped read succeeds.
    async function sources(chosen, strictPrepared = true) {
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
        const prepared=await readPreparedOption();
        if(prepared){
            catalog.push(prepared);
            el('source').add(new Option(prepared.name,prepared.key));
        }
        if(chosen==='prepared'&&!prepared&&strictPrepared){
            el('source').selectedIndex=-1;
            throw new Error('This preparation review could not be read. Retry or choose another source.');
        }
        el('source').value=catalog.some(x=>x.key===chosen)?chosen:'all';
    }
    // A review key from the address is only a lookup token; the server verifies its owner.
    async function readPreparedOption(){
        if(!reviewKey)return null;
        try{
            const response=await window.tradingFlowFetch('/api/v1/preparation/accepted?key='+encodeURIComponent(reviewKey),
                {signal:AbortSignal.timeout(5000)});
            if(!response.ok)return null;
            const review=await response.json();
            return {key:'prepared',name:'Reviewed preparation',description:'Saved revision '+review.parentRevisionId,expiresAtUtc:review.expiresAtUtc};
        }catch{return null;}
    }
    // Load exact current strategies and separately labelled recorded runs.
    async function loadStrategies(requested) {
        const response = await window.tradingFlowFetch('/api/v1/desk/strategy-choices');
        if (!response.ok) throw new Error('Strategy list unavailable. Retry to load recorded evaluations.');
        strategyChoices = await response.json();
        strategyLoadError = '';
        const picker = el('strategy');
        picker.replaceChildren(new Option('Select a strategy', ''));
        strategyChoices.forEach((choice, index) => {
            const context = choice.candidateRunId ? ' · recorded · ' + choice.mode + ' · ' + when(choice.runStartedAtUtc) : ' · current data';
            const option = new Option(choice.name + ' · ' + choice.strategy.semanticVersion + context, String(index));
            picker.add(option);
            if (requested.get('strategyId') === choice.strategy.strategyId && requested.get('semanticVersion') === choice.strategy.semanticVersion &&
                requested.get('contentSha256') === choice.strategy.contentSha256 && (requested.get('runId') || '') === (choice.candidateRunId || '')) picker.value = String(index);
        });
    }
    // Preserve the exact selected strategy/run in links and streamed requests.
    function strategyQuery(query) {
        const choice = el('strategy').value === '' ? null : strategyChoices[Number(el('strategy').value)];
        strategyKeys.forEach(key => query.delete(key));
        if (choice) {
            Object.entries(choice.strategy).forEach(([key, value]) => query.set(key, value));
            if (choice.candidateRunId) query.set('runId', choice.candidateRunId);
        }
        text('strategy-status', strategyLoadError || (choice ? choice.name + ' · ' + choice.strategy.semanticVersion + ' · ' + choice.strategy.contentSha256 +
            (choice.candidateRunId ? ' · Run ' + choice.candidateRunId + ' · recorded evaluations, not current recommendations' :
                restoredView ? ' · Refresh strategy checks to collect current evidence.' : ' · Collecting current strategy evidence for this page.') :
            'Select a strategy to collect data and see its checks for each stock.'));
        return query;
    }
    // Reconnect observations with the selected exact strategy; old rows are cleared before a new response arrives.
    function connect(restore = null, readOnlyReturn = !!restore) {
        restoredView=readOnlyReturn;
        if(!restoredView&&currentJob&&['queued','running'].includes(currentJob.status))cancelAssessment(currentJob.jobId).catch(()=>{});
        currentJob=null;currentStocks=new Map();currentSignature='';currentSubmitting=false;currentFailure='';el('strategy-refresh').disabled=false;
        stream?.close();const version=++generation;connected=false;snapshot=null;selected=null;newsForAll=true;newsSignature='';strategyCheckSignature='';rowNodes.clear();el('rows').replaceChildren();
        syncChart(null);
        el('focus-content').hidden=true;text('symbol','Choose a stock');text('name','Select a row to inspect its news, technicals and holding.');
        el('retry').hidden=true;text('connection','Connecting · live quotes');el('warning').hidden=true;el('empty').hidden=false;el('empty').querySelector('strong').textContent='Loading stocks…';
        text('count','— stocks');el('news-list').replaceChildren();
        const source=catalog.find(x=>x.key===el('source').value);text('title',source?.name||'All followed stocks');text('description',source?.description||'');
        const url=new URL(location.href);
        url.searchParams.set('source',el('source').value);
        strategyQuery(url.searchParams);
        history.replaceState({...history.state,deskObserved:!pendingOrder,deskOperator:operator},'',url);
        const streamQuery = strategyQuery(new URLSearchParams({source:el('source').value,page:String(page),search:el('search').value}));
        if(el('source').value==='prepared'&&reviewKey)streamQuery.set('preparationKey',reviewKey);
        const prepared=el('source').value==='prepared'?source:null;
        el('prepared-source').hidden=!prepared;
        if(prepared)text('prepared-source',prepared.description+' · '+(Date.parse(prepared.expiresAtUtc)<=Date.now()?'Evidence expired; saved list for inspection.':'Advisory list; no entry authorization.'));
        stream=new EventSource('/api/desk/stream?'+streamQuery);
        stream.addEventListener('snapshot',event=>{
            if(version!==generation)return;
            snapshot=JSON.parse(event.data);connected=true;updateHealth();
            text('session',({Regular:'Regular market',RegularMarket:'Regular market',Premarket:'Premarket',PreMarket:'Premarket',Postmarket:'Postmarket',PostMarket:'Postmarket',AfterHours:'After hours',Overnight:'Overnight',ClosedWeekend:'Market closed',Closed:'Market closed'})[snapshot.session]||snapshot.session.replace(/([a-z])([A-Z])/g,'$1 $2'));
            const warnings=[];
            if(snapshot.selection.error)warnings.push(snapshot.selection.error);
            page=snapshot.selection.page||0;
            const pageCount=snapshot.selection.pageCount||1;
            text('page',`Page ${page+1} of ${pageCount}`);
            el('pagination').hidden=pageCount<=1;
            el('prev').disabled=page===0;el('next').disabled=page+1>=pageCount;
            if(!snapshot.account.available)warnings.push(snapshot.account.detail);
            text('warning',warnings.join(' '));el('warning').hidden=!warnings.length;
            const screened=snapshot.selection.screenedAtUtc?' · Finviz snapshot '+when(snapshot.selection.screenedAtUtc)+' · refreshes each minute':'';
            text('description',snapshot.selection.source.description+screened);
            if(selected&&!snapshot.rows.some(x=>x.membership.symbol===selected)) selected=null;
            applyCurrentAssessments();
            const returned=restore;
            if(returned&&!snapshot.selection.error){restoreDeskSelection(returned);restore=null;}
            render();observeCurrentStrategy(version);
            if(returned&&!restore)restoreDeskScroll(returned,version);
            if(pendingOrder&&snapshot.rows.some(x=>x.membership.symbol===pendingOrder.symbol)){
                selected=pendingOrder.symbol;newsForAll=false;render();openOrder(pendingOrder.side);pendingOrder=null;
                const consumed=new URL(location.href);
                consumed.searchParams.delete('order');
                history.replaceState({...history.state,deskObserved:true,deskOperator:operator},'',consumed);
            }
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
        const prepared=el('source').value==='prepared';
        const current=choice&&!choice.candidateRunId&&!prepared;
        if(prepared&&choice&&!choice.candidateRunId)
            text('strategy-status','Use Research these stocks on Preparation to evaluate this frozen list, or select a recorded run.');
        el('strategy-refresh').hidden=!current;el('strategy-cancel').hidden=!current||!currentJob||!['queued','running'].includes(currentJob.status);
        if(!current||restoredView||!snapshot?.rows.length||currentSubmitting)return;
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
        if(!choice||choice.candidateRunId||!snapshot||restoredView)return;
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
    // A user can open review for either side. The ticket owns eligibility and submission checks.
    function openOrder(side,trigger){
        const row=snapshot?.rows.find(item=>item.membership.symbol===selected);
        const label=(side==='buy'?'Buy ':'Sell ')+(selected||'');
        if(row)openOrderUrl(orderReviewUrl(row,side),label,trigger);
    }
    // Desk observations explain possible restrictions without blocking the shared review ticket.
    function orderRestrictionHint(row,side){
        if(!row)return 'Select a stock before reviewing an order.';
        const restriction=runtimeRestriction(side);
        if(restriction)return restriction;
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
    // Entry restrictions cannot disable independently authorized exits.
    function runtimeRestriction(side){
        const current=window.tradingFlowWorkspaceStatus;
        if(!current)return 'Trading status is being read. Order submission is unavailable.';
        return (side==='buy'?current.openingRestriction:current.exitRestriction)||'';
    }
    // Keep candidate IDs and exact strategy-run context in the existing workflow URL.
    function openOrderUrl(href,title,trigger){
        window.tradingFlowOrderPopup.open(href,title,trigger);
    }
    // Strategy-run cards use the same retained popup as the stock's Buy and Sell actions.
    document.getElementById('watch-runs')?.addEventListener('click',event=>{
        const link=event.target.closest('a[href^="/OrderTicket"]');if(!link)return;
        event.preventDefault();
        const reason=runtimeRestriction('buy');
        if(reason){window.tradingFlowOrderPopup.unavailable(reason,'Strategy entry',link);return;}
        openOrderUrl(link.getAttribute('href'),link.textContent.trim()||'Strategy entry',link);
    });
    // View switch: all stocks, signals ready for entry, held positions, or stocks the strategy rejected.
    let view='all';
    let pendingOrder=explicit&&['buy','sell'].includes(incoming.get('order'))&&incoming.get('search')
        ?{symbol:incoming.get('search').toUpperCase(),side:incoming.get('order')}:null;
    // Match the restored view to an existing tab and keep its keyboard state synchronized.
    function setView(name){
        const buttons=[...document.querySelectorAll('[data-view]')];
        view=buttons.some(button=>button.dataset.view===name)?name:'all';
        for(const button of buttons)button.setAttribute('aria-pressed',String(button.dataset.view===view));
    }
    for(const button of document.querySelectorAll('[data-view]'))button.onclick=()=>{setView(button.dataset.view);render();};
    function isSignal(row){const a=row.strategyAssessment;return !!a&&['recorded','evaluated'].includes(a.state)&&!a.expired&&a.checks.length>0&&a.checks.every(c=>c.passed);}
    function isRejected(row){const a=row.strategyAssessment;return !!a&&['recorded','evaluated'].includes(a.state)&&a.checks.some(c=>!c.passed);}
    const inView=row=>view==='signals'?isSignal(row):view==='held'?!!row.holding:view==='rejected'?isRejected(row):true;
    // Apply the same visible stock-name filter when rendering and restoring a selection.
    const matchesSearch=(row,filter)=>(row.membership.symbol+' '+(row.membership.name||'')).toUpperCase().includes(filter);
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
    // Derive New York calendar dates from a verified UTC observation time, independently of the browser timezone.
    function exchangeDate(value){
        const stamp=Date.parse(value);if(!Number.isFinite(stamp))return null;
        const parts=exchangeDateFormat.formatToParts(stamp);
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
    // Retain SIP volume and receipt time in the selected stock's daily detail.
    function showSessionVolume(row){
        const value=sessionVolume(row);
        text('sip-volume', value
            ? new Intl.NumberFormat().format(value.volume)+' SIP shares · '+value.sessionDate+' NY · observed '+when(value.asOfUtc)
            : 'SIP session volume unavailable.');
        el('sip-volume').title='Daily aggregate includes regular and extended hours; excludes overnight-feed trades.';
    }
    // Display completed 1d values independently of quote freshness, and keep SIP daily volume separate.
    function renderDailyCells(node,row){
        const technical=dailyTechnical(row), detail=dailyTechnicalDetail(row);
        const cell=node.querySelector('.watch-technical'), rvol=node.querySelector('.watch-rvol');
        for(const value of cell.querySelectorAll('[data-technical]')){
            const field=value.dataset.technical, observed=technical?.[field];
            value.textContent=Number.isFinite(observed)?(field==='rsi'?number(observed):money(observed)):'—';
        }
        cell.title=detail;
        const relative=technical?.relativeVolume;
        rvol.querySelector('strong').textContent=Number.isFinite(relative)&&relative>=0?number(relative)+'×':'—';
        rvol.title=detail;
        const volume=node.querySelector('.watch-volume'), observation=sessionVolume(row);
        volume.querySelector('strong').textContent=observation?volumeFormat.format(observation.volume):'—';
        volume.querySelector('small').textContent=observation?observation.sessionDate:'Unavailable';
        volume.title=observation?number(observation.volume)+' SIP shares · '+observation.sessionDate+
            ' NY · observed '+when(observation.asOfUtc)+' · includes regular and extended hours and excludes overnight feed':
            'No verified SIP session volume.';
    }

    // Show the time window of stored counts and distinguish a failed read from a successful zero.
    function renderNewsSentiment(cell,row){
        const summary=row.newsSentiment, positive=cell.querySelector('.watch-news-positive');
        const negative=cell.querySelector('.watch-news-negative'), windowLabel=cell.querySelector('.watch-news-window');
        const asOf=Date.parse(summary?.asOfUtc), start=Date.parse(summary?.windowStartUtc), now=Date.now();
        const stale=!connected||!Number.isFinite(asOf)||asOf>now+5000||now-asOf>30000;
        const available=summary?.available===true;
        positive.textContent=available?summary.positive+' positive':'Unavailable';
        negative.textContent=available?summary.negative+' negative':'';
        negative.hidden=!available;
        positive.classList.toggle('watch-gain',available&&summary.positive>0);
        negative.classList.toggle('watch-loss',available&&summary.negative>0);
        windowLabel.textContent=available?(stale?'Observed 1h · partial':'Last 1h · partial'):'News read unavailable';
        cell.title=available?'Stored news · '+summary.neutral+' neutral · '+summary.unscored+' unscored · '+
            when(Number.isFinite(start)?summary.windowStartUtc:null)+' → '+when(Number.isFinite(asOf)?summary.asOfUtc:null):
            'Stored news counts could not be read.';
    }
    // Render server-provided facts and recorded checks without introducing client-side strategy thresholds.
    function render() {
        if(!snapshot)return;
        const filter=el('search').value.toUpperCase().trim();
        for(const [name,test] of [['signals',isSignal],['held',r=>!!r.holding],['rejected',isRejected]]){const count=document.querySelector('[data-view-count="'+name+'"]');if(count)count.textContent=String(snapshot.rows.filter(test).length);}
        let rows=snapshot.rows.filter(row=>inView(row)&&matchesSearch(row,filter));
        rows.sort((a,b)=>el('sort').value==='held'?(Number(!!b.holding)-Number(!!a.holding)||a.membership.symbol.localeCompare(b.membership.symbol)):el('sort').value==='news'?(Date.parse(b.news[0]?.publishedAtUtc||0)-Date.parse(a.news[0]?.publishedAtUtc||0)||a.membership.symbol.localeCompare(b.membership.symbol)):a.membership.symbol.localeCompare(b.membership.symbol));
        text('count',`${snapshot.selection.matchedCount??snapshot.selection.totalCount} stocks · ${rows.length} on this page`);
        const visible=new Set(rows.map(x=>x.membership.symbol));
        for(const [symbol,node]of rowNodes)if(!visible.has(symbol)){node.remove();rowNodes.delete(symbol);}
        for(const row of rows){
            const symbol=row.membership.symbol;
            if(!rowNodes.has(symbol))rowNodes.set(symbol,createStockRow(symbol));
            updateStockRow(rowNodes.get(symbol),row);
        }
        // Move only rows whose position changed, preserving keyboard focus during price updates.
        rows.forEach((row,index)=>{const node=rowNodes.get(row.membership.symbol),at=el('rows').children[index];if(at!==node)el('rows').insertBefore(node,at||null);});
        el('empty').hidden=rows.length>0;
        if(!rows.length){el('empty').querySelector('strong').textContent=snapshot.selection.error?'Screen unavailable':filter?'No matching stocks':'No stocks in this view';el('empty').querySelector('p').textContent=snapshot.selection.error?'The failed screen is not being shown as a successful empty result.':filter?'Try a different symbol or clear the search.':'Add stocks to a stock group or select a screener to discover stocks.';}
        renderFocus();renderNews();ageRows();updateHealth();
    }
    // Stable native buttons preserve keyboard focus while quotes update cell text.
    function createStockRow(symbol){
        const row=document.createElement('tr');
        row.dataset.symbol=symbol;
        row.innerHTML=`<td><button type="button" class="watch-symbol-button"></button></td>
            <td class="watch-row-price"><strong></strong><small></small></td>
            <td><div class="watch-trade-buttons">
                <button type="button" data-order-side="buy">Buy<span class="watch-order-price"></span></button>
                <button type="button" data-order-side="sell">Sell<span class="watch-order-price"></span></button>
            </div></td>
            <td class="watch-row-strategy"><strong></strong><small></small></td>
            <td class="watch-row-position"><strong></strong><small></small></td>
            <td class="watch-news-summary"><span class="watch-news-counts"><span class="watch-news-positive"></span><span class="watch-news-negative"></span></span><small class="watch-news-window"></small></td>
            <td class="watch-technical">
                <div>SMA 10/20 <span data-technical="sma10"></span> / <span data-technical="sma20"></span></div>
                <div>SMA 50/200 <span data-technical="sma50"></span> / <span data-technical="sma200"></span></div>
                <div>RSI <span data-technical="rsi"></span></div>
            </td>
            <td class="watch-rvol"><strong></strong><small>Daily</small></td>
            <td class="watch-volume"><strong></strong><small></small></td>
            <td><button type="button" class="watch-row-action"></button></td>`;
        const stock=row.querySelector('.watch-symbol-button'), action=row.querySelector('.watch-row-action');
        stock.textContent=symbol;
        action.addEventListener('click',event=>{
            event.stopPropagation();
            selectStock(symbol,!action.dataset.side);
            if(action.dataset.side)openOrder(action.dataset.side,action);
        });
        for(const button of row.querySelectorAll('[data-order-side]')){
            button.addEventListener('click',event=>{
                event.stopPropagation();
                selectStock(symbol,false);
                openOrder(button.dataset.orderSide,button);
            });
        }
        row.addEventListener('click',()=>selectStock(symbol,true));
        return row;
    }
    // Keep the whole workspace in view on desktop and focus the selected stock after explicit navigation.
    function selectStock(symbol,focus){
        selected=symbol;newsForAll=false;render();
        if(!focus)return;
        const heading=el('symbol');
        heading.tabIndex=-1;
        heading.focus({preventScroll:true});
        const target=matchMedia('(min-width:1151px)').matches?desk.querySelector('.watch-grid'):el('detail');
        target.scrollIntoView({block:'nearest'});
    }
    // Update the stable row with current evidence and its reviewed order actions.
    function updateStockRow(node,row){
        const symbol=row.membership.symbol, strategy=node.querySelector('.watch-row-strategy');
        const stock=node.querySelector('.watch-symbol-button');
        const lists=row.membership.wishlists.map(x=>x.name).join(', ');
        stock.title=[row.membership.name,lists||'Screener discovery'].filter(Boolean).join(' · ');
        node.dataset.selected=String(symbol===selected);
        stock.setAttribute('aria-pressed',String(symbol===selected));
        node.querySelector('.watch-row-price strong').textContent=money(row.quote?.midPrice);
        strategy.querySelector('strong').textContent=signalLabel(row);
        strategy.querySelector('small').textContent=row.strategyAssessment?signalReason(row):'';
        strategy.title=signalReason(row);
        renderDailyCells(node,row);
    }
    // Keep both review actions available and refresh the shorthand recommendation from current observations.
    function updateRowAction(node,row){
        const action=node.querySelector('.watch-row-action'), symbol=row.membership.symbol;
        const reasons={buy:orderRestrictionHint(row,'buy'),sell:orderRestrictionHint(row,'sell')};
        for(const button of node.querySelectorAll('[data-order-side]')){
            const buy=button.dataset.orderSide==='buy', side=button.dataset.orderSide;
            const price=buy?row.quote?.askPrice:row.quote?.bidPrice;
            button.title=reasons[side]?'Review order · '+reasons[side]:(buy?'Review buy at the ask':'Review sell at the bid');
            button.setAttribute('aria-label',(buy?'Buy ':'Sell ')+symbol);
            button.querySelector('.watch-order-price').textContent=price>0?money(price):'—';
        }
        const side=row.holding&&!reasons.sell?'sell':isSignal(row)&&!reasons.buy?'buy':'';
        action.dataset.side=side;
        action.textContent=side==='sell'?'Review exit':side==='buy'?'Review entry':'Inspect';
        action.setAttribute('aria-label',action.textContent+' '+symbol);
        action.title=side?'Open the server-reviewed ticket':(row.holding?reasons.sell:reasons.buy)||'Open stock detail';
    }
    // Summarize only checks actually recorded by the shared engine; missing and expired evidence stays explicit.
    function observationLabel(row){const assessment=row.strategyAssessment;if(!assessment)return 'Select a strategy';if(assessment.state==='missing')return 'Missing strategy data';if(!['recorded','evaluated'].includes(assessment.state))return assessment.state==='failed'?'Evaluation failed':assessment.state==='cancelled'?'Evaluation cancelled':'Collecting strategy data';return assessment.expired?'Evaluation expired':assessment.checks.some(check=>!check.passed)?'Strategy conditions failed':'Evaluated checks passed';}
    // Short outcome of the selected strategy for a row and the first failed check behind it.
    function signalLabel(row){const a=row.strategyAssessment;if(!a)return 'Not selected';if(!['recorded','evaluated'].includes(a.state))return a.state==='missing'?'No data':a.state==='failed'?'Failed':a.state==='cancelled'?'Cancelled':'Warming';return a.expired?'Expired':a.checks.some(c=>!c.passed)?'Rejected':'Passed';}
    function signalReason(row){const a=row.strategyAssessment;if(!a)return 'Choose a strategy at the top';const failed=a.checks?.find(c=>!c.passed);return failed?(failed.rejectionCode||failed.gateName.replace(/^strategy:/,'').replaceAll('_',' ')):a.detail||'';}
    function spreadDetail(row){const bid=row.quote?.bidPrice,ask=row.quote?.askPrice;return (ask>0&&bid>0?number((ask-bid)/((ask+bid)/2)*100)+'% · ':'')+quoteAge(row);}
    function updateHealth(){
        if(!snapshot||!connected)return;
        const current=snapshot.rows.filter(r=>r.quote?.timestamp&&Date.now()-Date.parse(r.quote.timestamp)<=20000&&Date.parse(r.quote.timestamp)<=Date.now()+5000).length;
        const available=snapshot.rows.filter(r=>r.quote?.timestamp).length;
        text('connection',snapshot.rows.length?`${current}/${snapshot.rows.length} current quotes · ${snapshot.rows.length-available} unavailable · ${Date.now()-lastStreamedQuoteAt<10000?'live SIP stream':'polled every 5s'}`:'Connected · no stocks in this view');
    }
    // Use an exact paper candidate when one is present and pass only observed symbol, group and price otherwise.
    function orderReviewUrl(row,side){
        const a=row.strategyAssessment;
        if(side==='buy'&&isSignal(row)&&a.candidateId&&a.candidateRunId)
            return '/OrderTicket?'+new URLSearchParams({candidateId:a.candidateId,runId:a.candidateRunId,env:'paper'});
        const query=new URLSearchParams({ticker:row.membership.symbol,side,env:'paper'});
        if(row.membership.wishlists.length)query.set('wishlistId',row.membership.wishlists[0].id);
        const price=side==='buy'?row.quote?.askPrice:row.quote?.bidPrice;if(price>0)query.set('limitPrice',price);
        return '/OrderTicket?'+query;
    }
    // Label the same review URL in the selected stock panel and attach any advisory hint.
    function reviewLink(side,reason,row,help){
        const link=el(side);
        const a=row.strategyAssessment;
        const price=side==='buy'?row.quote?.askPrice:row.quote?.bidPrice;
        const buyLabel=isSignal(row)&&a.candidateId&&a.candidateRunId?'Buy signal':isAdministrator?'Override buy':'Buy';
        link.textContent=(side==='buy'?buyLabel:'Sell')+(price>0?' '+money(price):'');
        link.href=orderReviewUrl(row,side);
        if(reason)link.setAttribute('aria-describedby',help);else link.removeAttribute('aria-describedby');
    }
    // Start history only for a selected stock and ignore quote ticks that keep the same selection.
    function syncChart(symbol) {
        if (chartSymbol === symbol) return;
        chartSymbol = symbol;
        if (symbol && !deskChart) {
            const root = el('focus-content').querySelector('[data-stock-chart]');
            if (root) deskChart = window.TradingFlowStockChart?.mount(root, { summaryHost: el('chart-summary') });
        }
        deskChart?.setSymbol(symbol);
    }
    // Keep quote/account rendering separate from the selected strategy's recorded evidence.
    function renderFocus(){
        const row=snapshot?.rows.find(x=>x.membership.symbol===selected);
        el('focus-content').hidden=!row;
        syncChart(row?.membership.symbol ?? null);
        if(!row){text('symbol','Choose a stock');text('name','Select a row to see its price, holding and news.');text('mid','—');text('prev-close','');return;}
        text('symbol',row.membership.symbol);text('name',row.membership.name||row.membership.wishlists.map(x=>x.name).join(' · ')||'Finviz discovery');
        const change=row.previousClose?.close>0&&row.quote?.midPrice!=null?(row.quote.midPrice/row.previousClose.close-1)*100:null;
        text('mid',money(row.quote?.midPrice));text('prev-close',row.previousClose?'Prev close '+money(row.previousClose.close)+(change==null?'':' · '+(change>0?'+':'')+number(change)+'%'):'');tone(el('prev-close'),change);
        text('quote-detail',quoteAge(row)+' · spread '+(row.quote?.askPrice>0&&row.quote?.bidPrice>0?money(row.quote.askPrice-row.quote.bidPrice):'—'));el('quote-detail').title=when(row.quote?.timestamp);
        const buyReason=orderRestrictionHint(row,'buy'),sellReason=orderRestrictionHint(row,'sell');
        reviewLink('buy',buyReason,row,'watch-order-help');
        reviewLink('sell',sellReason,row,sellReason===buyReason?'watch-order-help':'watch-exit-help');
        text('order-help',buyReason);el('order-help').hidden=!buyReason;
        text('exit-help',sellReason);el('exit-help').hidden=!sellReason||sellReason===buyReason;
        const symbol=encodeURIComponent(row.membership.symbol);
        el('stock-news').href='/News?ticker='+symbol;
        el('stock-earnings').href='/Earnings?search='+symbol;
        el('follow').hidden=el('source').value==='prepared'||row.membership.wishlists.length>0||!el('follow-list').options.length;
        const accountStale=!connected||Date.now()-Date.parse(snapshot.account.readAtUtc)>=30000;
        // Holding appears only for a held stock; account provenance moves into the tooltip.
        el('holding-block').hidden=!row.holding&&row.holdingsKnown;
        text('quantity',accountStale?'Stale snapshot':row.holdingsKnown?row.holding?number(row.holding.quantity):'Not held':'Unknown');
        text('entry',money(row.holding?.averageEntry));text('value',money(row.holding?.marketValue));text('pl',money(row.holding?.unrealizedPl));tone(el('pl'),row.holding?.unrealizedPl);
        el('holding-block').title=(accountStale?'Stale: last observed values. ':'')+snapshot.account.detail+' · cost basis '+money(row.holding?.costBasis)+' · '+when(snapshot.account.readAtUtc);
        const technical=dailyTechnical(row);text('sma10',money(technical?.sma10));text('sma20',money(technical?.sma20));text('sma50',money(technical?.sma50));text('sma200',money(technical?.sma200));text('tech-time',dailyTechnicalDetail(row));
        text('rsi',number(technical?.rsi));text('ema',money(technical?.ema20)+' / '+money(technical?.ema50));text('ema200',money(technical?.ema200));text('atr',number(technical?.atr));
        const relativeVolume=technical?.relativeVolume;
        text('rvol',Number.isFinite(relativeVolume)&&relativeVolume>=0?number(relativeVolume):'—');
        // Strategy checks appear only once a strategy is chosen at the top.
        el('strategy-block').hidden=!selectedStrategyChoice();text('observation',observationLabel(row));renderStrategyChecks(row);
        el('rsi').parentElement.parentElement.title=dailyTechnicalDetail(row);
        showSessionVolume(row);
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
    // Separate provider attempt status from the retained stories in this view.
    function renderNews(){
        const articles=newsForAll?snapshot.news:snapshot.rows.find(x=>x.membership.symbol===selected)?.news||[];
        text('news-title',newsForAll?'News for this view':selected+' news');el('news-all').hidden=newsForAll;const status=window.tradingFlowWorkspaceStatus?.news;
        const state=status?.state==='failed'?'Latest feed refresh failed. ':status?.state==='pending'?'First refresh has not completed. ':status?.state==='partial'?'Latest feed refresh was partial. ':'';
        text('news-coverage',state+snapshot.newsCoverage);
        const unavailable=!newsForAll&&snapshot.rows.find(row=>row.membership.symbol===selected)?.newsSentiment?.available===false||snapshot.newsCoverage.startsWith('News is unavailable');
        const signature=JSON.stringify([articles,unavailable]);if(signature===newsSignature)return;newsSignature=signature;el('news-list').replaceChildren();
        if(!articles.length){const p=document.createElement('p');p.className='watch-fine';p.textContent=unavailable?'Stored news could not be read. Retry when the connection recovers.':'No matched stories in the stored 72-hour window. This does not establish complete news coverage.';el('news-list').append(p);return;}
        for(const article of articles) el('news-list').append(window.TradingFlowNews.create(article,{allowGroup:false}));
    }
    // A closed market can have old quotes, and an old holding stays visible with its original observation.
    function ageStockRow(node,row){
        const price=node.querySelector('.watch-row-price'), position=node.querySelector('.watch-row-position');
        price.querySelector('small').textContent=rowQuoteAge(row);
        price.title=spreadDetail(row)+' · '+when(row.quote?.timestamp);
        price.classList.toggle('watch-stale',!connected||Date.now()-Date.parse(row.quote?.timestamp)>120000);
        const stale=!connected||Date.now()-Date.parse(snapshot.account.readAtUtc)>=30000;
        position.querySelector('strong').textContent=!row.holdingsKnown?'Unknown':row.holding?number(row.holding.quantity)+' sh':'—';
        const pl=position.querySelector('small');
        pl.textContent=row.holding?(stale?'Last observed ':'')+money(row.holding.unrealizedPl):'';
        tone(pl,row.holding?.unrealizedPl);
        renderNewsSentiment(node.querySelector('.watch-news-summary'),row);
    }
    // Age observations without rebuilding rows or resetting the chart and focus.
    function ageRows(){
        if(!snapshot)return;
        for(const row of snapshot.rows){
            const node=rowNodes.get(row.membership.symbol);
            if(!node)continue;
            ageStockRow(node,row);
            updateRowAction(node,row);
        }
    }
    el('source').onchange=()=>{page=0;el('search').value='';connect();};el('search').oninput=()=>{clearTimeout(searchTimer);searchTimer=setTimeout(()=>{page=0;connect();},300);};
    el('strategy').onchange=()=>connect();
    // A refresh requests new evidence explicitly; completed observations do not silently become continuous background trading jobs.
    el('strategy-refresh').onclick=()=>{
        const submission=assessmentSubmissions.get(currentSignature);
        // An explicit refresh starts new collection only after the previous attempt is known to be terminal.
        if(submission?.job&&['completed','failed','cancelled'].includes(submission.job.status))assessmentSubmissions.delete(currentSignature);
        restoredView=false;currentJob=null;currentStocks=new Map();currentSignature='';currentFailure='';applyCurrentAssessments();render();observeCurrentStrategy(generation);
    };
    el('strategy-cancel').onclick=async()=>{const version=generation,id=currentJob?.jobId;if(!id)return;try{const job=await cancelAssessment(id);if(version===generation){currentJob=job;text('strategy-status',job.detail);applyCurrentAssessments();render();}}catch(error){if(version===generation)text('strategy-status',error.message);}};
    el('prev').onclick=()=>{page=Math.max(0,page-1);connect();};el('next').onclick=()=>{page++;connect();};el('sort').onchange=render;
    el('news-all').onclick=()=>{newsForAll=true;renderNews();};
    el('import-open').onclick=()=>{el('import').hidden=false;el('import').elements.name.focus();};el('import-close').onclick=()=>{el('import').hidden=true;el('import-open').focus();};
    el('import').onsubmit=async event=>{event.preventDefault();const form=event.currentTarget,button=form.querySelector('[type=submit]');button.disabled=true;try{const saved=await api('screens',{name:form.elements.name.value,url:form.elements.url.value});await sources(saved.key);el('import').hidden=true;form.reset();connect();}catch(e){text('import-status',e.message);}finally{button.disabled=false;}};
    el('follow').onsubmit=async event=>{event.preventDefault();const button=event.currentTarget.querySelector('button');button.disabled=true;try{await api('follow',{source:el('source').value,symbol:selected,wishlistId:el('follow-list').value});text('follow-status','Added. Membership refreshes with the next update.');}catch(e){text('follow-status',e.message);}finally{button.disabled=false;}};
    document.addEventListener('workspace-status-changed',()=>render());
    document.addEventListener('click',event=>{
        const link=event.target.closest('a[href]');
        if(!event.defaultPrevented&&link?.origin===location.origin&&link.pathname!==location.pathname)saveDeskReturn();
    });
    // Save the tab's view and release observations when leaving Desk.
    window.addEventListener('pagehide', () => {
        saveDeskReturn();
        stream?.close();
        generation++;
        void deskChart?.dispose();
        deskChart = null;
        chartSymbol = null;
        const root = el('focus-content').querySelector('[data-stock-chart]');
        if (root) root.dataset.symbol = '';
    });
    // A browser-cached page carries its old authenticated markup; recheck identity before reading saved state.
    window.addEventListener('pageshow',event=>{
        if(!event.persisted)return;
        const order=document.getElementById('OrderPopup');
        if(order?.open)order.close();
        started=false;
        location.reload();
    });
    setInterval(()=>{ageRows();updateHealth();if(snapshot&&selected)renderFocus();},1000);
    // Load both catalogs before connecting; a strategy-list error must not hide otherwise available quotes.
    // A link to a recorded strategy run opens the Strategy runs section with that run loaded.
    if(explicit&&incoming.get('runId')&&document.getElementById('watch-runs'))document.getElementById('watch-runs').open=true;
    el('search').value=(savedDesk?.search??incoming.get('search')??'').slice(0,80);
    const initialSort=savedDesk?.sort??incoming.get('sort');
    if([...el('sort').options].some(option=>option.value===initialSort))el('sort').value=initialSort;
    setView(savedDesk?.view??incoming.get('view'));
    const initialPage=Number(savedDesk?.page??incoming.get('page'));
    if(Number.isInteger(initialPage)&&initialPage>=0&&initialPage<=10000)page=initialPage;

    // Validate sources and exact strategy identities against current catalogs on first entry and retry.
    async function start(){
        try{
            const requested=started?captureDeskReturn():savedDesk;
            const source=started?requested.source:savedDesk?.source??incoming.get('source')??(reviewKey?'prepared':'all');
            await sources(source,!requested);
            const strategy=new URLSearchParams(incoming);
            if(requested){
                for(const key of strategyKeys){
                    if(requested[key])strategy.set(key,requested[key]);else strategy.delete(key);
                }
            }
            try{await loadStrategies(strategy);}
            catch(error){strategyLoadError=error.message;strategyChoices=[];el('strategy').replaceChildren(new Option('Select a strategy',''));}
            started=true;
            const restore=source===el('source').value?requested:null;
            if(source!==el('source').value)page=0;
            connect(restore,rememberedEntry||!!requested);
        }catch(e){text('connection',e.message);el('empty').querySelector('strong').textContent='Sources unavailable';el('retry').hidden=false;}
    }
    el('retry').onclick=start;start();
})();
