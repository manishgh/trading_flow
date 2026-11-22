/* Desk market facts of one row: completed daily values, session volume and the change since the previous close. */
'use strict';
window.createDeskMarketView = ({ el, getSnapshot, isConnected }) => {
    const { money, number, when, exchangeDate } = window.TradingFlowDeskFormat;

    // Use only the server's completed daily observation; never substitute quotes, another ticker or future bars.
    function dailyTechnical(row){
        const technical=row.technical,stamp=Date.parse(technical?.timestamp),readAt=Date.parse(getSnapshot()?.readAtUtc);
        return technical&&['ready','insufficient'].includes(row.observation?.state)&&technical.timeframe==='1d'&&
            (!technical.ticker||technical.ticker.toUpperCase()===row.membership.symbol.toUpperCase())&&
            Number.isFinite(stamp)&&Number.isFinite(readAt)&&stamp<=readAt?technical:null;
    }
    // Keep the bar/feed source visible in detail and explain retained history when the stream disconnects.
    function dailyTechnicalDetail(row){
        const technical=dailyTechnical(row);
        return technical?(isConnected()?'':'Disconnected · last observed ')+ 'Completed 1d bar · '+when(technical.timestamp)+' · '+(technical.dataFeed||'Feed unavailable'):
            'Daily indicators unavailable · '+(row.observation?.detail||'No verified completed daily snapshot.');
    }
    // Accept only the server's matching daily volume receipt; completed technical-bar volume is never a substitute.
    function sessionVolume(row){
        const value=row.sessionVolume,readAt=Date.parse(getSnapshot()?.readAtUtc),asOf=Date.parse(value?.asOfUtc),barAt=Date.parse(value?.barTimestamp);
        const date=value?.sessionDate,today=exchangeDate(getSnapshot()?.readAtUtc);
        const validDate=typeof date==='string'&&/^\d{4}-\d{2}-\d{2}$/.test(date)&&Number.isFinite(Date.parse(date+'T00:00:00Z'))&&new Date(date+'T00:00:00Z').toISOString().slice(0,10)===date;
        return value&&String(value.ticker||'').toUpperCase()===row.membership.symbol.toUpperCase()&&
            typeof value.volume==='number'&&Number.isFinite(value.volume)&&value.volume>=0&&
            Number.isFinite(readAt)&&Number.isFinite(asOf)&&Number.isFinite(barAt)&&barAt<=asOf&&asOf<=readAt&&
            validDate&&today&&date<=today&&date===exchangeDate(value.barTimestamp)&&String(value.feed||'').toLowerCase()==='sip'?value:null;
    }
    // Retain SIP volume and receipt time in the selected stock's daily detail when that element is present.
    function showSessionVolume(row){
        const node=el('sip-volume');
        if(!node)return;
        const value=sessionVolume(row);
        node.textContent=value
            ? new Intl.NumberFormat().format(value.volume)+' SIP shares · '+value.sessionDate+' NY · observed '+when(value.asOfUtc)
            : 'SIP session volume unavailable.';
        node.title='Daily aggregate includes regular and extended hours; excludes overnight-feed trades.';
    }
    // Display completed 1d values independently of quote freshness, and keep SIP daily volume separate.
    function renderDailyCells(node,row){
        const technical=dailyTechnical(row), detail=dailyTechnicalDetail(row), rvol=node.querySelector('.watch-rvol');
        const relative=technical?.relativeVolume, atr=technical?.atr;
        rvol.querySelector('strong').textContent=Number.isFinite(relative)&&relative>=0?number(relative)+'×':'—';
        // The column header says "RVOL · ATR", so the cell shows the ATR dollar value without repeating the label.
        rvol.querySelector('small').textContent=Number.isFinite(atr)&&atr>=0?money(atr):'—';
        rvol.title='RVOL '+(Number.isFinite(relative)?number(relative)+'×':'—')+' · ATR '+money(atr)+' · '+detail;
    }

    // The daily indicators of a Desk row, in column order.
    const rowTrends=[['ema21','E21','EMA 21'],['sma50','S50','SMA 50'],['sma200','S200','SMA 200']];
    // Shows for each daily indicator whether the current price is above (▲) or below (▼) it; "—" when it has no value yet.
    function renderTrendCell(node,row){
        const cell=node.querySelector('.watch-row-trend'), technical=dailyTechnical(row);
        const price=row.quote?.midPrice>0?row.quote.midPrice:row.previousClose?.close;
        if(!cell.childElementCount)cell.append(...rowTrends.map(()=>{const chip=document.createElement('span');chip.className='watch-trend-chip';return chip;}));
        rowTrends.forEach(([key,short,name],index)=>{
            const chip=cell.children[index], value=technical?.[key];
            const known=Number.isFinite(value)&&value>0&&price>0, above=known&&price>=value;
            chip.textContent=short+(known?above?'▲':'▼':'—');
            chip.dataset.trend=known?above?'above':'below':'none';
            chip.title=known?name+' (day) '+money(value)+' · price '+money(price)+' is '+number(Math.abs(price/value-1)*100)+'% '+(above?'above':'below')+' · '+dailyTechnicalDetail(row)
                :name+' (day) has no value yet · '+dailyTechnicalDetail(row);
        });
    }

    // True only during the regular New York session, when "mid vs previous close" really is today's change.
    // The server sends the session as Closed, Overnight, Premarket, Regular, AfterHours or Unknown.
    function isRegularSession(){
        return String(getSnapshot()?.session||'').toLowerCase().startsWith('regular');
    }

    // Names the quote that is being compared outside regular hours, without claiming the market is closed when the
    // session is overnight or could not be read.
    function quoteSessionLabel(row){
        const session=String(getSnapshot()?.session||'').toLowerCase();
        if(row.quote?.feed==='overnight'||session==='overnight')return 'Overnight quote';
        if(session.startsWith('pre'))return 'Pre-market quote';
        if(session.startsWith('post')||session==='afterhours')return 'After-hours quote';
        if(session.startsWith('closed'))return 'Latest quote (market closed)';
        return 'Latest quote (session unknown)';
    }

    // Says exactly what the percentage compares, e.g. "After-hours quote vs 2026-11-20 close $594.51". The previous
    // close is the latest completed session before today in New York, so on a weekday evening the figure includes
    // today's regular-session move as well as the after-hours move.
    function changeBasisTitle(row){
        const date=row.previousClose?.sessionDate;
        return quoteSessionLabel(row)+' vs '+(date?date+' close ':'previous close ')+money(row.previousClose?.close)+
            '. This includes every move since that close, not only the latest session.';
    }

    // Formats the midpoint's percent change against the server's previous close with a non-colour direction glyph;
    // fails closed when either price is absent (the tooltip then still names the previous close when it is known).
    function formatRowDayChange(row){
        const prev = row.previousClose?.close, mid = row.quote?.midPrice;
        const fallbackTitle = prev > 0 ? 'Prev close ' + money(prev) : '';
        if (!(prev > 0) || typeof mid !== 'number' || !Number.isFinite(mid)) return { text: '—', value: null, title: fallbackTitle };
        const pct = (mid / prev - 1) * 100;
        if (!Number.isFinite(pct)) return { text: '—', value: null, title: fallbackTitle };
        const abs = Math.abs(pct).toFixed(2) + '%';
        const title = isRegularSession() ? 'Today: midpoint vs previous close ' + money(prev) : changeBasisTitle(row);
        if (pct > 0) return { text: '▲ +' + abs, value: pct, title };
        if (pct < 0) return { text: '▼ \u2212' + abs, value: pct, title };
        return { text: '0.00%', value: 0, title };
    }

    // Relabels the change column (text and tooltip) so it never claims "Day %" for an after-hours or weekend comparison.
    function renderChangeHeader(){
        const head=el('change-head');
        if(!head)return;
        const regular=isRegularSession();
        head.textContent=regular?'Price · today':'Price · since close';
        head.title=regular
            ?'Bid/ask midpoint now and its percent change since the previous close'
            :'Latest bid/ask midpoint and its percent change since the previous completed session\u2019s close; outside regular hours this includes every move since that close';
    }

    return { dailyTechnical, dailyTechnicalDetail, showSessionVolume, renderDailyCells, renderTrendCell, formatRowDayChange, renderChangeHeader };
};
