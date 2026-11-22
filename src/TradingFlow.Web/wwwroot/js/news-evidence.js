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
    classification:raw.classification || raw.category || 'Classification unavailable',why:raw.whyItMatters || '',url:safeUrl(raw.url),linkIsArticle:raw.linkKind === 'article',
    categories:topicKeys(raw.categories, categoryLabels),sectors:topicKeys(raw.sectors, sectorLabels),
    categoryRationale:raw.categoryRationale || '',categoryVersion:raw.categoryVersion || '',
    score:raw.sentimentScore !== null && raw.sentimentScore !== undefined && Number.isFinite(Number(raw.sentimentScore)) ? Number(raw.sentimentScore) : null
  });
  // Builds the hover tooltip summarizing provider symbols, source classification, and inferred display labels.
  function buildRowTooltip(item, tickerText) {
    const inferred = [
      ...item.categories.map(key => categoryLabels[key]),
      ...item.sectors.map(key => 'Sector: ' + sectorLabels[key])
    ];
    const parts = [
      'Provider symbols: ' + tickerText,
      [item.provider, item.source, item.classification].filter(Boolean).join(' · ')
    ];
    if (inferred.length) parts.push('Inferred display labels: ' + inferred.join(', '));
    if (item.categoryRationale) parts.push(item.categoryRationale);
    return parts.filter(Boolean).join(' · ');
  }
  // Updates a compact 1-row news article in place while keeping full metadata accessible in tooltips and disclosure.
  function patch(article,raw) {
    const item=normalize(raw), positive=item.score !== null && item.score > .15, negative=item.score !== null && item.score < -.15;
    article.classList.add('news-entry','news-item','news-compact-row');article.classList.toggle('is-positive',positive);article.classList.toggle('is-negative',negative);
    article.dataset.newsTicker=item.tickers.join(', ');article.dataset.newsTimestamp=item.timestamp || '';article.dataset.newsUrl=item.url || '';
    article.dataset.newsKey=`${item.url || ''}|${item.headline}|${item.timestamp || ''}`;
    const set=(key,value)=>{const node=article.querySelector(`[data-news-${key}]`);if(node && node.textContent!==String(value))node.textContent=value;return node;};
    const tickerText = item.tickers.map(x=>x==='MARKET'?'Broad market (MARKET)':x).join(', ') || 'Provider symbols unavailable';
    const tickersNode = set('tickers', tickerText);
    if (tickersNode) tickersNode.title = 'Provider symbols: ' + tickerText;
    set('headline',item.headline);
    const date=new Date(item.timestamp),valid=Number.isFinite(date.getTime());
    const time=set('time',valid?`${date.toISOString().slice(0,16).replace('T',' ')} UTC`:'Timestamp unavailable');
    if(time){ if(valid)time.dateTime=date.toISOString();else time.removeAttribute('datetime'); }
    const summaryEl = set('summary',item.summary); if(summaryEl) summaryEl.hidden=!item.summary;
    const whyEl = set('why',item.why); if(whyEl) whyEl.hidden=!item.why;
    set('source',[item.provider,item.source,item.classification].filter(Boolean).join(' · '));
    const topics=article.querySelector('[data-news-topics]');
    if(topics){
      topics.hidden=!item.categories.length&&!item.sectors.length;topics.title=item.categoryRationale;topics.dataset.newsCategoryVersion=item.categoryVersion;
      const badges=(key,keys,labels,prefix='')=>{const target=article.querySelector(`[data-news-${key}]`);if(!target)return;target.replaceChildren(...keys.map(value=>{const badge=document.createElement('span');badge.className='news-topic-badge';badge.textContent=prefix+labels[value];return badge;}));};
      badges('categories',item.categories,categoryLabels);badges('sectors',item.sectors,sectorLabels,'Sector: ');
    }
    set('sentiment',item.score===null?'Sentiment unavailable':`${positive?'Positive':negative?'Negative':'Neutral'} ${item.score>0?'+':''}${item.score.toFixed(2)}`);
    const link=article.querySelector('[data-news-link]');const linkLabel=item.url?(item.linkIsArticle?'Source article: ':'Related page, may not be this story: ')+new URL(item.url).host.replace(/^www[.]/,''):'';
    if(link){ link.hidden=!item.url;if(item.url)link.href=item.url;else link.removeAttribute('href');link.classList.toggle('is-related',!item.linkIsArticle);link.textContent=item.linkIsArticle?'↗':'↗?';link.title=linkLabel;link.setAttribute('aria-label',linkLabel); }
    const linkNote=article.querySelector('[data-news-link-note]');if(linkNote){linkNote.hidden=!item.url;linkNote.textContent=linkLabel;}
    const group=article.querySelector('[data-group-action]');if(group){const equities=item.tickers.filter(x=>x!=='MARKET'&&/^[A-Z0-9.-]{1,16}$/.test(x));group.dataset.tickers=equities.join(', ');group.hidden=!equities.length;}
    const tooltip = buildRowTooltip(item, tickerText);
    article.title = tooltip;
    const summaryBtn = article.querySelector('.news-row-more > summary');
    if (summaryBtn) summaryBtn.title = tooltip;
    return article;
  }
  // Builds a compact 1-row news item (sentiment chip · ticker · headline · time) with expandable details and sr-only metadata.
  function create(raw,{allowGroup=Boolean(document.getElementById('groupActionDialog'))}={}) {
    const article=document.createElement('article');article.dataset.newsItem='';
    const node=(tag,key,className)=>{const n=document.createElement(tag);n.dataset[`news${key}`]='';if(className)n.className=className;return n;};
    const main=document.createElement('div');main.className='news-row-main';
    const providerLabel=document.createElement('span');providerLabel.className='sr-only';providerLabel.textContent='Provider symbols:';
    const link=node('a','Link','news-row-link');link.target='_blank';link.rel='noopener noreferrer';link.textContent='↗';
    main.append(node('span','Sentiment','news-sentiment-chip'),providerLabel,node('strong','Tickers','news-row-tickers'),node('strong','Headline','news-headline'),node('time','Time','news-row-time'),link);
    const details=document.createElement('details');details.className='news-row-more';
    const summary=document.createElement('summary');summary.textContent='Details';
    const body=document.createElement('div');body.className='news-row-more-body';
    const topics=node('div','Topics','news-topic-labels'),topicLabel=document.createElement('small');topicLabel.className='sr-only';topicLabel.textContent='Inferred display labels';topics.append(topicLabel,node('span','Categories'),node('span','Sectors'));
    const footer=document.createElement('div');footer.className='news-entry-footer';footer.append(node('span','Source','muted'));const linkNote=document.createElement('span');linkNote.className='muted';linkNote.dataset.newsLinkNote='';footer.append(linkNote);
    if(allowGroup){const button=document.createElement('button');button.type='button';button.className='button secondary btn-sm';button.dataset.groupAction='add';button.textContent='Add symbols to group';footer.append(button);}
    body.append(node('p','Summary'),node('p','Why','news-why'),topics,footer);
    details.append(summary,body);
    article.append(main,details);
    return patch(article,raw);
  }
  return {create,patch,symbols,normalize};
})();
