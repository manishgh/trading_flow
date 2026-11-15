(() => {
    'use strict';

    const localDate = new Intl.DateTimeFormat(undefined, {
        timeZone: 'America/New_York', month: 'short', day: 'numeric'
    });
    const localTime = new Intl.DateTimeFormat(undefined, {
        timeZone: 'America/New_York', month: 'short', day: 'numeric',
        hour: '2-digit', minute: '2-digit', hourCycle: 'h23'
    });
    const levelSwatches = {
        vwap: 'levelA', sma50: 'levelA', sma200: 'levelB',
        opening_range_high: 'levelB', opening_range_low: 'levelB',
        premarket_high: 'premarket', premarket_low: 'premarket'
    };

    // Map a UTC instant to the candle that actually contains it, with an exclusive close.
    function candleIndex(bars, milliseconds) {
        let left = 0;
        let right = bars.length;
        while (left < right) {
            const middle = (left + right) >>> 1;
            if (bars[middle].startMs <= milliseconds) left = middle + 1;
            else right = middle;
        }
        const index = left - 1;
        return index >= 0 && milliseconds < bars[index].endMs ? index : -1;
    }

    // Read workspace theme tokens for the canvas, including the colour-blind theme.
    function palette(element) {
        const css = getComputedStyle(element);
        const tokens = {
            background: '--panel', ink: '--muted', grid: '--line',
            up: '--stock-chart-up', down: '--stock-chart-down',
            premarket: '--stock-chart-premarket', postmarket: '--stock-chart-postmarket',
            levelA: '--stock-chart-level-a', levelB: '--stock-chart-level-b', levelC: '--stock-chart-level-c'
        };
        return Object.fromEntries(Object.entries(tokens).map(([name, token]) =>
            [name, css.getPropertyValue(token).trim()]));
    }

    // Interpolate between integer bar centers because Lightweight Charts 5.0.9 treats fractional indexes as zero.
    function logicalCoordinate(scale, index) {
        const first = scale.logicalToCoordinate(0);
        const step = scale.logicalToCoordinate(1) - first;
        return first + step * index;
    }

    // Convert availability and session boundaries into coordinates inside the real candle interval.
    function boundary(scale, bars, milliseconds) {
        const index = candleIndex(bars, milliseconds);
        if (index >= 0) {
            const bar = bars[index];
            const offset = (milliseconds - bar.startMs) / (bar.endMs - bar.startMs);
            return logicalCoordinate(scale, bar.plotIndex - .5 + offset);
        }
        let left = 0;
        let right = bars.length;
        while (left < right) {
            const middle = (left + right) >>> 1;
            if (bars[middle].startMs < milliseconds) left = middle + 1;
            else right = middle;
        }
        const before = bars[left - 1];
        const after = bars[left];
        if (before && (before.endMs === milliseconds || !after)) return logicalCoordinate(scale, before.plotIndex + .5);
        return logicalCoordinate(scale, after.plotIndex - .5);
    }

    // Retain a blank plotted slot for observed same-segment time gaps without fabricating OHLCV.
    function buildTimeline(source) {
        const timeline = [];
        const bars = [];
        for (const bar of source) {
            const previous = bars.at(-1);
            if (previous && bar.sessionDate === previous.sessionDate && bar.segment === previous.segment &&
                bar.startMs > previous.endMs) {
                timeline.push({ time: previous.endMs / 1000 });
            }
            const point = { ...bar, plotIndex: timeline.length, time: bar.startMs / 1000 };
            timeline.push(point);
            bars.push(point);
        }
        return { bars, timeline };
    }

    // Collapse consecutive extended-hours bars into pane shading ranges with session boundaries.
    function extendedRanges(bars) {
        const ranges = [];
        for (const bar of bars) {
            if (!['premarket', 'postmarket'].includes(bar.segment)) continue;
            const prior = ranges.at(-1);
            if (prior?.end === bar.startMs && prior.segment === bar.segment) {
                prior.end = bar.endMs;
            } else {
                ranges.push({ start: bar.startMs, end: bar.endMs, segment: bar.segment });
            }
        }
        return ranges;
    }

    // Draw only the part of a price band or level whose underlying data had become available.
    function drawPriceRange(ctx, width, height, scale, candles, bars, start, end, lower, upper, color) {
        if (!(end > start)) return;
        const left = Math.max(0, boundary(scale, bars, start));
        const right = Math.min(width, boundary(scale, bars, end));
        const top = candles.priceToCoordinate(upper);
        const bottom = candles.priceToCoordinate(lower);
        if (right <= left || bottom < 0 || top > height) return;
        ctx.strokeStyle = color;
        ctx.fillStyle = color;
        if (upper !== lower) {
            ctx.globalAlpha = .15;
            ctx.fillRect(left, Math.max(0, top), right - left, Math.min(bottom, height) - Math.max(0, top));
        }
        ctx.globalAlpha = .85;
        ctx.beginPath();
        ctx.moveTo(left, top);
        ctx.lineTo(right, top);
        if (upper !== lower) {
            ctx.moveTo(left, bottom);
            ctx.lineTo(right, bottom);
        }
        ctx.stroke();
        ctx.globalAlpha = 1;
    }

    // Reserve a short price annotation after the last candle for current-only geometry.
    function drawCurrentBand(ctx, width, height, scale, candles, bars, band, color) {
        const left = boundary(scale, bars, bars.at(-1).endMs);
        const isZone = band.upper > band.lower;
        const right = Math.min(width, left + (isZone ? 40 : 20));
        const top = candles.priceToCoordinate(band.upper);
        const bottom = candles.priceToCoordinate(band.lower);
        if (left < 0 || right <= left || bottom < 0 || top > height) return;
        const y = Math.max(0, top);
        const bandHeight = Math.max(2, Math.min(bottom, height) - y);
        ctx.fillStyle = color;
        ctx.globalAlpha = isZone ? .28 : .68;
        ctx.fillRect(left, y, right - left, bandHeight);
        if (isZone) {
            ctx.globalAlpha = .95;
            ctx.fillRect(left, y, right - left, 1);
            ctx.fillRect(left, y + bandHeight - 1, right - left, 1);
        }
        ctx.globalAlpha = 1;
    }

    // Distinguish calculations made from a retrieved basis and shown retrospectively at the source close.
    function reconstructedAtClose(point, bar, reconstructed) {
        return reconstructed && bar && Date.parse(point.availableAtUtc) > bar.endMs;
    }

    // Trace only matched reconstructed points, breaking across gaps, sessions or recorded basis boundaries.
    function reconstructionCurve(level, bars, basisBreaks, reconstructed) {
        if (!reconstructed || !level.points?.length) return [];
        return level.points.flatMap(point => {
            const start = Date.parse(point.startUtc);
            const candle = candleIndex(bars, start);
            const bar = bars[candle];
            if (bar?.startMs !== start || !reconstructedAtClose(point, bar, reconstructed)) return [];
            const prior = bars[candle - 1];
            const continuous = prior && prior.plotIndex + 1 === bar.plotIndex &&
                (level.key !== 'vwap' || prior.sessionDate === bar.sessionDate) &&
                !basisBreaks.some(boundaryAt => boundaryAt > prior.startMs && boundaryAt <= bar.startMs);
            return [{ index: bar.plotIndex, closeAt: bar.endMs, value: point.value, continuous }];
        });
    }

    // Draw the historical calculation distinctly from levels with earlier actual availability.
    function drawReconstructedCurve(ctx, canvasWidth, scale, candles, bars, curve, color) {
        ctx.strokeStyle = color;
        ctx.lineWidth = 1.75;
        ctx.setLineDash([3, 3]);
        ctx.beginPath();
        let previous;
        for (const point of curve) {
            const x = boundary(scale, bars, point.closeAt);
            const y = candles.priceToCoordinate(point.value);
            if (point.continuous && previous?.index + 1 === point.index) {
                ctx.lineTo(x, y);
            } else {
                // Keep one matched point visible even before there is a second point for a curve.
                if (x >= 0 && x < canvasWidth) {
                    ctx.moveTo(x - 2, y);
                    ctx.lineTo(x, y);
                }
                ctx.moveTo(x, y);
            }
            previous = point;
        }
        ctx.stroke();
        ctx.lineWidth = 1;
        ctx.setLineDash([]);
    }

    // Walk forward through verified level points and stop a superseded level at the next effective point.
    function levelRanges(level, bars, lastEnd, basisBreaks) {
        if (!level.points?.length) return [];
        const ranges = [];
        const points = level.points;
        for (let index = 0; index < points.length; index++) {
            const point = points[index];
            const start = Math.max(Date.parse(point.startUtc), Date.parse(point.availableAtUtc));
            const next = points[index + 1];
            const nextStart = next ? Date.parse(next.startUtc) : lastEnd;
            const following = candleIndex(bars, nextStart);
            const followingClose = following < 0 ? nextStart : bars[following].endMs;
            const nextEffective = next ? Math.max(nextStart, Date.parse(next.availableAtUtc)) : lastEnd;
            const breakAt = basisBreaks.find(time => time > start) ?? lastEnd;
            const end = Math.min(nextEffective, followingClose, breakAt);
            if (end > start) ranges.push({ start, end, value: point.value });
        }
        return ranges;
    }

    // Attach one pane primitive so range shading follows the chart's native scale, pan and zoom.
    function attachShading(candles, scale, model, colors) {
        let refresh;
        // Draw palette-encoded overlays below the price candles inside the visible pane.
        const renderer = { draw(target) {
            if (!model.bars.length) return;
            target.useMediaCoordinateSpace(({ context: ctx, mediaSize: { width, height } }) => {
                const bars = model.bars;
                ctx.save();
                if (model.layers.sessions) {
                    ctx.globalAlpha = .09;
                    for (const range of model.sessions) {
                        const start = Math.max(0, boundary(scale, bars, range.start));
                        const end = Math.min(width, boundary(scale, bars, range.end));
                        if (end <= start) continue;
                        ctx.fillStyle = colors[range.segment];
                        ctx.fillRect(start, 0, end - start, height);
                    }
                    ctx.globalAlpha = 1;
                }
                for (const band of model.zones) {
                    if (!model.layers[band.type] || model.hiddenZones.has(band.id)) continue;
                    const color = colors[band.type === 'support' ? 'up' : 'down'];
                    if (band.currentOnly) drawCurrentBand(ctx, width, height, scale, candles, bars, band, color);
                    else drawPriceRange(ctx, width, height, scale, candles, bars, band.start, band.end, band.lower, band.upper, color);
                }
                for (const level of model.levels) {
                    if (model.hiddenLevels.has(level.key)) continue;
                    const color = colors[levelSwatches[level.key] || 'levelC'];
                    ctx.setLineDash([4, 4]);
                    for (const range of level.ranges) {
                        drawPriceRange(ctx, width, height, scale, candles, bars,
                            range.start, range.end, range.value, range.value, color);
                    }
                    if (level.currentOnly) drawCurrentBand(ctx, width, height, scale, candles, bars,
                        { upper: level.value, lower: level.value }, color);
                    drawReconstructedCurve(ctx, width, scale, candles, bars, level.curve, color);
                }
                ctx.restore();
            });
        } };
        const view = {
            // Preserve contrast by placing bands and shading beneath the candle glyphs.
            zOrder() { return 'bottom'; },
            // Reuse the renderer across background redraws.
            renderer() { return renderer; }
        };
        candles.attachPrimitive({
            // Capture the library's redraw hook after the series owns the primitive.
            attached({ requestUpdate }) { refresh = requestUpdate; },
            // Preserve one view for the lifetime of this plot.
            paneViews() { return [view]; }
        });
        // Schedule redraws after model or layer changes.
        return () => refresh?.();
    }

    // Set the prior leftmost candle as the viewport anchor when prepending or replacing history.
    function preserveRange(scale, oldBars, bars, range) {
        if (!range || !oldBars.length || !bars.length) return false;
        const base = oldBars.find(bar => bar.plotIndex >= Math.ceil(range.from)) || oldBars.at(-1);
        const next = bars.find(bar => bar.startMs >= base.startMs);
        if (!next) return false;
        const delta = next.plotIndex - base.plotIndex;
        scale.setVisibleLogicalRange({ from: range.from + delta, to: range.to + delta });
        return true;
    }

    // Build the real Lightweight Charts panes once per mounted control and expose plot operations.
    function createPlot(element, onInspect) {
        const library = window.LightweightCharts;
        const chartRoot = element.closest('[data-stock-chart]');
        const colors = palette(chartRoot);
        const model = { bars: [], timeline: [], zones: [], sessions: [], levels: [], events: [], layers: {}, hiddenLevels: new Set(), hiddenZones: new Set() };
        const chart = library.createChart(element, {
            autoSize: true,
            layout: { background: { color: colors.background }, textColor: colors.ink, attributionLogo: false, fontSize: 11 },
            grid: { vertLines: { color: colors.grid }, horzLines: { color: colors.grid } },
            rightPriceScale: { borderColor: colors.grid },
            timeScale: {
                borderColor: colors.grid, timeVisible: true, secondsVisible: false, rightOffset: 3,
                lockVisibleTimeRangeOnResize: true,
                // Preserve UTC as the time key and label ticks in New York exchange time.
                tickMarkFormatter(time, type) {
                    const date = new Date(time * 1000);
                    if (type > library.TickMarkType.DayOfMonth) {
                        return localTime.formatToParts(date).filter(part => ['hour', 'minute', 'literal'].includes(part.type))
                            .slice(-3).map(part => part.value).join('');
                    }
                    return localDate.format(date);
                }
            },
            localization: {
                // Show the linked crosshair label in the same exchange time as the axis.
                timeFormatter: time => localTime.format(new Date(time * 1000)) + ' NY'
            },
            handleScroll: { vertTouchDrag: false, mouseWheel: false }
        });
        const scale = chart.timeScale();
        const candles = chart.addSeries(library.CandlestickSeries, { priceLineVisible: false, lastValueVisible: false });
        let volume;
        const markers = library.createSeriesMarkers(candles, [], { autoScale: false });
        const refreshShading = attachShading(candles, scale, model, colors);

        // Plot news and earnings only in matching candle intervals. The list keeps every original event.
        function paintMarkers() {
            const shapes = [];
            for (const event of model.events) {
                if (!model.layers[event.kind]) continue;
                const index = candleIndex(model.bars, Date.parse(event.eventAtUtc));
                if (index < 0) continue;
                shapes.push({
                    time: model.bars[index].startMs / 1000, id: event.id, position: 'aboveBar',
                    shape: event.kind === 'earnings' ? 'square' : 'circle',
                    color: event.kind === 'earnings' ? colors.levelB : colors.levelA, size: .8
                });
            }
            markers.setMarkers(shapes.sort((a, b) => a.time - b.time || a.id.localeCompare(b.id)));
        }

        // Use workspace direction colours while retaining independent volume and wick encodings.
        function paintTheme() {
            Object.assign(colors, palette(chartRoot));
            chart.applyOptions({
                layout: { background: { color: colors.background }, textColor: colors.ink },
                grid: { vertLines: { color: colors.grid }, horzLines: { color: colors.grid } },
                rightPriceScale: { borderColor: colors.grid },
                timeScale: { borderColor: colors.grid }
            });
            candles.applyOptions({
                upColor: colors.up, borderUpColor: colors.up, wickUpColor: colors.up,
                downColor: colors.down, borderDownColor: colors.down, wickDownColor: colors.down
            });
        }

        // Retain the graph's present time span and put its right edge just beyond newest data.
        function latest() {
            const range = scale.getVisibleLogicalRange();
            if (!model.bars.length) return;
            const span = range ? range.to - range.from : Math.min(model.bars.length, 62);
            const right = model.timeline.length + 2;
            scale.setVisibleLogicalRange({ from: right - span, to: right });
        }

        // Rebuild the bounded plotted data and preserve a manually panned or prepended time range.
        function setData(data, retainPosition) {
            const oldBars = model.bars;
            const range = scale.getVisibleLogicalRange();
            Object.assign(model, data, buildTimeline(data.bars));
            const lastEnd = model.bars.at(-1)?.endMs;
            model.sessions = extendedRanges(model.bars);
            model.zones = data.zones.map(zone => ({
                ...zone, start: Date.parse(zone.effectiveAtUtc), end: zone.untilUtc ? Date.parse(zone.untilUtc) : lastEnd
            }));
            const basisBreaks = model.basisBreaks.map(Date.parse).sort((a, b) => a - b);
            model.levels = data.levels.map(level => ({
                ...level,
                ranges: levelRanges(level, model.bars, lastEnd, basisBreaks),
                curve: reconstructionCurve(level, model.bars, basisBreaks, data.reconstructed),
                currentOnly: !level.points?.length
            }));
            candles.setData(model.timeline.map(bar => ({
                time: bar.time, open: bar.open, high: bar.high, low: bar.low, close: bar.close
            })));
            paintVolume();
            paintMarkers();
            if (!model.bars.length) return;
            const wasAtLatest = range && range.to >= oldBars.at(-1)?.plotIndex;
            if ((!wasAtLatest || retainPosition) && preserveRange(scale, oldBars, model.bars, range)) return;
            scale.applyOptions({ timeVisible: data.interval !== '1d' });
            if (model.bars.length < 65 && !oldBars.length) {
                scale.setVisibleLogicalRange({ from: -.5, to: model.timeline.length + 2 });
            } else latest();
        }

        // A disabled volume layer clears the series and releases its native pane.
        function paintVolume() {
            if (!model.layers.volume) {
                if (volume) chart.removeSeries(volume);
                volume = null;
                return;
            }
            if (!volume) {
                volume = chart.addSeries(library.HistogramSeries, {
                    priceFormat: { type: 'volume' }, priceLineVisible: false, lastValueVisible: false
                }, 1);
                chart.panes()[1].setStretchFactor(.25);
            }
            volume.setData(model.bars.map(bar => ({
                time: bar.time, value: bar.volume, color: bar.close >= bar.open ? colors.up : colors.down
            })));
        }

        // Toggle only the rendered layers, leaving the same underlying series and time range.
        function setLayers(layers, hiddenLevels, hiddenZones) {
            const volumeChanged = model.layers.volume !== layers.volume;
            model.layers = { ...layers };
            model.hiddenLevels = hiddenLevels;
            model.hiddenZones = hiddenZones;
            if (volumeChanged) paintVolume();
            paintMarkers();
            refreshShading();
        }

        // Center a keyboard-selected candle only when it has left the user's current viewport.
        function reveal(candle) {
            const bar = model.bars[candle];
            const index = bar.plotIndex;
            const range = scale.getVisibleLogicalRange();
            if (range && (index < range.from || index > range.to)) {
                const halfSpan = (range.to - range.from) / 2;
                scale.setVisibleLogicalRange({ from: index - halfSpan, to: index + halfSpan });
            }
            chart.setCrosshairPosition(bar.close, bar.time, candles);
        }

        // Repaint model colours when the workspace changes appearance or inherits a system change.
        function themeChanged() {
            paintTheme();
            paintVolume();
            paintMarkers();
            refreshShading();
        }

        const theme = new MutationObserver(themeChanged);
        theme.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
        const media = matchMedia('(prefers-color-scheme: dark)');
        media.addEventListener('change', themeChanged);
        paintTheme();
        // Pointer inspection updates the shared candle readout using its real interval start.
        chart.subscribeCrosshairMove(({ time }) => { if (typeof time === 'number') onInspect(time * 1000); });
        return {
            setData, setLayers, latest, reveal,
            // Stop observing theme changes and detach pane rendering when the component is removed.
            dispose() {
                theme.disconnect();
                media.removeEventListener('change', themeChanged);
                chart.remove();
            }
        };
    }

    window.TradingFlowStockChartPlot = { createPlot, candleIndex, reconstructedAtClose };
})();
