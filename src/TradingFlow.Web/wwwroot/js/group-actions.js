const dialog = document.getElementById('groupActionDialog');
const element = name => document.getElementById(`groupAction${name}`);
let selection, retained, receipt, creation, creationReceipt, inFlight = false;
const storageKey = `tf-group-pending-${dialog?.dataset.operatorId}`;
const savePending = () => { try { if (retained || creation) sessionStorage.setItem(storageKey, JSON.stringify({ selection, retained, creation })); else sessionStorage.removeItem(storageKey); } catch { /* Same-page retry still retains identity. */ } };
const saved = (() => { try { return JSON.parse(sessionStorage.getItem(storageKey)); } catch { return null; } })();
if (dialog && (saved?.retained || saved?.creation) && saved?.selection) { selection = saved.selection; retained = saved.retained; creation = saved.creation; element('Resume').hidden = false; }
function visible(name, show) { element(name).hidden = !show; element(`${name}Label`).hidden = !show; }
function lock(locked) { for (const name of ['Source','Target','Tag','CreateName','Create']) element(name).disabled = locked; }
async function loadGroups() {
  const response = await window.tradingFlowFetch('/api/v1/wishlists/mutable', { cache: 'no-store' });
  if (!response.ok) throw new Error(`Group read failed (${response.status}).`);
  const groups = await response.json();
  for (const name of ['Source','Target']) {
    const blank = document.createElement('option'); blank.value = ''; blank.textContent = 'Choose a group';
    element(name).replaceChildren(blank, ...groups.map(group => {
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
    lock(true); element('Summary').textContent = `Pending ${retained.operation}: ${retained.tickers.join(', ')}. Source group ID: ${retained.sourceId || 'none'}. Destination group ID: ${retained.targetId || 'none'}. Tag: ${retained.tag || 'none'}. Command ID: ${retained.commandId}.`;
    for (const name of ['Source','Target','Tag']) { element(name).hidden = true; element(`${name}Label`).hidden = true; }
    element('Status').textContent = 'The previous outcome is unknown. Retry that exact change before starting another.';
    element('Apply').disabled = false; element('Apply').textContent = 'Retry the same change';
    if (!dialog.open) dialog.showModal();
    return;
  }
  if (creation) {
    lock(true); element('Apply').disabled = true; element('CreatePanel').hidden = false;
    element('CreateName').value = creation.name; element('Create').disabled = false;
    element('Create').textContent = 'Retry exact empty group creation';
    element('Summary').textContent = `Pending empty group creation: ${creation.name}. Command ID: ${creation.commandId}. No membership is applied.`;
    element('Status').textContent = 'The creation outcome is unknown. Retry the same identity before reviewing membership.';
    for (const name of ['Source','Target','Tag']) visible(name, false);
    if (!dialog.open) dialog.showModal();
    return;
  }
  selection = selectionData; retained = null; receipt = null;
  element('Undo').hidden = true; element('Apply').disabled = true; element('Apply').textContent = 'Apply reviewed change';
  element('Status').textContent = 'Loading groups…'; lock(false);
  element('CreatePanel').hidden = !['add','copy','move'].includes(selection.operation);
  element('CreateName').value = ''; element('Create').textContent = 'Create empty group';
  element('CreateUndo').hidden = !creationReceipt;
  element('Summary').textContent = `${selection.operation}: ${selection.tickers.join(', ')}. This changes discovery groups only.`;
  visible('Target', ['add','copy','move'].includes(selection.operation));
  visible('Source', selection.operation !== 'add'); visible('Tag', ['tag','untag'].includes(selection.operation));
  element('Tag').value = '';
  inFlight = true; dialog.showModal();
  try {
    const groups = await loadGroups();
    element('Status').textContent = groups.length ? 'Review the symbols and group before applying.' : 'Create an empty destination group, then review membership.';
    element('Apply').disabled = !groups.length;
  } catch (error) { element('Status').textContent = error.message; element('Apply').disabled = true; }
  finally { inFlight = false; }
}
element('Apply')?.addEventListener('click', async () => {
  if (inFlight) return;
  if (!retained) retained = { commandId: crypto.randomUUID(), operation: selection.operation,
    sourceId: selection.operation === 'add' ? null : element('Source').value || null,
    targetId: ['add','copy','move'].includes(selection.operation) ? element('Target').value || null : null,
    tickers: selection.tickers, tag: element('Tag').value || null };
  savePending(); inFlight = true;
  lock(true); element('Apply').disabled = true; element('Close').disabled = true;
  try {
    const response = await window.tradingFlowFetch('/api/v1/wishlists/bulk', { method: 'POST',
      headers: { 'Content-Type':'application/json' }, body: JSON.stringify(retained) });
    if (!response.ok) {
      const detail = await response.json().catch(() => ({}));
      if (response.status < 500) { retained = null; savePending(); lock(false); }
      throw new Error(detail.error || `Group change failed (${response.status}).`);
    }
    receipt = await response.json();
    retained = null; savePending(); element('Resume').hidden = true;
    element('Status').textContent = receipt.undone ? 'This command was already undone.' : `${receipt.affected} membership changes saved. Reload the list to see them.`;
    element('Undo').hidden = receipt.undone; element('Apply').disabled = true;
  } catch (error) {
    element('Status').textContent = error.message;
    element('Apply').disabled = false; element('Apply').textContent = retained ? 'Retry the same change' : 'Apply reviewed change';
  } finally { inFlight = false; element('Close').disabled = false; element('Resume').hidden = !retained; }
});
element('Create')?.addEventListener('click', async () => {
  if (inFlight || retained) return;
  let acknowledgedCreation;
  if (!creation) {
    const name = element('CreateName').value.trim();
    if (!name || name.length > 120) { element('Status').textContent = 'Enter a group name of 1–120 characters.'; return; }
    creation = { commandId:crypto.randomUUID(), name };
  }
  savePending(); inFlight = true; lock(true); element('Apply').disabled = true; element('Close').disabled = true;
  try {
    const response = await window.tradingFlowFetch('/api/v1/wishlists/create', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(creation) });
    if (!response.ok) {
      const detail = await response.json().catch(() => ({}));
      if (response.status < 500) { creation = null; savePending(); }
      throw new Error(detail.error || `Group creation failed (${response.status}).`);
    }
    const created = await response.json(); creation = null; savePending(); lock(false);
    if (created.receipt.undone) { element('Status').textContent = 'This creation was already undone. Start a new creation.'; return; }
    creationReceipt = acknowledgedCreation = created;
    element('CreateUndo').hidden = false;
    await loadGroups();
    for (const name of ['Source','Target']) {
      if (![...element(name).options].some(option => option.value === created.id)) {
        const option = document.createElement('option'); option.value = created.id; option.textContent = created.name; element(name).append(option);
      }
    }
    visible('Target', true); visible('Source', selection.operation !== 'add'); visible('Tag', false);
    element('Target').value = created.id; element('CreateUndo').hidden = false;
    element('Apply').disabled = false; element('Create').textContent = 'Create empty group';
    element('Status').textContent = `Created empty group ${created.name}. No symbols added. Review and Apply membership separately.`;
  } catch (error) {
    element('Status').textContent = acknowledgedCreation ? `Empty group ${acknowledgedCreation.name} was created; the group list could not be refreshed. No membership was applied. ${error.message}` : error.message;
    lock(Boolean(creation)); element('Create').disabled = false;
    element('Create').textContent = creation ? 'Retry exact empty group creation' : 'Create empty group';
  } finally { inFlight = false; element('Close').disabled = false; element('Resume').hidden = !retained && !creation; }
});
element('CreateUndo')?.addEventListener('click', async () => {
  if (inFlight || !creationReceipt || retained || creation) return;
  inFlight = true; element('Close').disabled = true;
  try {
    const response = await window.tradingFlowFetch(`/api/v1/wishlists/bulk/${creationReceipt.receipt.commandId}/undo`, {method:'POST'});
    if (!response.ok) { const detail = await response.json(); throw new Error(detail.error || `Creation undo failed (${response.status}).`); }
    for (const name of ['Source','Target']) for (const option of [...element(name).options]) if (option.value === creationReceipt.id) option.remove();
    creationReceipt = null; element('CreateUndo').hidden = true;
    element('Status').textContent = 'Empty group creation undone. No existing memberships changed.';
  } catch (error) { element('Status').textContent = error.message; }
  finally { inFlight = false; element('Close').disabled = false; }
});
element('Undo')?.addEventListener('click', async () => {
  if (inFlight) return;
  inFlight = true; element('Undo').disabled = true; element('Close').disabled = true;
  try {
    const response = await window.tradingFlowFetch(`/api/v1/wishlists/bulk/${receipt.commandId}/undo`, { method:'POST' });
    if (!response.ok) { const error = await response.json(); throw new Error(error.error || `Undo failed (${response.status}).`); }
    element('Status').textContent = 'Change undone. Reload the list to see the restored memberships.';
    element('Undo').hidden = true;
  } catch (error) { element('Status').textContent = error.message; }
  finally { inFlight = false; element('Undo').disabled = false; element('Close').disabled = false; }
});
dialog?.addEventListener('cancel', event => { if (inFlight) event.preventDefault(); });
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
