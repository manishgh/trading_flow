(() => {
  const root = document.querySelector('[data-desk-root]'), form = document.getElementById('DeskFilterForm');
  if (!root || !form) return;
  const key = `tradingflow.desk.${root.dataset.operatorId}.${root.dataset.environment}.filters`;
  const fields = ['search','minPrice','maxPrice','maxSpreadBps','minChange','maxChange','minVolumeZ20','maxVolumeZ20','tracked','sector','minMarketCap','maxMarketCap','minVolume','minRvol','maxRvol','minAtrPercent','maxAtrPercent','minGap','maxGap','minVwapDistance','maxVwapDistance','minRsi','maxRsi','maxEarningsDays','catalystOnly','signalState','source','sort','dir','pageSize'];
  const pick = document.getElementById('DeskPreset'), status = document.getElementById('DeskPresetStatus');
  function read() { try { const value = JSON.parse(localStorage.getItem(key)); return Array.isArray(value) ? value.filter(x => typeof x.name === 'string' && x.name.length <= 40 && x.values && typeof x.values === 'object').slice(0,20) : []; } catch { return []; } }
  let presets = read();
  function render() {
    const blank = document.createElement('option'); blank.value = ''; blank.textContent = 'Choose preset';
    pick.replaceChildren(blank, ...presets.map((p,i) => { const option = document.createElement('option'); option.value = i; option.textContent = p.name; return option; }));
  }
  function save() { try { localStorage.setItem(key, JSON.stringify(presets)); render(); return true; } catch { status.textContent = 'Browser storage is unavailable; filters still work through the URL.'; return false; } }
  document.getElementById('DeskPresetSave').addEventListener('click', () => {
    const name = document.getElementById('DeskPresetName').value.trim();
    if (!name) { status.textContent = 'Enter a preset name.'; return; }
    const values = Object.fromEntries(fields.map(name => [name, form.elements.namedItem(name)?.value || '']));
    presets = presets.filter(p => p.name !== name); presets.unshift({name,values}); presets = presets.slice(0,20);
    if (save()) status.textContent = 'Filters saved for this operator and environment in this browser.';
  });
  document.getElementById('DeskPresetDelete').addEventListener('click', () => {
    if (pick.value === '') return;
    presets.splice(Number(pick.value),1); if(save()) status.textContent = 'Preset deleted.';
  });
  pick.addEventListener('change', () => {
    const preset = presets[Number(pick.value)]; if (pick.value === '' || !preset) return;
    const url = new URL(location.href);
    fields.forEach(name => { const value = preset.values[name]; if(typeof value === 'string' && value.length <= 200 && value) url.searchParams.set(name,value); else url.searchParams.delete(name); });
    url.searchParams.delete('pageNumber'); location.assign(url.pathname + url.search);
  });
  render();
})();
