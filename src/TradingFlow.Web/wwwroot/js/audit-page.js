import { renderAuditRecords, renderAuditReasons, renderAuditOutcomes, renderAuditComparison, renderAuditWorkflow } from './audit-renderer.js';

const form = document.getElementById('auditFilterForm');
let controller, revision = 0;
const set = (id, value) => { const element = document.getElementById(id); if (element) element.textContent = value; };

function updateExports() {
  for (const link of document.querySelectorAll('[data-audit-export]')) {
    const target = new URL(location.href); target.searchParams.set('handler', 'Export');
    target.searchParams.set('format', link.dataset.auditExport); link.href = target;
  }
}

async function refresh() {
  const current = ++revision;
  controller?.abort(); controller = new AbortController();
  const url = new URL(location.href); url.searchParams.set('handler', 'Snapshot');
  try {
    const response = await window.tradingFlowFetch(url, { cache: 'no-store', signal: controller.signal });
    if (!response.ok) throw new Error(`Audit read failed (${response.status}).`);
    const data = await response.json();
    if (current !== revision || !data.success) return;
    set('auditRefreshedAt', data.refreshedAt); set('auditEvaluations', data.evaluations);
    set('auditAccepted', data.accepted); set('auditRejected', data.rejected);
    set('auditCoverage', `${data.tickerCount} tickers / ${data.strategyCount} strategies`);
    set('auditPageStatus', `Page ${data.pageNumber} of ${data.pageCount} · ${data.totalMatching} matching decisions`);
    set('auditReadStatus', '');
    form.elements.sort.value = data.sort;
    form.elements.descending.value = String(data.descending);
    for (const heading of document.querySelectorAll('[data-audit-sort]')) {
      const key = heading.dataset.auditSort;
      heading.setAttribute('aria-sort', key === data.sort ? data.descending ? 'descending' : 'ascending' : 'none');
      const target = new URL(location.href);
      target.searchParams.set('sort', key);
      target.searchParams.set('descending', String(key === data.sort ? !data.descending : false));
      target.searchParams.set('pageNumber', '1');
      heading.querySelector('a').href = target;
    }
    document.getElementById('auditEmpty').hidden = data.records.length !== 0;
    renderAuditRecords(document.getElementById('auditRecordsBody'), data.records);
    renderAuditReasons(document.getElementById('auditReasonCards'), data.reasons);
    renderAuditOutcomes(document.getElementById('auditFunnel'), data.funnel);
    renderAuditComparison(document.getElementById('auditComparisonBody'), data.comparison);
    if (data.workflow) {
      set('auditWorkflowDetail', data.workflow.detail);
      renderAuditWorkflow(document.getElementById('auditWorkflowCounts'), document.getElementById('auditWorkflowGates'), data.workflow, data.evaluations);
    }
    document.getElementById('auditOutcomePanel').hidden = data.funnel.length === 0;
    document.getElementById('auditComparison').hidden = data.comparison.length === 0;
    set('auditComparisonName', data.comparisonRunName);
    document.getElementById('auditComparisonEmpty').hidden = data.comparison.length > 0 || !new URL(location.href).searchParams.get('compareWith');
    document.getElementById('auditReasonPanel').hidden = data.reasons.length === 0;
    const options = data.reasons.map(reason => {
      const option = document.createElement('option'); option.value = reason.reasonKey; option.label = reason.displayReason; return option;
    });
    document.getElementById('auditReasonOptions').replaceChildren(...options);
    for (const [id, number, disabled] of [['auditPrevious', data.pageNumber - 1, data.pageNumber <= 1], ['auditNext', data.pageNumber + 1, data.pageNumber >= data.pageCount]]) {
      const link = document.getElementById(id), target = new URL(location.href);
      target.searchParams.set('pageNumber', String(Math.max(1, number)));
      link.href = target; link.setAttribute('aria-disabled', String(disabled));
    }
    updateExports();
  } catch (error) {
    if (error.name !== 'AbortError' && current === revision) set('auditReadStatus', error.message);
  }
}

function apply() {
  const target = new URL(location.href);
  for (const [key, value] of new FormData(form)) {
    if (value) target.searchParams.set(key, value); else target.searchParams.delete(key);
  }
  target.searchParams.set('pageNumber', '1');
  history.replaceState(null, '', target); updateExports(); refresh();
}
form?.addEventListener('submit', event => { event.preventDefault(); apply(); });
form?.addEventListener('change', apply);
let debounce;
form?.addEventListener('input', () => { clearTimeout(debounce); debounce = setTimeout(apply, 300); });
document.getElementById('clearAuditFilters')?.addEventListener('click', () => {
  for (const input of form.querySelectorAll('input,select')) if (['decision','reason','ticker'].includes(input.name)) input.value = '';
  apply();
});
document.getElementById('auditReasonCards')?.addEventListener('click', event => {
  const reason = event.target.closest('[data-reason-card]');
  if (reason) { document.getElementById('reasonFilter').value = reason.dataset.reasonCard; apply(); }
});
document.addEventListener('click', event => {
  const link = event.target.closest('a[data-audit-nav]');
  if (!link || event.ctrlKey || event.metaKey || event.shiftKey || event.button !== 0) return;
  event.preventDefault(); if (link.getAttribute('aria-disabled') === 'true') return;
  history.replaceState(null, '', link.href); updateExports(); refresh();
});
function startRefresh() {
  clearInterval(window.tradingFlowAuditRefresh);
  window.tradingFlowAuditRefresh = setInterval(refresh, 5000);
  refresh();
}
window.addEventListener('pageshow', event => { if (event.persisted) startRefresh(); });
window.addEventListener('pagehide', () => { clearInterval(window.tradingFlowAuditRefresh); clearTimeout(debounce); controller?.abort(); });
startRefresh();
