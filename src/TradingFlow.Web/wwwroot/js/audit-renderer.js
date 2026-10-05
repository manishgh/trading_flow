const node = (tag, text, className) => {
  const element = document.createElement(tag);
  if (text !== undefined) element.textContent = String(text ?? '');
  if (className) element.className = className;
  return element;
};

export function renderAuditRecords(body, records) {
  const focused = body.contains(document.activeElement) ? document.activeElement : null;
  const old = new Map([...body.children].map(row => [row.dataset.id, row]));
  const rows = records.map(record => {
    const key = String(record.id), signature = JSON.stringify(record);
    const existing = old.get(key);
    if (existing?.dataset.signature === signature) return existing;
    const row = node('tr', undefined, 'audit-table-row');
    Object.assign(row.dataset, { id: key, signature, decision: record.decision.toLowerCase(), reason: record.reasonKey, ticker: record.ticker });
    for (const text of [record.localTime, record.ticker, record.strategy, record.decision]) row.append(node('td', text, 'audit-cell'));
    const evidence = node('td', undefined, 'audit-cell');
    evidence.append(node('div', record.reason));
    if (record.rawReason) evidence.append(node('code', record.rawReason));
    if (record.gateEvidence?.length) {
      const table = node('table', undefined, 'gate-evidence');
      const heading = node('tr');
      for (const text of ['Gate', 'Measured', 'Required']) heading.append(node('th', text));
      table.append(heading);
      for (const gate of record.gateEvidence) {
        const line = node('tr');
        for (const text of [gate.label, gate.actual, `${gate.operator} ${gate.threshold}`]) line.append(node('td', text));
        table.append(line);
      }
      evidence.append(table);
    }
    evidence.append(node('div', record.explanation, 'muted audit-note'));
    const chips = node('div');
    for (const chip of record.chips ?? []) {
      const label = node('span', `${chip.label}: ${chip.value}`, `audit-chip${chip.passed ? '' : ' fail'}`);
      chips.append(label);
    }
    evidence.append(chips); row.append(evidence);
    const cell = node('td', undefined, 'audit-cell wide'), detail = node('details');
    detail.open = existing?.querySelector('details')?.open ?? false;
    detail.append(node('summary', 'View Signal Evaluation'), node('pre', record.signalJson, 'audit-json'));
    cell.append(detail); row.append(cell);
    if (existing?.contains(document.activeElement)) {
      const hadSummaryFocus = document.activeElement?.tagName === 'SUMMARY';
      queueMicrotask(() => { if (hadSummaryFocus) detail.querySelector('summary').focus(); });
    }
    return row;
  });
  // Keep unchanged nodes in place: live refresh must preserve selection, open
  // details and keyboard focus, even when another record was appended.
  for (let index = 0; index < rows.length; index++) {
    if (body.children[index] !== rows[index]) body.insertBefore(rows[index], body.children[index] ?? null);
  }
  while (body.children.length > rows.length) body.lastElementChild.remove();
  if (focused?.isConnected && document.activeElement !== focused) focused.focus({ preventScroll: true });
}

export function renderAuditReasons(container, reasons) {
  const focused = container.contains(document.activeElement) ? document.activeElement : null;
  const old = new Map([...container.children].map(button => [button.dataset.reasonCard, button]));
  const buttons = reasons.map(reason => {
    const signature = JSON.stringify(reason);
    const button = old.get(reason.reasonKey) ?? node('button', undefined, 'audit-reason-card');
    button.type = 'button'; button.dataset.reasonCard = reason.reasonKey;
    if (button.dataset.signature !== signature) {
      button.replaceChildren(node('div', reason.displayReason, 'audit-reason-title'),
        node('div', `${reason.count} rejected (${reason.percentOfRejected}%)`, 'muted'), node('code', reason.exampleReason));
      button.dataset.signature = signature;
    }
    return button;
  });
  for (let index = 0; index < buttons.length; index++) {
    if (container.children[index] !== buttons[index]) container.insertBefore(buttons[index], container.children[index] ?? null);
  }
  while (container.children.length > buttons.length) container.lastElementChild.remove();
  if (focused?.isConnected && document.activeElement !== focused) focused.focus({ preventScroll: true });
}

export function renderAuditOutcomes(container, stages) {
  if (!container) return;
  container.replaceChildren(...stages.map(stage => {
    const row = node('li'), head = node('div', undefined, 'audit-funnel-head');
    head.append(node('span', stage.label), node('strong', `${stage.remaining} / ${stage.total}`));
    const track = node('div', undefined, 'audit-funnel-track'), fill = node('progress', undefined, 'audit-funnel-fill tf-progress');
    fill.max = 100; fill.value = Math.max(0, Math.min(100, Number(stage.remainingPercent) || 0));
    fill.setAttribute('aria-label', stage.label); track.append(fill);
    row.append(head, track);
    if (stage.removed > 0) row.append(node('small', `${stage.removed} decisions in this group`, 'muted'));
    return row;
  }));
}

export function renderAuditComparison(container, rows) {
  if (!container) return;
  container.replaceChildren(...rows.map(metric => {
    const row = node('tr');
    row.append(node('td', metric.label), node('td', metric.current, 'num'), node('td', metric.other, 'num'),
      node('td', metric.deltaText, `num ${metric.deltaClass}`));
    return row;
  }));
}

export function renderAuditWorkflow(counts, gates, workflow, evaluations) {
  if (!counts || !gates || !workflow) return;
  const items = [{ label: 'Signal evaluations', count: evaluations, evidence: 'Pinned full-run decision records; includes repeated evaluations' }, ...workflow.counts];
  counts.replaceChildren(...items.map(item => {
    const row = node('tr'), heading = node('th', item.label); heading.scope = 'row';
    row.append(heading, node('td', item.count ?? 'unavailable'), node('td', item.evidence)); return row;
  }));
  if (!workflow.gates.length) {
    const row = node('tr'), cell = node('td', 'Ordered gate traces unavailable'); cell.colSpan = 5; row.append(cell); gates.replaceChildren(row); return;
  }
  gates.replaceChildren(...workflow.gates.map(gate => {
    const row = node('tr'), heading = node('th', gate.name); heading.scope = 'row';
    row.append(node('td', gate.order), heading, node('td', gate.reached), node('td', gate.passed), node('td', gate.rejected)); return row;
  }));
}
