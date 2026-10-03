(() => {
 'use strict';
 document.querySelectorAll('[data-query-select]').forEach(select=>select.addEventListener('change',()=>{
   const url=new URL(location.href);url.searchParams.set(select.dataset.querySelect,select.value);location.assign(url.pathname+url.search);
 }));
 const views=document.querySelectorAll('[data-desk-view]');
 function show(view){document.querySelectorAll('[data-desk-panel]').forEach(x=>x.hidden=x.dataset.deskPanel!==view);views.forEach(x=>x.setAttribute('aria-pressed',String(x.dataset.deskView===view)));}
 views.forEach(x=>x.addEventListener('click',()=>show(x.dataset.deskView)));
 if(views.length){const params=new URLSearchParams(location.search);show(params.has('runId')?'candidates':Array.from(params.keys()).some(x=>['ticker','id','source','search','strategyid','screenerquery'].includes(x.toLowerCase()))?'market':'candidates');}
})();
