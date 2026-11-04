const dialog = document.getElementById('groupActionDialog');
const element = name => document.getElementById(`groupAction${name}`);
let selection, retained, receipt, creation, creationReceipt, inFlight = false, changed = false;
const storageKey = `tf-group-pending-${dialog?.dataset.operatorId}`;
const savePending = () => { try { if (retained || creation) sessionStorage.setItem(storageKey, JSON.stringify({ selection, retained, creation })); else sessionStorage.removeItem(storageKey); } catch { /* Same-page retry still retains identity. */ } };
const saved = (() => { try { return JSON.parse(sessionStorage.getItem(storageKey)); } catch { return null; } })();
if (dialog && (saved?.retained || saved?.creation) && saved?.selection) { selection = saved.selection; retained = saved.retained; creation = saved.creation; element('Resume').hidden = false; }
const needsTarget = operation => ['add','copy','move'].includes(operation);
// The page that opened the dialog already names the source wishlist; only ask for one when it does not.
const needsSourcePicker = () => selection.operation !== 'add' && !selection.sourceId;
const verbs = { add: 'Add', copy: 'Copy', move: 'Move', remove: 'Remove' };
function visible(name, show) { element(name).hidden = !show; element(`${name}Label`).hidden = !show; }
function lock(locked) { for (const name of ['Source','Target','CreateName','Create']) element(name).disabled = locked; }
// Apply stays unavailable until every wishlist the change needs has been chosen.
function syncApply() {
  if (retained || receipt || creation) return;
  const source = selection.sourceId || element('Source').value;
  element('Apply').disabled = (needsTarget(selection.operation) && !element('Target').value) || (selection.operation !== 'add' && !source)
    || (selection.operation === 'move' && element('Target').value === source);
}
async function loadGroups() {
  const response = await window.tradingFlowFetch('/api/v1/wishlists/mutable', { cache: 'no-store' });
  if (!response.ok) throw new Error(`Could not read the wishlists (${response.status}).`);
  const groups = await response.json();
  for (const name of ['Source','Target']) {
    const blank = document.createElement('option'); blank.value = ''; blank.textContent = 'Choose a wishlist';
    const choices = groups.filter(group => name === 'Source' || group.id !== selection.sourceId);
    element(name).replaceChildren(blank, ...choices.map(group => {
      const option = document.createElement('option'); option.value = group.id; option.textContent = group.name; return option;
    }));
  }
  if (selection.sourceId) element('Source').value = selection.sourceId;
  return groups;
}
async function open(selectionData) {
  if (inFlight) return;
  if (retained && !receipt) {
    element('CreatePanel').hidden = true;
    lock(true); element('Summary').textContent = `Unfinished change: ${verbs[retained.operation] || retained.operation} ${retained.tickers.join(', ')}. Command ${retained.commandId}.`;
    for (const name of ['Source','Target']) visible(name, false);
    element('Status').textContent = 'The last attempt did not confirm whether it was saved. Retry that exact change before starting another; a retry never applies it twice.';
    element('Apply').disabled = false; element('Apply').textContent = 'Retry the same change';
    if (!dialog.open) dialog.showModal();
    return;
  }
  if (creation) {
    lock(true); element('Apply').disabled = true; element('CreatePanel').hidden = false; element('CreatePanel').open = true;
    element('CreateName').value = creation.name; element('Create').disabled = false;
    element('Create').textContent = 'Retry creating this wishlist';
    element('Summary').textContent = `Unfinished: creating the empty wishlist ${creation.name}. No stocks are added by this step.`;
    element('Status').textContent = 'The last attempt did not confirm whether the wishlist was created. Retry it; a retry never creates two.';
    for (const name of ['Source','Target']) visible(name, false);
    if (!dialog.open) dialog.showModal();
    return;
  }
  selection = selectionData; retained = null; receipt = null;
  element('Undo').hidden = true; element('Apply').disabled = true; element('Apply').textContent = verbs[selection.operation] || 'Apply';
  element('Status').textContent = 'Loading wishlists…'; lock(false);
  element('CreatePanel').hidden = !needsTarget(selection.operation); element('CreatePanel').open = false;
  element('CreateName').value = ''; element('Create').textContent = 'Create wishlist';
  element('CreateUndo').hidden = !creationReceipt;
  const where = selection.operation === 'remove' ? 'from this wishlist' : needsTarget(selection.operation) ? 'to the wishlist you choose' : '';
  element('Summary').textContent = `${verbs[selection.operation] || selection.operation} ${selection.tickers.join(', ')} ${where}.`;
  visible('Target', needsTarget(selection.operation));
  visible('Source', needsSourcePicker());
  inFlight = true; dialog.showModal();
  try {
    const groups = await loadGroups();
    const destinations = groups.filter(group => group.id !== selection.sourceId);
    const none = needsTarget(selection.operation) && destinations.length === 0;
    element('Status').textContent = none ? 'There is no other wishlist yet. Name one under New wishlist and create it.' : '';
    if (none) element('CreatePanel').open = true;
    syncApply();
  } catch (error) { element('Status').textContent = error.message; element('Apply').disabled = true; }
  finally { inFlight = false; }
}
element('Target')?.addEventListener('change', syncApply);
element('Source')?.addEventListener('change', syncApply);
element('Apply')?.addEventListener('click', async () => {
  if (inFlight) return;
  if (!retained) retained = { commandId: crypto.randomUUID(), operation: selection.operation,
    sourceId: selection.operation === 'add' ? null : selection.sourceId || element('Source').value || null,
    targetId: needsTarget(selection.operation) ? element('Target').value || null : null,
    tickers: selection.tickers, tag: null };
  savePending(); inFlight = true;
  lock(true); element('Apply').disabled = true; element('Close').disabled = true;
  try {
    const response = await window.tradingFlowFetch('/api/v1/wishlists/bulk', { method: 'POST',
      headers: { 'Content-Type':'application/json' }, body: JSON.stringify(retained) });
    if (!response.ok) {
      const detail = await response.json().catch(() => ({}));
      if (response.status < 500) { retained = null; savePending(); lock(false); }
      throw new Error(detail.error || `The change was not saved (${response.status}).`);
    }
    receipt = await response.json();
    retained = null; savePending(); element('Resume').hidden = true; changed = true;
    element('Status').textContent = receipt.undone ? 'This change was already undone.' : `Saved: ${receipt.affected} stock${receipt.affected === 1 ? '' : 's'} changed.`;
    element('Undo').hidden = receipt.undone; element('Apply').disabled = true;
  } catch (error) {
    element('Status').textContent = error.message;
    element('Apply').disabled = false; element('Apply').textContent = retained ? 'Retry the same change' : verbs[selection.operation] || 'Apply';
  } finally { inFlight = false; element('Close').disabled = false; element('Resume').hidden = !retained; }
});
element('Create')?.addEventListener('click', async () => {
  if (inFlight || retained) return;
  let acknowledgedCreation;
  if (!creation) {
    const name = element('CreateName').value.trim();
    if (!name || name.length > 120) { element('Status').textContent = 'Enter a wishlist name of 1–120 characters.'; return; }
    creation = { commandId:crypto.randomUUID(), name };
  }
  savePending(); inFlight = true; lock(true); element('Apply').disabled = true; element('Close').disabled = true;
  try {
    const response = await window.tradingFlowFetch('/api/v1/wishlists/create', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(creation) });
    if (!response.ok) {
      const detail = await response.json().catch(() => ({}));
      if (response.status < 500) { creation = null; savePending(); }
      throw new Error(detail.error || `The wishlist was not created (${response.status}).`);
    }
    const created = await response.json(); creation = null; savePending(); lock(false); changed = true;
    if (created.receipt.undone) { element('Status').textContent = 'This creation was already undone. Start a new one.'; return; }
    creationReceipt = acknowledgedCreation = created;
    element('CreateUndo').hidden = false;
    await loadGroups();
    if (![...element('Target').options].some(option => option.value === created.id)) {
      const option = document.createElement('option'); option.value = created.id; option.textContent = created.name; element('Target').append(option);
    }
    visible('Target', true); visible('Source', needsSourcePicker());
    element('Target').value = created.id; element('CreateUndo').hidden = false;
    element('Create').textContent = 'Create wishlist';
    element('Status').textContent = `Created ${created.name} and chose it. No stocks added yet: ${verbs[selection.operation] || 'Apply'} adds them.`;
    syncApply();
  } catch (error) {
    element('Status').textContent = acknowledgedCreation ? `${acknowledgedCreation.name} was created, but the list of wishlists could not be refreshed. No stocks were added. ${error.message}` : error.message;
    lock(Boolean(creation)); element('Create').disabled = false;
    element('Create').textContent = creation ? 'Retry creating this wishlist' : 'Create wishlist';
  } finally { inFlight = false; element('Close').disabled = false; element('Resume').hidden = !retained && !creation; }
});
element('CreateUndo')?.addEventListener('click', async () => {
  if (inFlight || !creationReceipt || retained || creation) return;
  inFlight = true; element('Close').disabled = true;
  try {
    const response = await window.tradingFlowFetch(`/api/v1/wishlists/bulk/${creationReceipt.receipt.commandId}/undo`, {method:'POST'});
    if (!response.ok) { const detail = await response.json(); throw new Error(detail.error || `Could not undo the creation (${response.status}).`); }
    for (const name of ['Source','Target']) for (const option of [...element(name).options]) if (option.value === creationReceipt.id) option.remove();
    creationReceipt = null; element('CreateUndo').hidden = true;
    element('Status').textContent = 'The new empty wishlist was removed again. No other wishlist changed.';
    syncApply();
  } catch (error) { element('Status').textContent = error.message; }
  finally { inFlight = false; element('Close').disabled = false; }
});
element('Undo')?.addEventListener('click', async () => {
  if (inFlight) return;
  inFlight = true; element('Undo').disabled = true; element('Close').disabled = true;
  try {
    const response = await window.tradingFlowFetch(`/api/v1/wishlists/bulk/${receipt.commandId}/undo`, { method:'POST' });
    if (!response.ok) { const error = await response.json(); throw new Error(error.error || `Undo failed (${response.status}).`); }
    element('Status').textContent = 'Change undone.';
    element('Undo').hidden = true;
  } catch (error) { element('Status').textContent = error.message; }
  finally { inFlight = false; element('Undo').disabled = false; element('Close').disabled = false; }
});
dialog?.addEventListener('cancel', event => { if (inFlight) event.preventDefault(); });
// A page that lists wishlist membership shows the saved change as soon as the dialog closes.
dialog?.addEventListener('close', () => { if (changed && !retained && !creation && location.pathname.toLowerCase() === '/wishlists') location.reload(); });
element('Resume')?.addEventListener('click', () => open(selection));
document.addEventListener('click', event => {
  const trigger = event.target.closest('[data-group-action]');
  if (!trigger || !dialog) return;
  const bulk = trigger.closest('[data-bulk-form]');
  const tickers = bulk ? [...bulk.querySelectorAll('[data-bulk-item]:checked')].map(input => input.value)
    : (trigger.dataset.tickers || '').split(',').map(value => value.trim()).filter(Boolean);
  if (!tickers.length) return;
  open({ operation: trigger.dataset.groupAction, tickers, sourceId: trigger.dataset.sourceId || null });
});

// Selection is confined to the rendered page and is not retained across navigation.
for (const bulk of document.querySelectorAll('[data-bulk-form]')) {
  if (!bulk.closest('[data-desk-root]')) continue;
  const items = () => [...bulk.querySelectorAll('[data-bulk-item]')];
  const toggle = bulk.querySelector('[data-bulk-toggle-all]');
  function sync() {
    const count = items().filter(x => x.checked).length;
    bulk.querySelector('[data-bulk-count]').textContent = count;
    bulk.querySelector('[data-bulk-bar]').hidden = count === 0;
    if (toggle) { toggle.checked = count > 0 && count === items().length; toggle.indeterminate = count > 0 && count < items().length; }
  }
  bulk.addEventListener('change', event => {
    if (event.target === toggle) items().forEach(x => { x.checked = toggle.checked; });
    sync();
  });
  bulk.querySelector('[data-bulk-clear]')?.addEventListener('click', () => { items().forEach(x => { x.checked = false; }); sync(); });
  sync();
}
