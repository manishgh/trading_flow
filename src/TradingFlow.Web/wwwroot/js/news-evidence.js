/* Shared presentation only. Querying, classification and trading decisions stay server-side. */
window.TradingFlowNews = (() => {
  const symbols = value => [...new Set((Array.isArray(value) ? value : String(value || '').split(',')).map(x => String(x).trim().toUpperCase()).filter(Boolean))].sort();
  const safeUrl = value => { try {const url=new URL(value);return ['http:','https:'].includes(url.protocol) ? url.href : null;}catch{return null;} };
  const normalize = raw => ({
    tickers:symbols(raw.symbols || raw.tickers || raw.ticker), timestamp:raw.timestamp || raw.publishedAtUtc,
    headline:raw.headline || 'Untitled article',summary:raw.summary || '',provider:raw.provider || '',source:raw.source || '',
    classification:raw.classification || raw.category || 'Classification unavailable',why:raw.whyItMatters || '',url:safeUrl(raw.url),
    score:raw.sentimentScore !== null && raw.sentimentScore !== undefined && Number.isFinite(Number(raw.sentimentScore)) ? Number(raw.sentimentScore) : null
  });
  function patch(article,raw) {
    const item=normalize(raw), positive=item.score !== null && item.score > .15, negative=item.score !== null && item.score < -.15;
    article.classList.add('news-entry','news-item');article.classList.toggle('is-positive',positive);article.classList.toggle('is-negative',negative);
    article.dataset.newsTicker=item.tickers.join(', ');article.dataset.newsTimestamp=item.timestamp || '';article.dataset.newsUrl=item.url || '';
    article.dataset.newsKey=item.url || `${item.headline}|${item.timestamp || ''}`;
    const set=(key,value)=>{const node=article.querySelector(`[data-news-${key}]`);if(node.textContent!==String(value))node.textContent=value;return node;};
    set('tickers',item.tickers.join(', ') || 'MARKET');set('headline',item.headline);
    const date=new Date(item.timestamp),valid=Number.isFinite(date.getTime());
    const time=set('time',valid?`${date.toISOString().replace('T',' ').replace(/\.\d{3}Z$/,'')} UTC`:'Timestamp unavailable');
    if(valid)time.dateTime=date.toISOString();else time.removeAttribute('datetime');
    set('summary',item.summary).hidden=!item.summary;set('why',item.why).hidden=!item.why;
    set('source',[item.provider,item.source,item.classification].filter(Boolean).join(' · '));
    set('sentiment',item.score===null?'Sentiment unavailable':`${positive?'Positive':negative?'Negative':'Neutral'} ${item.score>0?'+':''}${item.score.toFixed(2)}`);
    const link=article.querySelector('[data-news-link]');link.hidden=!item.url;if(item.url)link.href=item.url;else link.removeAttribute('href');
    const group=article.querySelector('[data-group-action]');if(group){const equities=item.tickers.filter(x=>x!=='MARKET'&&/^[A-Z0-9.-]{1,16}$/.test(x));group.dataset.tickers=equities.join(', ');group.hidden=!equities.length;}
    return article;
  }
  function create(raw,{allowGroup=Boolean(document.getElementById('groupActionDialog'))}={}) {
    const article=document.createElement('article');article.dataset.newsItem='';
    const node=(tag,key,className)=>{const n=document.createElement(tag);n.dataset[`news${key}`]='';if(className)n.className=className;return n;};
    const meta=document.createElement('div');meta.className='news-timeline-meta';meta.append(node('strong','Tickers'),node('time','Time'),node('span','Sentiment'));
    const footer=document.createElement('div');footer.className='news-entry-footer';footer.append(node('span','Source','muted'));
    if(allowGroup){const button=document.createElement('button');button.type='button';button.className='button secondary';button.dataset.groupAction='add';button.textContent='Add symbols to group';footer.append(button);}
    const link=node('a','Link');link.target='_blank';link.rel='noopener noreferrer';link.textContent='Open article';footer.append(link);
    article.append(meta,node('strong','Headline','news-headline'),node('p','Summary'),node('p','Why','news-why'),footer);
    return patch(article,raw);
  }
  return {create,patch,symbols,normalize};
})();
