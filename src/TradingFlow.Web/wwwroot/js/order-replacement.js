(() => {
  const dialog = document.getElementById('ReplacementDialog'); if(!dialog) return;
  const el = name => document.getElementById(`Replacement${name}`);
  const key = `tf-replacement-${dialog.dataset.operatorId}-paper`;
  let pending, busy = false;
  try { pending = JSON.parse(sessionStorage.getItem(key)); } catch { /* Same-page retry remains available. */ }
  let issued = Boolean(pending), canRelease = false;
  function save() { try { if(pending && issued) sessionStorage.setItem(key,JSON.stringify(pending)); else sessionStorage.removeItem(key); } catch { /* Preserve in memory. */ } el('Resume').hidden = !pending || !issued; }
  function open(id) {
    if(busy) return;
    if(!pending || !issued) pending = {clientOrderId:id,idempotencyKey:crypto.randomUUID()};
    el('Identity').textContent = `Original client order: ${pending.clientOrderId}. Cancellation command: ${pending.idempotencyKey}.`;
    el('Status').textContent = 'Review the original order before requesting cancellation.';
    el('Review').hidden = el('Release').hidden = true; canRelease = false; el('Cancel').disabled = false; save(); dialog.showModal();
  }
  async function execute(cancel) {
    if(busy || !pending) return; busy = true;
    if(cancel) { issued = true; save(); }
    canRelease = false; el('Release').hidden = true;
    el('Cancel').disabled = el('Refresh').disabled = el('Close').disabled = true; el('Review').hidden = true;
    try {
      const url = cancel ? '/api/v1/orders/replacement/cancel' : `/api/v1/orders/replacement/review?clientOrderId=${encodeURIComponent(pending.clientOrderId)}`;
      const options = cancel ? {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(pending)} : {cache:'no-store'};
      const response = await window.tradingFlowFetch(url,options);
      const result = await response.json();
      if(!response.ok) throw new Error(result.message || result.error || `Replacement review failed (${response.status}).`);
      el('Status').textContent = `${result.state}: ${result.detail}`;
      canRelease = result.canRelease === true; el('Release').hidden = !canRelease;
      el('Cancel').textContent = 'Retry the same cancellation';
      if(result.canReview && result.reviewUrl) {
        const target = new URL(result.reviewUrl,location.origin);
        if(target.origin !== location.origin || !['/Desk','/OrderTicket'].includes(target.pathname)) throw new Error('Review link is unavailable.');
        el('Review').href = target.pathname + target.search; el('Review').hidden = false; el('Cancel').disabled = true;
      }
    } catch(error) { el('Status').textContent = error.message; el('Cancel').textContent = 'Retry the same cancellation'; }
    finally { busy = false; el('Refresh').disabled = el('Close').disabled = false; if(el('Review').hidden) el('Cancel').disabled = false; }
  }
  el('Cancel').addEventListener('click', () => execute(true)); el('Refresh').addEventListener('click', () => execute(false));
  el('Close').addEventListener('click', () => { if(!busy) { if(!issued) pending = null; save(); dialog.close(); } });
  el('Resume').addEventListener('click', () => open(pending?.clientOrderId));
  el('Review').addEventListener('click', () => { pending = null; issued = false; save(); });
  el('Release').addEventListener('click', () => { if(!busy && canRelease) { pending = null; issued = false; save(); dialog.close(); } });
  dialog.addEventListener('cancel',event => { if(busy) event.preventDefault(); else if(!issued) { pending = null; save(); } });
  document.addEventListener('click',event => { const button = event.target.closest('[data-replacement-order]'); if(button) open(button.dataset.replacementOrder); });
  save();
})();
