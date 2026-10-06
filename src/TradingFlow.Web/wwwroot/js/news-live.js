(() => {
  const form = document.querySelector('.news-filter-bar');
  const status = document.querySelector('#news-live-status');
  if (!form || !status) return;
  const filters = () => JSON.stringify([...new FormData(form)]);
  const original = filters();
  let busy = false;
  const reading = () => document.hidden || filters() !== original ||
    document.querySelector('#news-live-body')?.contains(document.activeElement) ||
    document.querySelector('#news-live-body details[open]') || document.querySelector('dialog[open]');
  async function refresh() {
    if (busy || reading()) return;
    busy = true;
    try {
      const response = await fetch(location.href, {credentials:'same-origin',cache:'no-store'});
      if (response.status === 401 || new URL(response.url).pathname === '/Login') {
        const link = document.createElement('a');
        link.href = '/Login?ReturnUrl=' + encodeURIComponent(location.pathname + location.search);
        link.textContent = 'Sign in again to continue live updates';
        status.replaceChildren(link);
        return;
      }
      if (!response.ok) throw Error('News refresh failed');
      const documentCopy = new DOMParser().parseFromString(await response.text(), 'text/html');
      const next = documentCopy.querySelector('#news-live-body');
      const current = document.querySelector('#news-live-body');
      if (!next || !current) throw Error('News response is incomplete');
      // A reader may start editing/opening a dialog while the request is in flight.
      if (reading()) return;
      current.replaceWith(document.importNode(next, true));
      status.textContent = 'Updated ' + new Date().toLocaleTimeString() + ' · refreshes every 30 seconds';
    } catch {
      status.textContent = 'Live update failed · showing the last loaded news; retrying in 30 seconds';
    } finally { busy = false; }
  }
  setInterval(refresh, 30000);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) refresh(); });
})();
