/* Desk live quotes: the one-use quote socket, its frames and how a row's quote is replaced or labelled. Display only. */
'use strict';
window.createDeskQuoteStream = ({ getGeneration, getSnapshot, getSelected, patchRow, renderFocus, updateHealth }) => {
    // Live display quotes for the visible stocks: socket state, provider (Alpaca SIP) state and the latest item per stock.
    const quoteSocket = { connection: null, view: null, state: 'idle', upstream: '', reason: '', items: new Map(), attempt: 0, closing: Promise.resolve() };
    // "Live" needs the open socket, a streamed quote and Alpaca's current confirmation of that stock's subscription.
    const isStreaming = row => row.quote?.source==='socket'&&quoteSocket.state==='live'&&row.quoteStream==='acknowledged';

    // Close the quote socket; quotes already shown keep their own time and source labels.
    // Returns when the old socket has closed, so its server slot is free before another socket asks for one.
    function stopQuoteSocket(state='idle',reason=''){
        const active=quoteSocket.connection;quoteSocket.connection=null;
        quoteSocket.state=state;quoteSocket.reason=reason;
        const done=active?active.stop().catch(()=>{}):Promise.resolve();
        quoteSocket.closing=Promise.all([quoteSocket.closing,done]);
        return quoteSocket.closing;
    }
    // A new view (stream reconnect, source, page or search) starts without live quotes; a renewed capability keeps them.
    function onQuoteView(data,version){
        if(version!==getGeneration())return;
        if(data.view!==quoteSocket.view){quoteSocket.view=data.view;quoteSocket.items.clear();quoteSocket.upstream='';}
        quoteSocket.reason=data.reason||'';
        if(data.capability)void openQuoteSocket(data.capability,data.view,version);
        updateHealth();
    }
    // Open the Desk quote socket and present the capability once, in the invocation only; it is never stored or reused.
    async function openQuoteSocket(capability,view,version){
        const attempt=++quoteSocket.attempt;
        await stopQuoteSocket('connecting',quoteSocket.reason);
        if(attempt!==quoteSocket.attempt||version!==getGeneration())return;
        if(!window.signalR){quoteSocket.state='closed';quoteSocket.reason='Live-quote client unavailable';updateHealth();return;}
        const active=new window.signalR.HubConnectionBuilder()
            .withUrl('/api/v1/desk/quotes/hub',{transport:window.signalR.HttpTransportType.WebSockets,skipNegotiation:true})
            .configureLogging(window.signalR.LogLevel.None).build();
        quoteSocket.connection=active;
        const closed=reason=>{if(quoteSocket.connection!==active)return;stopQuoteSocket('closed',reason);refreshLiveRows();};
        // A server refusal (for example the per-user socket budget) arrives as the close error; show its reason.
        const serverReason=error=>error?.message?.replace(/^.*HubException: /,'');
        active.onclose(error=>closed(serverReason(error)||quoteSocket.reason||'Live-quote socket closed'));
        try{
            await active.start();
            if(quoteSocket.connection!==active||version!==getGeneration()){void active.stop().catch(()=>{});return;}
            quoteSocket.state='live';updateHealth();
            active.stream('WatchQuotes',capability).subscribe({
                next(frame){if(quoteSocket.connection===active&&version===getGeneration()&&frame.view===view)applyQuoteFrame(frame);},
                complete(){closed(quoteSocket.reason||'Live-quote socket ended');},
                error(error){closed(serverReason(error)||'Live-quote socket closed');}
            });
        }catch(error){closed(serverReason(error)||'Live-quote socket could not connect');}
    }
    // Patch only the rows whose quote changed; the table is not rebuilt or re-sorted for a quote tick.
    function applyQuoteFrame(frame){
        if(frame.kind==='ended'){quoteSocket.reason=frame.reason||'';return;}
        quoteSocket.upstream=frame.upstream;
        for(const symbol of frame.removed||[])quoteSocket.items.delete(symbol);
        const changed=new Set(frame.removed||[]);
        for(const item of frame.quotes||[]){
            if(quoteSocket.items.size>=50&&!quoteSocket.items.has(item.symbol))continue;
            quoteSocket.items.set(item.symbol,item);changed.add(item.symbol);
        }
        if(getSnapshot())for(const row of getSnapshot().rows)if(changed.has(row.membership.symbol)&&mergeLiveQuote(row))patchRow(row);
        if(getSnapshot()&&getSelected()&&changed.has(getSelected()))renderFocus();
        updateHealth();
    }
    // Relabel streamed rows when the socket stops; their prices stay with their own provider times.
    function refreshLiveRows(){
        if(getSnapshot())for(const row of getSnapshot().rows)if(row.quote?.source==='socket')patchRow(row);
        if(getSnapshot()&&getSelected())renderFocus();
        updateHealth();
    }
    // Use a streamed quote only when it is newer than the row's; a snapshot quote is never relabelled as streamed.
    function mergeLiveQuote(row){
        const item=quoteSocket.items.get(row.membership.symbol);
        row.quoteStream=item?.state||null;
        if(!item?.timestamp||item.state!=='acknowledged')return !!item;
        const shown=Date.parse(row.quote?.timestamp||0)||0, offered=Date.parse(item.timestamp);
        if(row.quote?.source==='socket'?offered<shown:offered<=shown)return true;
        row.quote={ticker:item.symbol,bidPrice:item.bidPrice,askPrice:item.askPrice,midPrice:item.midPrice,bidSize:item.bidSize,askSize:item.askSize,
            timestamp:item.timestamp,receivedAtUtc:item.receivedAtUtc,feed:item.feed||'sip',source:'socket'};
        return true;
    }
    // Describe the quote socket from its own state and Alpaca's replies, never from silence.
    function quoteSocketSummary(){
        const states=[...quoteSocket.items.values()].map(item=>item.state);
        const count=state=>states.filter(value=>value===state).length;
        if(quoteSocket.state==='connecting')return 'live-quote socket connecting · snapshot quotes meanwhile';
        if(quoteSocket.state!=='live')return 'snapshot quotes only'+(quoteSocket.reason?' · '+quoteSocket.reason:'');
        if(quoteSocket.upstream==='unavailable')return 'live-quote socket open · Alpaca SIP stream unavailable, snapshot quotes only';
        const parts=['live-quote socket · Alpaca confirmed '+count('acknowledged')+'/'+getSnapshot().rows.length];
        if(count('pending'))parts.push(count('pending')+' awaiting Alpaca confirmation');
        if(count('unacknowledged'))parts.push(count('unacknowledged')+' not confirmed by Alpaca');
        if(count('refused'))parts.push(count('refused')+' over the shared quote limit');
        return parts.join(' · ');
    }

    // A new connection starts without the previous view's live quotes.
    function resetQuoteView() {
        quoteSocket.view = null;
        quoteSocket.items.clear();
        quoteSocket.reason = '';
    }

    return { isStreaming, stopQuoteSocket, onQuoteView, mergeLiveQuote, quoteSocketSummary, resetQuoteView };
};
