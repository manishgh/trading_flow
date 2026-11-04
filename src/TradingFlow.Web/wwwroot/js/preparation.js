(() => {
    'use strict';
    const el = id => document.getElementById('prep-' + id);
    const root=document.querySelector('.prep-page');if(!root||root.dataset.initialized)return;root.dataset.initialized='true';
    const draftKey = 'preparation-draft:' + location.origin + ':' + root.dataset.preparationOwner;
    let settings, definition, detail, selectedOccurrence, metadata, catalog, shown = 30;
    let followingLatest=true, reviewDirty=false, initialized=false, requestVersion=0, refreshing=0, available=[], receivedAt=0, stopped=false, commandInFlight=false, editorDirty=false;
    function showView(view) {
        document.querySelectorAll('[data-prep-panel]').forEach(x=>x.hidden=x.dataset.prepPanel!==view);
        document.querySelectorAll('[data-prep-view]').forEach(x=>x.setAttribute('aria-pressed',String(x.dataset.prepView===view)));
    }
    document.querySelectorAll('[data-prep-view]').forEach(x=>x.onclick=()=>showView(x.dataset.prepView));
    function clearReview() { followingLatest=true;reviewDirty=false;el('update').textContent='';el('latest').hidden=true; selectedOccurrence=null; pendingRun=null; pendingHandoff=null;pendingCandidate=null;pendingCuration=null;el('review').hidden=true;el('empty').hidden=false; }
    const selected = new Set(); let pendingHandoff, pendingRun, pendingCandidate, pendingCuration;
    async function api(path = '', body) {
        const options=body===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)};options.signal=AbortSignal.timeout(30000);
        const response = await window.tradingFlowFetch('/api/v1/' + path, options);
        const raw=await response.text();let data;try{data=raw?JSON.parse(raw):null;}catch{throw new Error('Preparation returned an unreadable response. Retry loading or sign in again.');}
        if(!response.ok){const error=new Error(data?.message||(response.status===404?'This workflow is no longer available. Open another workflow or save a new one.':response.status===401||response.status===403?'Sign in again to access preparation.':'Unable to load preparation. Retry the request.'));error.status=response.status;throw error;}return data;
    }
    const status = text => { el('status').textContent = text; };
    async function action(fn) { if(commandInFlight)return;commandInFlight=true;root.inert=true;root.setAttribute('aria-busy','true');try { await fn(); } catch (e) { status(e.name==='TimeoutError'?'Request timed out. Retry; the same collection or handoff key will be retained.':e.message); } finally {commandInFlight=false;root.inert=false;root.removeAttribute('aria-busy');syncControls();} }
    function button(label, fn) { const b=document.createElement('button'); b.textContent=label; b.type='button'; b.onclick=()=>action(fn); return b; }
    function line(parent, text) { const p=document.createElement('p'); p.textContent=text; parent.append(p); }
    function when(value) { const d=new Date(value); return d.toLocaleString(undefined,{timeZone:'America/New_York'})+' New York · '+d.toLocaleString()+' local · '+d.toISOString(); }
    function value(key) { return key.split('.').reduce((x,k)=>x[k],settings); }
    function set(key,v) { const parts=key.split('.'); let target=settings; for(const part of parts.slice(0,-1)) target=target[part]; target[parts.at(-1)]=v; }
    function retain(dirty=true) { editorDirty=dirty;try{sessionStorage.setItem(draftKey,JSON.stringify({settings,definition,editorDirty}));}catch{} }
    function renderEditor() {
        el('name').value=settings.name; el('merge').value=settings.merge; el('fields').replaceChildren();
        for(const field of metadata.fields) {
            const label=document.createElement('label'); label.textContent=field.label; const input=document.createElement('input'); input.dataset.key=field.key;
            input.type=field.kind==='boolean'?'checkbox':'number'; input.step=field.kind==='optional'?'any':'1';
            if(field.kind==='boolean') input.checked=value(field.key); else input.value=value(field.key)??'';
            input.oninput=()=>{set(field.key,field.kind==='boolean'?input.checked:input.value===''?null:Number(input.value));retain();};
            label.append(input); el('fields').append(label);
        }
        renderSources(); el('toggle').disabled=!definition; el('toggle').textContent=settings.enabled?'Pause workflow':'Enable workflow';
        el('revision').textContent=definition?'Saved revision '+definition.revision+' · '+definition.contentSha256:'';
        el('save').textContent=definition?'Save settings':'Save paused workflow';el('draft-note').textContent=definition?'Editing saved workflow. Save changes before collecting.':'Unsaved workflow · choose at least one source, then save paused to review its schedule.';syncControls();
    }
    function renderSources() {
        el('sources').replaceChildren(); settings.sources.forEach((source,index)=>{
            const card=document.createElement('div');card.className='prep-card';const title=source.kind==='wishlist'?[...el('wishlist').options].find(x=>x.value===(source.wishlistId||source.key))?.textContent:source.query;line(card,(source.kind==='wishlist'?'Wishlist: ':'Finviz: ')+(title||source.key));
            const label=document.createElement('label');label.textContent='Required source';const required=document.createElement('input');required.type='checkbox';required.checked=source.required;
            required.onchange=()=>{source.required=required.checked;retain();};label.append(required);card.append(label,button('Remove source',()=>{settings.sources.splice(index,1);renderSources();retain();}));el('sources').append(card);
        });el('source-status').textContent=settings.sources.length?settings.sources.length+' selected source'+(settings.sources.length===1?'':'s'):'Add at least one wishlist or Finviz filter before saving.';
    }
    function syncControls(){
        el('new').disabled=el('setup-new').disabled=!initialized;el('list').disabled=el('open').disabled=!initialized||!available.length;el('refresh').disabled=!initialized||!definition;
        root.querySelector('[data-prep-view="history"]').disabled=!definition;root.querySelector('[data-prep-view="settings"]').disabled=!initialized;root.querySelector('[data-prep-view="alerts"]').disabled=!initialized;
        el('schedule').hidden=!definition;el('setup').hidden=!!definition||available.length>0;syncCollection();
    }
    function syncCollection(){
        const current=definition&&detail?.definition.workflowId===definition.workflowId;
        const busy=current&&detail.history.some(x=>['queued','running','cancelling'].includes(x.status));
        const now=detail?.readAtUtc?Date.parse(detail.readAtUtc)+Date.now()-receivedAt:Date.now();
        const windows=current?(detail.collectionWindows||[]):[];
        for(const phase of ['nightly','morning'])el(phase).disabled=!current||busy||!windows.some(x=>x.phase===phase&&Date.parse(x.scheduledAtUtc)<=now&&now<Date.parse(x.deadlineUtc));
        el('collection-status').textContent=!current?'Save and open a workflow to see collection availability.':detail.planningError?detail.planningError:busy?'Collection is queued or running. Results refresh automatically every 10 seconds.':windows.some(x=>Date.parse(x.scheduledAtUtc)<=now&&now<Date.parse(x.deadlineUtc))?'An exchange-session collection window is open. Run manually or enable the saved schedule.':'Manual collection is outside the configured exchange-session window. The next scheduled times are listed below.';
    }
    async function list() { available=await api('preparation');el('list').replaceChildren();if(!available.length)el('list').add(new Option('No saved workflows',''));for(const row of available)el('list').add(new Option(row.settings.name+(row.settings.enabled?' · enabled':' · paused'),row.workflowId));if(definition&&available.some(x=>x.workflowId===definition.workflowId))el('list').value=definition.workflowId;syncControls();return available; }
    async function load(id, replaceEditor=true, selectLatest=true, silent=false) {
        if(!id){status('Create and save a workflow first.');return;}
        const version=silent?requestVersion:++requestVersion;refreshing++;
        let loaded;try{loaded=await api('preparation/'+id);}finally{refreshing--;}
        if(version!==requestVersion||(silent&&(definition?.workflowId!==id||commandInFlight))||stopped)return;
        detail=loaded;receivedAt=Date.now();
        if(replaceEditor){clearReview();definition=detail.definition;settings=structuredClone(definition.settings);renderEditor();retain(false);}
        syncControls();
        el('upcoming').replaceChildren();if(detail.planningError)line(el('upcoming'),detail.planningError);
        detail.upcoming.forEach(x=>line(el('upcoming'),x.phase+' for '+x.targetDate+' · '+when(x.scheduledAtUtc)));
        el('history').replaceChildren();detail.history.forEach(row=>{const card=document.createElement('div');card.className='prep-card';line(card,row.slot.phase+' · '+row.slot.targetDate+' · '+row.status+' · '+when(row.slot.scheduledAtUtc));if(row.error)line(card,row.error);
            if(row.snapshot)card.append(button('Review snapshot',()=>review(row)));else if(['queued','running','cancelling'].includes(row.status))card.append(button('Cancel occurrence',async()=>{await api(`preparation/${id}/occurrences/${row.id}/cancel`,{});await load(id,false);}));el('history').append(card);});
        if(selectLatest) {
            const latest=detail.history.find(x=>x.snapshot);
            if(latest&&(!selectedOccurrence||(followingLatest&&!reviewDirty))){if(selectedOccurrence?.snapshot.revisionId!==latest.snapshot.revisionId||selectedOccurrence?.status!==latest.status)review(latest,false,false);}
            else if(latest&&selectedOccurrence?.id!==latest.id) {
                el('update').textContent='A newer result is available. Your current review has been kept.';el('latest').hidden=false;
            }
        }
        if(!detail.history.length){line(el('history'),'No collection has run for this workflow yet. Review its schedule or start a manual run when its window opens.');el('empty').textContent='Workflow saved. No preparation result yet. Review the schedule below; results appear automatically after collection.';}
        if(!silent)status('History refreshed. '+(detail.definition.settings.enabled?'Scheduling enabled.':'Workflow paused.'));
        document.dispatchEvent(new CustomEvent('preparation-changed',{detail:{count:available.length}}));
    }
    function review(row, navigate=true, historical=true) {
        followingLatest=!historical;reviewDirty=false;el('update').textContent=historical?'Viewing a saved historical result.':'Latest available result for '+row.slot.targetDate;el('latest').hidden=true;
        if(navigate)showView('daily');el('empty').hidden=true;
        selectedOccurrence=row;el('reason').value='';el('additions').value='';selected.clear();pendingHandoff=null;pendingCandidate=null;pendingCuration=null;shown=30;const snap=row.snapshot;
        snap.symbols.filter(x=>x.readiness==='complete').forEach(x=>selected.add(x.symbol));el('review').hidden=false;el('audit').replaceChildren();
        line(el('audit'),row.slot.phase+' · '+row.slot.targetDate+' · '+snap.readiness+(Date.now()>=Date.parse(snap.expiresAtUtc)?' · Expired — collect a fresh result to continue.':''));line(el('audit'),'Observation cutoff: '+when(snap.cutoffUtc));line(el('audit'),'Handoff expires: '+when(snap.expiresAtUtc));
        snap.reasons.forEach(x=>line(el('audit'),x));const provenance=document.createElement('details'),provenanceTitle=document.createElement('summary');provenanceTitle.textContent='Source provenance and revision';provenance.append(provenanceTitle);line(provenance,'Revision '+snap.revisionId);el('audit').append(provenance);snap.sources.forEach(x=>line(provenance,x.source.kind+' '+x.source.key+' · '+x.status+' · observed '+(x.observedAtUtc?when(x.observedAtUtc):'unavailable')+' · '+(x.reason||x.contentSha256||'')));
        if(row.slot.phase!=='morning')line(el('audit'),'Nightly evidence is prepared. Collect and review a fresh morning result before continuing.');
        line(el('audit'),'Removed since nightly: '+(snap.removedSymbols.join(', ')||'none'));renderMembers();if(navigate)el('review').scrollIntoView({block:'start'});
    }
    function renderMembers() {
        const snap=selectedOccurrence.snapshot;el('members').replaceChildren();
        snap.symbols.slice(0,shown).forEach(row=>{const card=document.createElement('details');card.className='prep-card';const summary=document.createElement('summary');summary.textContent=row.symbol+' · '+row.change+' · '+row.readiness;card.append(summary);
            const label=document.createElement('label');label.textContent='Include in reviewed membership';const check=document.createElement('input');check.type='checkbox';check.checked=selected.has(row.symbol);check.disabled=row.readiness!=='complete';check.onchange=()=>{reviewDirty=true;check.checked?selected.add(row.symbol):selected.delete(row.symbol);pendingHandoff=null;pendingCandidate=null;pendingCuration=null;el('handoff').disabled=el('paper').disabled=selected.size===0||selectedOccurrence.slot.phase!=='morning'||snap.readiness==='unavailable'||Date.now()>=Date.parse(snap.expiresAtUtc);};label.append(check);card.append(label);
            line(card,'Daily close '+(row.close??'unavailable')+' · '+row.historyCount+' sessions · '+row.feed+' / '+row.adjustment+' · gap '+(row.gapPercent??'unavailable')+'% · same-time RVOL '+(row.sameTimeRelativeVolume??'unavailable'));
            [...row.reasons,...row.context].forEach(x=>line(card,x));row.news.forEach(x=>{line(card,x.publishedAtUtc+' · '+x.headline);try{const url=new URL(x.url);if(['https:','http:'].includes(url.protocol)){const a=document.createElement('a');a.href=url.href;a.textContent='Open captured article';a.target='_blank';a.rel='noopener noreferrer';card.append(a);}else line(card,'Article link unavailable.');}catch{line(card,'Article link unavailable.');}});
            const market=document.createElement('a');market.href='/Desk?search='+encodeURIComponent(row.symbol);market.textContent='Open market detail';card.append(market);
            const audit=document.createElement('a');audit.href=`/api/v1/preparation/${definition.workflowId}/occurrences/${selectedOccurrence.id}/evidence/${encodeURIComponent(row.symbol)}`;audit.textContent=' Open captured evidence';audit.target='_blank';audit.rel='noopener';card.append(audit);line(card,'Evidence hash: '+row.evidenceSha256);el('members').append(card);});
        el('more').hidden=shown>=snap.symbols.length;el('handoff').disabled=selectedOccurrence.slot.phase!=='morning'||snap.readiness==='unavailable'||selected.size===0||Date.now()>=Date.parse(snap.expiresAtUtc);el('paper').disabled=el('handoff').disabled;
    }
    async function alerts() {
        const rows=await api('preparation/events');el('notifications').replaceChildren();if(!rows.length)line(el('notifications'),'No preparation alerts yet. Alerts appear after a configured collection completes.');rows.reverse().forEach(row=>{const alert=JSON.parse(row.payloadJson);el('notifications').append(button(when(row.occurredAtUtc)+' · '+alert.readiness,async()=>{await load(alert.workflowId,true,false);const occurrence=detail.history.find(x=>x.id===alert.occurrenceId&&x.snapshot?.revisionId===alert.revisionId);if(occurrence)review(occurrence);else {clearReview();showView('daily');status('Requested revision unavailable. No other result was substituted.');}}));});
    }
    el('name').oninput=()=>{settings.name=el('name').value;retain();};el('merge').onchange=()=>{settings.merge=el('merge').value;retain();};
    function newWorkflow(){if(!initialized)return;requestVersion++;clearReview();detail=null;definition=null;settings=structuredClone(metadata.defaults);showView('settings');renderEditor();retain();status('New workflow: choose a source, save paused, then review the schedule.');el('name').focus();}
    el('new').onclick=el('setup-new').onclick=newWorkflow;
    el('open').onclick=()=>action(async()=>{await load(el('list').value);showView('daily');});el('refresh').onclick=()=>action(()=>definition?load(definition.workflowId,false):Promise.resolve(status('Open a workflow first.')));
    el('add-wishlist').onclick=()=>{const id=el('wishlist').value;if(id){if(settings.sources.some(x=>x.kind==='wishlist'&&(x.wishlistId||x.key)===id)){status('That wishlist is already selected.');return;}if(settings.sources.length>=20){status('Select at most 20 sources.');return;}settings.sources.push({kind:'wishlist',key:id,wishlistId:id,required:true});renderSources();retain();}};
    el('add-query').onclick=()=>{const query=el('query').value.trim();if(query){if(settings.sources.some(x=>x.kind==='finviz'&&x.key===query)){status('That Finviz filter is already selected.');return;}if(settings.sources.length>=20){status('Select at most 20 sources.');return;}settings.sources.push({kind:'finviz',key:query,query,required:false});renderSources();retain();}};
    el('editor').onsubmit=e=>{e.preventDefault();action(async()=>{if(!settings.sources.length){status('Add at least one wishlist or Finviz filter before saving.');el('wishlist').focus();return;}definition=await api('preparation',{workflowId:definition?.workflowId||'00000000-0000-0000-0000-000000000000',expectedRevision:definition?.revision||0,settings:definition?settings:{...settings,enabled:false}});await list();await load(definition.workflowId);showView('daily');el('schedule').open=true;});};
    el('toggle').onclick=()=>action(async()=>{definition=await api('preparation',{workflowId:definition.workflowId,expectedRevision:definition.revision,settings:{...settings,enabled:!settings.enabled}});await list();await load(definition.workflowId);});
    for(const phase of ['nightly','morning'])el(phase).onclick=()=>action(async()=>{if(!definition||el(phase).disabled)return;pendingRun??={phase,idempotencyKey:crypto.randomUUID()};if(pendingRun.phase!==phase)pendingRun={phase,idempotencyKey:crypto.randomUUID()};await api(`preparation/${definition.workflowId}/run`,pendingRun);pendingRun=null;await load(definition.workflowId,false);});
    el('latest').onclick=()=>{const latest=detail?.history.find(x=>x.snapshot);if(latest)review(latest,true,false);};
    el('more').onclick=()=>{shown+=30;renderMembers();};el('alerts').onclick=()=>action(alerts);
    el('handoff').onclick=()=>action(async()=>{
        reviewDirty=true;
        const strategy=catalog.strategies[Number(el('strategy').value)];if(!strategy)throw new Error('Select an available research strategy.');
        pendingHandoff??={revisionId:selectedOccurrence.snapshot.revisionId,symbols:[...selected],reason:el('reason').value,idempotencyKey:crypto.randomUUID()};
        const handoff=await api(`preparation/${definition.workflowId}/occurrences/${selectedOccurrence.id}/handoff`,pendingHandoff);
        pendingCandidate??={contractVersion:'1.0',idempotencyKey:crypto.randomUUID(),mode:'backtest',universeSnapshotId:handoff.universeSnapshotId,strategies:[strategy.identity],asOfUtc:selectedOccurrence.snapshot.cutoffUtc};
        const run=await api('candidate-runs',pendingCandidate);pendingCandidate=null;
        pendingHandoff=null;status('Research candidate run created: '+run.candidateRunId);location.href='/Desk?runId='+encodeURIComponent(run.candidateRunId);
    });
    el('reason').oninput=()=>{reviewDirty=true;pendingHandoff=null;pendingCandidate=null;pendingCuration=null;};
    el('strategy').onchange=()=>{reviewDirty=true;pendingCandidate=null;};
    el('additions').oninput=()=>{reviewDirty=true;pendingCuration=null;};
    el('paper').onclick=()=>action(async()=>{
        reviewDirty=true;
        pendingHandoff??={revisionId:selectedOccurrence.snapshot.revisionId,symbols:[...selected],reason:el('reason').value,idempotencyKey:crypto.randomUUID()};
        await api(`preparation/${definition.workflowId}/occurrences/${selectedOccurrence.id}/handoff`,pendingHandoff);
        location.href='/Paper?preparationKey='+encodeURIComponent(pendingHandoff.idempotencyKey);
    });
    el('curate').onclick=()=>action(async()=>{
        reviewDirty=true;
        pendingCuration??={parentRevisionId:selectedOccurrence.snapshot.revisionId,addSymbols:el('additions').value.split(/[\s,]+/).filter(Boolean).map(x=>x.toUpperCase()),removeSymbols:selectedOccurrence.snapshot.symbols.filter(x=>!selected.has(x.symbol)).map(x=>x.symbol),reason:el('reason').value,idempotencyKey:crypto.randomUUID()};
        await api(`preparation/${definition.workflowId}/occurrences/${selectedOccurrence.id}/curate`,pendingCuration);pendingCuration=null;await load(definition.workflowId,false);
    });
    setInterval(()=>{syncCollection();if(selectedOccurrence&&Date.now()>=Date.parse(selectedOccurrence.snapshot.expiresAtUtc)){el('handoff').disabled=true;el('paper').disabled=true;}},1000);
    async function initialize(){
        initialized=false;el('retry').hidden=true;syncControls();status('Loading workflows…');
        try{
            const [editor,lists]=await Promise.all([api('preparation/editor'),api('wishlists')]);metadata=editor;
            el('wishlist').replaceChildren();(Array.isArray(lists)?lists:lists.wishlists||[]).forEach(x=>el('wishlist').add(new Option(x.name,x.id)));
            let draft;try{draft=JSON.parse(sessionStorage.getItem(draftKey));}catch{}
            settings=draft?.settings?{...structuredClone(metadata.defaults),...draft.settings,filters:{...metadata.defaults.filters,...draft.settings.filters}}:structuredClone(metadata.defaults);definition=draft?.definition||null;editorDirty=draft?.editorDirty??!!(draft&&(!definition||JSON.stringify(settings)!==JSON.stringify(definition.settings)));
            await list();let missing=false;
            if(definition&&!available.some(x=>x.workflowId===definition.workflowId)){definition=null;settings.enabled=false;missing=true;}
            initialized=true;renderEditor();syncControls();
            catalog={strategies:[]};el('strategy').replaceChildren();
            try{catalog=await api('strategies?mode=backtest');catalog.strategies.forEach((x,i)=>el('strategy').add(new Option(x.name+' · '+x.identity.semanticVersion+' · '+x.identity.contentSha256,i)));}catch{el('strategy').add(new Option('Strategy catalog unavailable — reload to retry',''));}
            const query=new URLSearchParams(location.search);
            if(query.has('workflow')){
                await load(query.get('workflow'),true,false);const row=detail.history.find(x=>x.id===query.get('occurrence')&&x.snapshot?.revisionId===query.get('revision'));
                if(row)review(row);else{clearReview();showView('daily');status('Requested revision unavailable. No other result was substituted.');}
            }else if(definition){await load(definition.workflowId,false);if(editorDirty)showView('settings');}
            else if(draft){showView('settings');status(missing?'Saved workflow is no longer available. Your settings are kept as an unsaved draft.':'Unsaved workflow restored. Choose sources and save paused.');retain();}
            else if(available.length)await load(available[0].workflowId);
            else{showView('daily');el('empty').textContent='No saved preparation workflows for this account. Configure your first workflow above.';status('Preparation is not configured yet.');}
            try{await alerts();}catch{line(el('notifications'),'Alerts could not be loaded. Use Refresh alerts to retry.');}
        }catch(e){status(e.message);el('retry').hidden=false;syncControls();}
    }
    el('retry').onclick=initialize;
    const refreshTimer=setInterval(async()=>{
        if(stopped||commandInFlight||!initialized||!definition||refreshing||document.hidden||root.closest('details')&&!root.closest('details').open)return;
        const id=definition.workflowId;
        try{await load(id,false,true,true);}catch(e){if(definition?.workflowId===id){el('update').textContent='Automatic refresh failed. Your current review is retained. '+e.message;}}
    },10000);
    window.addEventListener('pagehide',()=>{stopped=true;requestVersion++;clearInterval(refreshTimer);});
    window.addEventListener('pageshow',event=>{if(event.persisted)location.reload();});
    initialize();
})();
