const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { runInNewContext } = require('node:vm');

const scope = { window: {} };
runInNewContext(readFileSync(resolve(__dirname, '../../src/TradingFlow.Web/wwwroot/js/stock-chart-indicators.js'), 'utf8'), scope);
const { joinedStudies, pageRanges } = scope.window.TradingFlowStockChartIndicators;

// Deliberately different source revisions represent separately received pages, not one invented continuous calculation.
function page(times, values, revision, anchor = '2026-01-01T00:00:00Z') {
    return {
        bars: times.map(time => ({ startUtc: new Date(time).toISOString(), availableAtUtc: new Date(time + 1).toISOString() })),
        seriesKey: { interval: '1d' }, asOfUtc: '2026-11-15T12:00:00Z',
        evidence: { provider: 'alpaca', feed: 'sip', adjustmentPolicy: 'all' },
        indicators: [{ id: 'sma20', sourceInterval: '1d', sessionView: 'regular', adjustmentPolicy: 'all',
            formulaVersion: 'fixture', parameters: '20', sourceRevision: revision, anchorUtc: anchor,
            reconstructed: true, columns: ['value'], samples: [values], segmentStarts: [0],
            segmentAnchorsUtc: [anchor], availabilityRuns: [] }]
    };
}

test('older valid values retain their own provenance without replacing newer overlaps or joining page seams', () => {
    const latest = page([400, 500], [40, 50], 'latest');
    const middle = page([200, 300, 400], [20, null, 999], 'middle', '2025-12-01T00:00:00Z');
    const oldest = page([100, 200, 300], [10, 999, 999], 'oldest', '2025-11-01T00:00:00Z');
    const result = joinedStudies(latest, [oldest, middle], ['sma20'])[0];
    assert.equal(result.points.get(100).values[0], 10);
    assert.equal(result.points.get(200).values[0], 20);
    assert.equal(result.points.has(300), false, 'an older page must not fill the newer page gap');
    assert.equal(result.points.get(400).values[0], 40);
    assert.equal(result.points.get(100).sourceRevision, 'oldest');
    assert.equal(result.points.get(200).sourceRevision, 'middle');
    assert.equal(result.points.get(400).sourceRevision, 'latest');
    assert.equal(result.points.get(200).anchorUtc, '2025-12-01T00:00:00Z');
    assert.equal(result.points.get(100).reconstructed, true);
    assert.equal(result.points.get(200).historySegment, true);
    for (const seam of [100, 200, 400]) assert.equal(result.points.get(seam).segmentStart, true);
    assert.equal(result.separateHistory, true);
    assert.equal(result.earlierUnavailable, false);
});

test('a concrete price-basis contradiction suppresses the old page but leaves current values intact', () => {
    const latest = page([300], [30], 'latest');
    const older = page([100, 200], [10, 20], 'older');
    older.evidence.adjustmentPolicy = 'raw';
    const result = joinedStudies(latest, [older], ['sma20'])[0];
    assert.equal(result.points.size, 1);
    assert.equal(result.points.get(300).values[0], 30);
    assert.equal(result.earlierUnavailable, true);
});

test('a segment starting in warm-up retains its real seed at the first available value', () => {
    const latest = page([100, 200], [null, 20], 'revision');
    const result = joinedStudies(latest, [], ['sma20'])[0];
    assert.equal(result.points.get(200).anchorUtc, '2026-01-01T00:00:00Z');
    assert.equal(result.points.get(200).segmentStart, true);
});

test('candles and studies share newer-page ownership including missing candles', () => {
    const latest = page([400, 500], [40, 50], 'latest');
    const middle = page([200], [20], 'middle');
    const oldest = page([100, 200, 300, 400], [10, 999, 999, 999], 'oldest');
    const ranges = pageRanges(latest, [oldest, middle]);
    const times = Array.from(ranges.flatMap(({ page, before }) => page.bars
        .map(bar => Date.parse(bar.startUtc)).filter(time => time < before))).sort((a, b) => a - b);
    assert.deepEqual(times, [100, 200, 400, 500]);
    const study = joinedStudies(latest, [oldest, middle], ['sma20'])[0];
    assert.deepEqual(Array.from(study.points.keys()).sort((a, b) => a - b), times);
});
