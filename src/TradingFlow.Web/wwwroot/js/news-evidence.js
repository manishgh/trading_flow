/* Shared presentation only. Querying, classification and trading decisions stay server-side. */
window.TradingFlowNews = (() => {
  // Display names mirror NewsDisplayTaxonomy; no topic inference runs in the browser.
  const categoryLabels = {'company':'Company','sector':'Sector','financial':'Financial / macro','policy':'Government / policy','global':'Global events','market':'Other market'};
  const sectorLabels = {'technology':'Technology','healthcare':'Healthcare','financials':'Financials','energy':'Energy','industrials':'Industrials','consumer-discretionary':'Consumer discretionary','consumer-staples':'Consumer staples','utilities':'Utilities','real-estate':'Real estate','materials':'Materials','communication-services':'Communication services'};
  const topicKeys = (value, labels) => Array.isArray(value) ? Object.keys(labels).filter(key => value.some(item => String(item).toLowerCase() === key)) : [];
  const symbols = value => [...new Set((Array.isArray(value) ? value : String(value || '').split(',')).map(x => String(x).trim().toUpperCase()).filter(Boolean))].sort();
  const safeUrl = value => { try {const url=new URL(value);return ['http:','https:'].includes(url.protocol) ? url.href : null;}catch{return null;} };
  const normalize = raw => ({
    tickers:symbols(raw.symbols || raw.tickers || raw.ticker), timestamp:raw.timestamp || raw.publishedAtUtc,
    headline:raw.headline || 'Untitled article',summary:raw.summary || '',provider:raw.provider || '',source:raw.source || '',
    classification:raw.classification || raw.category || 'Classification unavailable',why:raw.whyItMatters || '',url:safeUrl(raw.url),
    categories:topicKeys(raw.categories, categoryLabels),sectors:topicKeys(raw.sectors, sectorLabels),
    categoryRationale:raw.categoryRationale || '',categoryVersion:raw.categoryVersion || '',
    score:raw.sentimentScore !== null && raw.sentimentScore !== undefined && Number.isFinite(Number(raw.sentimentScore)) ? Number(raw.sentimentScore) : null
  });
  function patch(article,raw) {
    const item=normalize(raw), positive=item.score !== null && item.score > .15, negative=item.score !== null && item.score < -.15;
    article.classList.add('news-entry','news-item');article.classList.toggle('is-positive',positive);article.classList.toggle('is-negative',negative);
    article.dataset.newsTicker=item.tickers.join(', ');article.dataset.newsTimestamp=item.timestamp || '';article.dataset.newsUrl=item.url || '';
    article.dataset.newsKey=item.url || `${item.headline}|${item.timestamp || ''}`;
    const set=(key,value)=>{const node=article.querySelector(`[data-news-${key}]`);if(node.textContent!==String(value))node.textContent=value;return node;};
    set('tickers',item.tickers.map(x=>x==='MARKET'?'Broad market (MARKET)':x).join(', ') || 'Provider symbols unavailable');set('headline',item.headline);
    const date=new Date(item.timestamp),valid=Number.isFinite(date.getTime());
    const time=set('time',valid?`${date.toISOString().replace('T',' ').replace(/\.\d{3}Z$/,'')} UTC`:'Timestamp unavailable');
    if(valid)time.dateTime=date.toISOString();else time.removeAttribute('datetime');
    set('summary',item.summary).hidden=!item.summary;set('why',item.why).hidden=!item.why;
    set('source',[item.provider,item.source,item.classification].filter(Boolean).join(' · '));
    const topics=article.querySelector('[data-news-topics]');
    if(topics){
      topics.hidden=!item.categories.length&&!item.sectors.length;topics.title=item.categoryRationale;topics.dataset.newsCategoryVersion=item.categoryVersion;
      const badges=(key,keys,labels,prefix='')=>{const target=article.querySelector(`[data-news-${key}]`);if(!target)return;target.replaceChildren(...keys.map(value=>{const badge=document.createElement('span');badge.className='news-topic-badge';badge.textContent=prefix+labels[value];return badge;}));};
      badges('categories',item.categories,categoryLabels);badges('sectors',item.sectors,sectorLabels,'Sector: ');
    }
    set('sentiment',item.score===null?'Sentiment unavailable':`${positive?'Positive':negative?'Negative':'Neutral'} ${item.score>0?'+':''}${item.score.toFixed(2)}`);
    const link=article.querySelector('[data-news-link]');link.hidden=!item.url;if(item.url)link.href=item.url;else link.removeAttribute('href');
    const group=article.querySelector('[data-group-action]');if(group){const equities=item.tickers.filter(x=>x!=='MARKET'&&/^[A-Z0-9.-]{1,16}$/.test(x));group.dataset.tickers=equities.join(', ');group.hidden=!equities.length;}
    return article;
  }
  function create(raw,{allowGroup=Boolean(document.getElementById('groupActionDialog'))}={}) {
    const article=document.createElement('article');article.dataset.newsItem='';
    const node=(tag,key,className)=>{const n=document.createElement(tag);n.dataset[`news${key}`]='';if(className)n.className=className;return n;};
    const meta=document.createElement('div');meta.className='news-timeline-meta';const providerLabel=document.createElement('span');providerLabel.className='muted';providerLabel.textContent='Provider symbols:';meta.append(providerLabel,node('strong','Tickers'),node('time','Time'),node('span','Sentiment'));
    const topics=node('div','Topics','news-topic-labels'),topicLabel=document.createElement('small');topicLabel.textContent='Inferred display labels';topics.append(topicLabel,node('span','Categories'),node('span','Sectors'));
    const footer=document.createElement('div');footer.className='news-entry-footer';footer.append(node('span','Source','muted'));
    if(allowGroup){const button=document.createElement('button');button.type='button';button.className='button secondary';button.dataset.groupAction='add';button.textContent='Add symbols to group';footer.append(button);}
    const link=node('a','Link');link.target='_blank';link.rel='noopener noreferrer';link.textContent='Open article';footer.append(link);
    article.append(meta,node('strong','Headline','news-headline'),node('p','Summary'),node('p','Why','news-why'),topics,footer);
    return patch(article,raw);
  }
  return {create,patch,symbols,normalize};
})();
