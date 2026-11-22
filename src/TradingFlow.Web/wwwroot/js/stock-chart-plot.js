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

    // Weekly markers use the assigned exchange week, including its holidays and after-close events.
    function eventCandleIndex(bars, event, interval) {
        if (interval !== '1w') return candleIndex(bars, Date.parse(event.eventAtUtc));
        return event.weeklySessionDate ? bars.findIndex(bar => bar.sessionDate === event.weeklySessionDate) : -1;
    }

    // Read workspace theme tokens for the canvas, including the colour-blind theme.
    function palette(element) {
        const css = getComputedStyle(element);
        const tokens = {
            background: '--panel', ink: '--muted', grid: '--line',
            up: '--stock-chart-up', down: '--stock-chart-down',
            premarket: '--stock-chart-premarket', postmarket: '--stock-chart-postmarket',
            levelA: '--stock-chart-level-a', levelB: '--stock-chart-level-b', levelC: '--stock-chart-level-c',
            study0: '--stock-chart-study-0', study1: '--stock-chart-study-1', study2: '--stock-chart-study-2',
            study3: '--stock-chart-study-3', study4: '--stock-chart-study-4'
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
    function drawPriceRange(ctx, width, height, scale, candles, bars, start, end, lower, upper, color, highlighted = false) {
        if (!(end > start)) return;
        const left = Math.max(0, boundary(scale, bars, start));
        const right = Math.min(width, boundary(scale, bars, end));
        const top = candles.priceToCoordinate(upper);
        const bottom = candles.priceToCoordinate(lower);
        if (right <= left || bottom < 0 || top > height) return;
        ctx.strokeStyle = color;
        ctx.fillStyle = color;
        if (upper !== lower) {
            ctx.globalAlpha = highlighted ? .48 : .15;
            ctx.fillRect(left, Math.max(0, top), right - left, Math.min(bottom, height) - Math.max(0, top));
        }
        ctx.lineWidth = highlighted ? 2.5 : 1;
        ctx.globalAlpha = highlighted ? 1 : .85;
        ctx.beginPath();
        ctx.moveTo(left, top);
        ctx.lineTo(right, top);
        if (upper !== lower) {
            ctx.moveTo(left, bottom);
            ctx.lineTo(right, bottom);
        }
        ctx.stroke();
        ctx.lineWidth = 1;
        ctx.globalAlpha = 1;
    }

    // Reserve a short price annotation after the last candle for current-only geometry.
    function drawCurrentBand(ctx, width, height, scale, candles, bars, band, color, highlighted = false) {
        const left = boundary(scale, bars, bars.at(-1).endMs);
        const isZone = band.upper > band.lower;
        const right = Math.min(width, left + (isZone ? 40 : 20));
        const top = candles.priceToCoordinate(band.upper);
        const bottom = candles.priceToCoordinate(band.lower);
        if (left < 0 || right <= left || bottom < 0 || top > height) return;
        const y = Math.max(0, top);
        const bandHeight = Math.max(2, Math.min(bottom, height) - y);
        ctx.fillStyle = color;
        ctx.globalAlpha = isZone ? (highlighted ? .48 : .28) : (highlighted ? .95 : .68);
        ctx.fillRect(left, y, right - left, bandHeight);
        if (isZone) {
            const border = highlighted ? 2.5 : 1;
            ctx.globalAlpha = highlighted ? 1 : .95;
            ctx.fillRect(left, y, right - left, border);
            ctx.fillRect(left, y + bandHeight - border, right - left, border);
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
                    const highlighted = model.highlightedOverlay === 'support' && band.type === 'support';
                    if (band.currentOnly) drawCurrentBand(ctx, width, height, scale, candles, bars, band, color, highlighted);
                    else drawPriceRange(ctx, width, height, scale, candles, bars, band.start, band.end, band.lower, band.upper, color, highlighted);
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

    // Matches an overlay highlight key, which is the exact selection token (e.g. "ema21:1d"), against a study.
    function matchesHighlightedStudy(study, key) {
        return Boolean(key && study) && study.token === key;
    }

    // Draw study lines with explicit gaps, highlight emphasis/dimming, and an immediate redraw trigger.
    function attachStudyLine(component, scale, model, colors) {
        let refresh;
        const renderer = {
            // Keep the first valid point and avoid connecting across unknown samples or calculation segments.
            draw(target) {
                target.useMediaCoordinateSpace(({ context: ctx }) => {
                    const range = scale.getVisibleLogicalRange();
                    if (!range) return;
                    const begin = Math.max(0, Math.floor(range.from) - 1);
                    const end = Math.min(model.timeline.length - 1, Math.ceil(range.to) + 1);
                    const { study, index, series, color, dash } = component;
                    const highlight = model.highlightedOverlay;
                    const isMatch = Boolean(highlight) && matchesHighlightedStudy(study, highlight);
                    const isDimmed = Boolean(highlight) && !isMatch;
                    ctx.save();
                    ctx.lineWidth = isMatch ? 3.0 : isDimmed ? 1.1 : (component.index === 0 ? 1.6 : 1.25);
                    ctx.setLineDash(isMatch ? [] : dash);
                    ctx.globalAlpha = isDimmed ? 0.28 : 1.0;
                    ctx.strokeStyle = colors[color];
                    ctx.beginPath();
                    let lastY;
                    for (let offset = begin; offset <= end; offset++) {
                        const bar = model.timeline[offset];
                        const point = study.points.get(bar.startMs);
                        const value = point?.values[index];
                        if (!Number.isFinite(value)) {
                            lastY = undefined;
                            continue;
                        }
                        const x = logicalCoordinate(scale, offset);
                        const y = series.priceToCoordinate(value);
                        if (!Number.isFinite(y)) continue;
                        if (lastY === undefined || point.segmentStart) {
                            ctx.moveTo(x - .6, y);
                        } else if (study.source && study.sourceInterval !== study.chartInterval) {
                            ctx.lineTo(x, lastY);
                        }
                        ctx.lineTo(x, y);
                        lastY = y;
                    }
                    ctx.stroke();
                    ctx.restore();
                });
            }
        };
        const view = {
            // Keep the indicator lines visible over the grid and reference lines.
            renderer() { return renderer; }
        };
        component.series.attachPrimitive({
            // Capture Lightweight Charts' redraw hook so highlight toggles repaint immediately.
            attached({ requestUpdate }) { refresh = requestUpdate; },
            // Return the persistent view across native chart redraws.
            paneViews() { return [view]; }
        });
        return () => refresh?.();
    }

    // Name each visible lower pane inside its own canvas, alongside the accessible external readout.
    function attachStudyLabel(component, colors) {
        const renderer = {
            // Use the same theme tokens as the plot and the study swatch.
            draw(target) {
                target.useMediaCoordinateSpace(({ context: ctx, mediaSize }) => {
                    const study = component.study;
                    const title = study.name + ' · ' + study.sourceInterval;
                    ctx.save();
                    ctx.font = '600 11px system-ui, sans-serif';
                    ctx.fillStyle = colors.background;
                    ctx.fillRect(3, 2, Math.min(mediaSize.width - 3, ctx.measureText(title).width + 9), 16);
                    ctx.fillStyle = colors[component.color];
                    ctx.fillText(title, 7, 14, Math.max(0, mediaSize.width - 14));
                    ctx.restore();
                });
            }
        };
        const view = {
            // Keep the pane title clear of the study's data graphics.
            zOrder() { return 'top'; },
            // Reuse the title renderer on each redraw.
            renderer() { return renderer; }
        };
        component.series.attachPrimitive({
            // Return the pane title view for the life of this study series.
            paneViews() { return [view]; }
        });
    }

    // Build the real Lightweight Charts panes once per mounted control and expose plot operations.
    function createPlot(element, onInspect, onVisibleRange) {
        const library = window.LightweightCharts;
        const chartRoot = element.closest('[data-stock-chart]');
        const colors = palette(chartRoot);
        const model = { bars: [], timeline: [], zones: [], sessions: [], levels: [], events: [], layers: {},
            hiddenLevels: new Set(), hiddenZones: new Set(), studies: [], shownPane: null, highlightedOverlay: null };
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
                timeFormatter(time) {
                    const bar = model.interval === '1w' ? model.bars.find(item => item.time === time) : null;
                    if (bar) return bar.sessionDate + ' – ' + bar.weekly.lastSessionDate;
                    return localTime.format(new Date(time * 1000)) + ' NY';
                }
            },
            handleScroll: { vertTouchDrag: false, mouseWheel: false }
        });
        const scale = chart.timeScale();
        const candles = chart.addSeries(library.CandlestickSeries, { priceLineVisible: false, lastValueVisible: false });
        let volume;
        let formingSeries;
        let privateMarkers;
        let privateEvents = [];
        let plotted = [];
        let tradePlan = null;
        let tradePlanLines = [];
        const markers = library.createSeriesMarkers(candles, [], { autoScale: false });
        const refreshShading = attachShading(candles, scale, model, colors);

        // Removes all currently drawn Entry/Stop/Target price lines from the main candlestick series without altering candle data.
        function clearTradePlanLines() {
            for (const line of tradePlanLines) {
                try { candles.removePriceLine(line); } catch { /* Series may already be disposed. */ }
            }
            tradePlanLines = [];
        }

        // Renders native Lightweight Charts reference lines for finite positive server Entry, Stop, and Target levels using theme palette colours.
        function paintTradePlanLines() {
            clearTradePlanLines();
            if (!tradePlan) return;
            // The first line is the current quote the plan was priced at (the ask for a long, the bid for a short): a
            // reference, not the strategy's own entry trigger.
            const definitions = [
                { price: tradePlan.limit, title: tradePlan.direction === 'short' ? 'Bid' : 'Ask', color: colors.levelA || '#3b82f6', lineStyle: library.LineStyle.Dashed },
                { price: tradePlan.stop, title: 'Stop', color: colors.down || '#fb7185', lineStyle: library.LineStyle.Solid },
                { price: tradePlan.target, title: 'Target', color: colors.up || '#2dd4bf', lineStyle: library.LineStyle.Solid }
            ];
            for (const item of definitions) {
                if (typeof item.price !== 'number' || !Number.isFinite(item.price) || item.price <= 0) continue;
                tradePlanLines.push(candles.createPriceLine({
                    price: item.price,
                    color: item.color,
                    lineWidth: 2,
                    lineStyle: item.lineStyle,
                    axisLabelVisible: true,
                    title: item.title
                }));
            }
        }

        // Accepts advisory server Entry/Stop/Target levels (or null to clear) and updates native chart price lines without performing client math.
        function setTradePlanLevels(plan) {
            const toPositive = value => {
                const parsed = typeof value === 'number' ? value : Number(value);
                return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
            };
            const limit = plan ? toPositive(plan.limit) : null;
            const stop = plan ? toPositive(plan.stop) : null;
            const target = plan ? toPositive(plan.target) : null;
            tradePlan = (limit !== null || stop !== null || target !== null)
                ? { limit, stop, target, direction: plan?.direction === 'short' ? 'short' : 'long' } : null;
            paintTradePlanLines();
        }

        // Drop the native marker when its old coordinates no longer identify the selected candle.
        function clearCrosshair() {
            chart.clearCrosshairPosition();
        }

        scale.subscribeSizeChange(clearCrosshair);

        // Plot at most one grouped news marker and one grouped earnings marker per candle, keeping full details in the dialog.
        function paintMarkers() {
            const grouped = new Map();
            for (const event of model.events) {
                if (!model.layers[event.kind]) continue;
                const index = eventCandleIndex(model.bars, event, model.interval);
                if (index < 0) continue;
                const time = model.bars[index].startMs / 1000;
                const kind = event.kind === 'earnings' ? 'earnings' : 'news';
                const key = `${time}:${kind}`;
                const existing = grouped.get(key);
                if (existing) existing.count += 1;
                else grouped.set(key, { time, kind, count: 1 });
            }
            const shapes = [...grouped.values()]
                .sort((a, b) => a.time - b.time || a.kind.localeCompare(b.kind))
                .map(({ time, kind, count }) => ({
                    time,
                    id: `${kind}:${time}`,
                    position: 'aboveBar',
                    shape: kind === 'earnings' ? 'square' : 'circle',
                    color: kind === 'earnings' ? colors.levelB : colors.levelA,
                    size: .8,
                    text: count > 1 ? String(count) : ''
                }));
            markers.setMarkers(shapes);
        }

        // Keep private events in their own marker collection and group only on displayed completed candles.
        function paintPrivateEvents() {
            if (!privateMarkers) return;
            const matching = new Set();
            for (const event of privateEvents) {
                const index = eventCandleIndex(model.bars, event, model.interval);
                if (index >= 0) matching.add(model.bars[index].time);
            }
            privateMarkers.setMarkers([...matching].sort((one, two) => one - two).map(time => ({
                time, position: 'belowBar', shape: 'square', color: colors.levelC, size: .8
            })));
        }

        // Create private chart markers only for an explicit private page context.
        function setPrivateEvents(events) {
            privateEvents = model.interval !== '1w' && Array.isArray(events) ? events : [];
            if (!privateMarkers && privateEvents.length) privateMarkers = library.createSeriesMarkers(candles, [], { autoScale: false });
            paintPrivateEvents();
        }

        // Exclude whitespace slots and the separate preview when resolving private window endpoints.
        function visibleBounds() {
            const range = scale.getVisibleLogicalRange();
            if (!range) return null;
            const first = model.bars.find(bar => bar.plotIndex + .5 > range.from);
            const last = model.bars.findLast(bar => bar.plotIndex - .5 < range.to);
            return first && last && first.startMs < last.endMs ? { fromMs: first.startMs, toMs: last.endMs } : null;
        }

        // Only page-owned contexts observe the visible completed range as it pans or zooms.
        function reportVisibleRange() { onVisibleRange?.(visibleBounds()); }

        if (onVisibleRange) scale.subscribeVisibleLogicalRangeChange(reportVisibleRange);

        // Draw an outlined candle in its own series without changing completed candles, studies or cursor selection.
        function setForming(preview) {
            if (!preview && !formingSeries) return;
            const range = scale.getVisibleLogicalRange();
            if (!formingSeries) {
                formingSeries = chart.addSeries(library.CandlestickSeries, {
                    priceLineVisible: false, lastValueVisible: false, upColor: 'transparent', downColor: 'transparent',
                    borderUpColor: colors.up, borderDownColor: colors.down, wickUpColor: colors.up, wickDownColor: colors.down
                });
            }
            const time = preview ? Date.parse(preview.startUtc) / 1000 : null;
            formingSeries.setData(preview ? [{
                time, open: preview.open, high: preview.high, low: preview.low, close: preview.close
            }] : []);
            if (range && model.bars.length) scale.setVisibleLogicalRange(range);
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
            formingSeries?.applyOptions({ borderUpColor: colors.up, borderDownColor: colors.down,
                wickUpColor: colors.up, wickDownColor: colors.down });
            for (const component of plotted) {
                component.series.applyOptions({ color: colors[component.color] });
                for (const line of component.referenceLines) line.applyOptions({ color: colors.grid });
            }
        }

        // Reserve panes only for selected studies with actual numeric samples.
        function visibleStudies() {
            return model.studies.filter(study => study.points.size && (study.display === 'price' ||
                (study.display === 'pane' && (model.shownPane === null || study.token === model.shownPane))));
        }

        // Add a native scale for a study component; price-pane overlays opt out of autoscaling so distant averages never flatten candles.
        function addComponent(study, index, paneIndex) {
            const column = study.columns[index];
            const histogram = column === 'histogram';
            const color = 'study' + ((study.slot + index) % 5);
            const dash = [[], [5, 3], [2, 3], [8, 3, 2, 3]][(study.slot + index) % 4];
            const priceFormat = study.unit === 'shares' ? { type: 'volume' } : { type: 'price', precision: 4, minMove: .0001 };
            const options = { color: colors[color], lastValueVisible: false, priceLineVisible: false, priceFormat };
            const lineOptions = {
                ...options,
                lineVisible: false,
                crosshairMarkerVisible: false,
                ...(paneIndex === 0 ? { autoscaleInfoProvider: () => null } : {})
            };
            const series = histogram ? chart.addSeries(library.HistogramSeries, options, paneIndex) :
                chart.addSeries(library.LineSeries, lineOptions, paneIndex);
            const referencePrices = index === 0 && study.family === 'rsi' ? [30, 70] : [];
            const referenceLines = referencePrices.map(price => series.createPriceLine({
                price, color: colors.grid, lineWidth: 1, lineStyle: library.LineStyle.Dashed, axisLabelVisible: false
            }));
            const component = { study, index, series, color, dash, histogram, referenceLines, refresh: null };
            if (!histogram) component.refresh = attachStudyLine(component, scale, model, colors);
            if (paneIndex && index === 0) attachStudyLabel(component, colors);
            return component;
        }

        // Highlights a selected study line or support band on the chart and lights up its Y-axis badge, or clears highlight on null.
        function setHighlightedOverlay(key) {
            model.highlightedOverlay = key ? String(key).trim().toLowerCase() : null;
            for (const component of plotted) {
                const isMatch = Boolean(model.highlightedOverlay) && matchesHighlightedStudy(component.study, model.highlightedOverlay);
                component.series.applyOptions({ lastValueVisible: isMatch });
                component.refresh?.();
            }
            refreshShading();
        }

        // Keep native panes and series across live snapshots unless the visible study layout changes.
        function paintStudies() {
            const visible = visibleStudies();
            const expected = visible.flatMap(study => study.columns.map((_, index) => ({ study, index })));
            const changed = expected.length !== plotted.length || expected.some(({ study, index }, offset) =>
                plotted[offset].study.token !== study.token || plotted[offset].study.slot !== study.slot ||
                plotted[offset].study.columns[index] !== study.columns[index]);
            if (changed) {
                for (const component of plotted) chart.removeSeries(component.series);
                plotted = [];
                let paneIndex = volume ? 1 : 0;
                for (const study of visible) {
                    const lower = study.display === 'pane';
                    if (lower) paneIndex++;
                    const components = study.columns.map((_, index) => addComponent(study, index, lower ? paneIndex : 0));
                    if (lower) {
                        chart.panes()[paneIndex].setStretchFactor(.38);
                        components[0].series.priceScale().applyOptions({ scaleMargins: { top: .28, bottom: .14 } });
                    }
                    plotted.push(...components);
                }
                element.style.setProperty('--stock-chart-panes', paneIndex - (volume ? 1 : 0));
            }
            for (const [offset, component] of plotted.entries()) {
                component.study = expected[offset].study;
                const { study, index, histogram } = component;
                const isMatch = Boolean(model.highlightedOverlay) && matchesHighlightedStudy(study, model.highlightedOverlay);
                component.series.applyOptions({ lastValueVisible: isMatch });
                component.series.setData(model.timeline.map(bar => {
                    const value = study.points.get(bar.startMs)?.values[index];
                    if (!Number.isFinite(value)) return { time: bar.time };
                    if (histogram) return { time: bar.time, value, color: value >= 0 ? colors.up : colors.down };
                    return { time: bar.time, value };
                }));
                component.refresh?.();
            }
        }

        // Hide the previous profile during a selection request while preserving the price view.
        function clearStudies() {
            model.studies = [];
            paintStudies();
        }

        // Switch lower studies on compact screens, keeping the same candle time range.
        function setShownPane(pane) {
            if (model.shownPane === pane) return;
            model.shownPane = pane;
            paintStudies();
        }

        // Retain the graph's present time span and put its right edge just beyond newest data.
        function scrollToLatest() {
            const range = scale.getVisibleLogicalRange();
            if (!model.bars.length) return;
            const span = range ? range.to - range.from : Math.min(model.bars.length, 62);
            const right = model.timeline.length + 2;
            scale.setVisibleLogicalRange({ from: right - span, to: right });
        }

        // A request to show the latest candle leaves its readout selected and clears the old marker.
        function latest() {
            clearCrosshair();
            scrollToLatest();
        }

        // Fit the initial weekly page and preserve a manually panned or prepended time range.
        function setData(data, retainPosition) {
            clearCrosshair();
            const oldBars = model.bars;
            const range = scale.getVisibleLogicalRange();
            Object.assign(model, data, buildTimeline(data.bars));
            if (!data.studies) model.studies = [];
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
            paintStudies();
            paintMarkers();
            paintPrivateEvents();
            if (!model.bars.length) return;
            const wasAtLatest = range && range.to >= oldBars.at(-1)?.plotIndex;
            if ((!wasAtLatest || retainPosition) && preserveRange(scale, oldBars, model.bars, range)) return;
            scale.applyOptions({ timeVisible: !['1d', '1w'].includes(data.interval) });
            if (!oldBars.length && (data.interval === '1w' || model.bars.length < 65)) {
                scale.setVisibleLogicalRange({ from: -.5, to: model.timeline.length + 2 });
            } else scrollToLatest();
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
            if (volumeChanged && model.studies.length) {
                for (const component of plotted) chart.removeSeries(component.series);
                plotted = [];
                paintStudies();
            }
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
            paintStudies();
            paintMarkers();
            paintPrivateEvents();
            paintTradePlanLines();
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
            setData, setLayers, latest, reveal, clearStudies, setShownPane, setForming, setPrivateEvents, setTradePlanLevels, setHighlightedOverlay, visibleBounds,
            // Stop observing theme changes and detach pane rendering when the component is removed.
            dispose() {
                clearTradePlanLines();
                tradePlan = null;
                theme.disconnect();
                media.removeEventListener('change', themeChanged);
                scale.unsubscribeSizeChange(clearCrosshair);
                if (onVisibleRange) scale.unsubscribeVisibleLogicalRangeChange(reportVisibleRange);
                chart.remove();
            }
        };
    }

    window.TradingFlowStockChartPlot = { createPlot, candleIndex, eventCandleIndex, reconstructedAtClose };
})();
