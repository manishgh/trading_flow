const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const root = path.resolve(__dirname, '../..');

function load(name, exports) {
  const file = path.join(root, 'design/prototypes', name + '.dc.html');
  const html = fs.readFileSync(file, 'utf8');
  assert.doesNotMatch(html, /intraday|OpportunityProbability|opening.range/iu);
  const script = html.match(/<script\b[^>]*type="text\/x-dc"[^>]*>([\s\S]*?)<\/script>/u);
  assert.ok(script, name + ' must contain its component script');
  class DCLogic {
    props = { liveQuotes: false };
    setState(update) {
      Object.assign(this.state, typeof update === 'function' ? update(this.state) : update);
    }
  }
  const context = vm.createContext({
    DCLogic,
    React: { createElement: (type, props, ...children) => ({ type, props, children }) },
    setInterval: () => 0, clearInterval: () => {},
    setTimeout: () => 0, clearTimeout: () => {}
  });
  return new vm.Script(script[1] + '\n;({Component, ' + exports + '});', { filename: file })
    .runInContext(context, { timeout: 1000 });
}

test('backtest retains only existing swing fixtures, without inferred promotion', () => {
  const { Component, STRATEGIES, UNIVERSES, REJECTIONS, SUGGESTIONS, TRADES, SNAPSHOTS } =
    load('Backtest Lab', 'STRATEGIES, UNIVERSES, REJECTIONS, SUGGESTIONS, TRADES, SNAPSHOTS');
  assert.deepEqual(Array.from(STRATEGIES, s => [s.id, s.ret, s.dd, s.trades, s.win]), [
    ['s2', 22.18, 12.4, 61, 54.1],
    ['s3', 9.04, 7.12, 88, 51.1],
    ['s4', -3.41, 9.86, 47, 38.3]
  ]);
  for (const records of [REJECTIONS, SUGGESTIONS, TRADES]) {
    assert.deepEqual(Object.keys(records).sort(), ['s2', 's3', 's4']);
  }
  assert.equal(SNAPSHOTS.length, 3);
  const component = new Component();
  assert.ok(UNIVERSES[component.state.wishlist]);
  for (const strategy of STRATEGIES) {
    component.setState({ selected: [strategy.id], drill: strategy.id });
    for (const phase of ['configure', 'running', 'results']) {
      component.setState({ phase });
      const view = component.renderVals();
      assert.ok(view.gates.every(g => g.note.startsWith('Not evaluated')));
      assert.ok(view.catalog.every(s => s.status !== 'PROMOTED'));
    }
  }
  component.setState({ selected: [] });
  assert.match(component.renderVals().selectedCountText, /Select at least one/u);
});

test('desktop desk keeps swing fixtures and fixed swing screening', () => {
  const { Component, SEED, SCREENERS, TABS } = load('Trading Desk', 'SEED, SCREENERS, TABS');
  assert.deepEqual(Array.from(SEED, s => s.t), ['AVGO', 'ANET', 'CRWD', 'DKNG', 'UBER']);
  assert.deepEqual(Object.keys(SCREENERS), ['swing']);
  assert.ok(SEED.every(s => s.mh === 'swing 10b'));
  const component = new Component();
  assert.ok(SEED.some(s => s.t === component.state.selected));
  for (const row of SEED) {
    for (const tab of TABS) {
      component.setState({ selected: row.t, tab: tab.key });
      const view = component.renderVals();
      assert.ok(view.sel);
      assert.equal(view.setScreenerScope, undefined);
    }
  }
  component.renderVals().syncScreener();
  component.renderVals().promoteCandidates();
  assert.match(component.renderVals().toastBody, /swing quality/u);
});

test('mobile retains its existing swing fixture and valid selected symbol', () => {
  const { Component, SEED, TABS } = load('Trading Desk Mobile', 'SEED, TABS');
  assert.deepEqual(Array.from(SEED, s => s.t), ['UBER']);
  assert.equal(SEED[0].mh, 'swing 10b');
  const component = new Component();
  assert.equal(component.state.selected, 'UBER');
  for (const tab of TABS) {
    for (const view of ['list', 'detail']) {
      component.setState({ tab: tab.key, view });
      assert.ok(component.renderVals());
    }
  }
});

test('operations deletes retired positions/orders without relabeling evidence', () => {
  const { Component, POSITIONS, ORDERS, WISHLISTS, SCREENER_QUERIES, BLOCKS, EVENTS } =
    load('Desk Operations', 'POSITIONS, ORDERS, WISHLISTS, SCREENER_QUERIES, BLOCKS, EVENTS');
  assert.deepEqual(Array.from(POSITIONS, p => p.t), ['AVGO', 'UBER']);
  assert.deepEqual(Array.from(ORDERS, o => o.sym), ['DKNG']);
  assert.deepEqual(Object.keys(SCREENER_QUERIES), ['swing']);
  assert.equal(BLOCKS.length, 0);
  assert.equal(EVENTS.length, 0);
  const component = new Component();
  assert.equal(component.state.wishlist, 'swing');
  for (const wishlist of Object.keys(WISHLISTS)) {
    component.selectWishlist(wishlist);
    for (const page of ['positions', 'orders', 'wishlists', 'ops']) {
      component.setState({ page });
      const view = component.renderVals();
      assert.equal(view.posCount, 2);
      assert.match(view.opsOverall, /not evaluated/u);
      assert.equal(view.setImportScope, undefined);
    }
    component.renderVals().runImport();
  }
});
