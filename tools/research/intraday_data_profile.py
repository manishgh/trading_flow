#!/usr/bin/env python3
"""Build a no-lookahead intraday market-data profile from cached 1m candles.

The backtest engine is intentionally conservative and relatively expensive
because it simulates strategy/ticker execution. This profiler is a faster
research layer: it scans cached candles once, computes daily runner/volume
statistics, and writes CSV/Markdown files that help decide which deterministic
strategy settings deserve a full backtest.
"""

from __future__ import annotations

import argparse
import csv
import math
from collections import deque
from dataclasses import dataclass
from datetime import date, datetime, time, timedelta, timezone
from pathlib import Path
from statistics import mean, median
from typing import Iterable

try:
    from zoneinfo import ZoneInfo
except ImportError as exc:  # pragma: no cover - Python 3.9+ includes zoneinfo.
    raise SystemExit("Python zoneinfo support is required.") from exc


try:
    NY: ZoneInfo | None = ZoneInfo("America/New_York")
except Exception:
    NY = None

UTC = timezone.utc
REGULAR_OPEN = time(9, 30)
REGULAR_CLOSE = time(16, 0)


@dataclass(frozen=True)
class Bar:
    timestamp_utc: datetime
    timestamp_ny: datetime
    open: float
    high: float
    low: float
    close: float
    volume: float


@dataclass
class DayProfile:
    ticker: str
    date: str
    bars: int
    regular_bars: int
    extended_bars: int
    previous_regular_close: float | None
    regular_open: float | None
    regular_close: float | None
    regular_high: float | None
    regular_low: float | None
    high_time_ny: str
    low_time_ny: str
    premarket_volume: float
    first_30m_volume: float
    regular_volume: float
    full_day_volume: float
    rolling_avg_day_volume: float | None
    rolling_avg_first_30m_volume: float | None
    relative_day_volume: float | None
    relative_first_30m_volume: float | None
    peak_cumulative_rvol: float | None
    gap_pct: float | None
    open_to_high_pct: float | None
    open_to_close_pct: float | None
    prev_close_to_high_pct: float | None
    intraday_runner_score_pct: float | None
    max_15m_gain_pct: float | None
    max_30m_gain_pct: float | None
    max_15m_loss_pct: float | None
    opening_range_30m_pct: float | None
    close_location_value: float | None
    max_intraday_drawdown_pct: float | None
    event_move_type: str
    data_quality_flag: str
    runner_10pct: bool
    runner_25pct: bool
    prev_close_runner_10pct: bool
    prev_close_runner_25pct: bool


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--candles-root",
        default="data/backtest/normalized/20260313-20260613",
        help="Root containing TICKER/bars_1m.csv files.",
    )
    parser.add_argument(
        "--tickers",
        default="",
        help="Optional comma-separated tickers. Defaults to every folder with bars_1m.csv.",
    )
    parser.add_argument(
        "--output-root",
        default="data/research/intraday-profile/profile-90d-20260613",
        help="Directory for profile CSV and Markdown outputs.",
    )
    parser.add_argument(
        "--history-days",
        type=int,
        default=20,
        help="Prior regular sessions used for no-lookahead volume baselines.",
    )
    parser.add_argument(
        "--min-lines",
        type=int,
        default=1000,
        help="Skip ticker files with fewer data rows than this threshold.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    candles_root = Path(args.candles_root)
    output_root = Path(args.output_root)
    output_root.mkdir(parents=True, exist_ok=True)

    tickers = resolve_tickers(candles_root, args.tickers)
    profiles: list[DayProfile] = []
    skipped: list[tuple[str, str]] = []

    for ticker in tickers:
        path = candles_root / ticker / "bars_1m.csv"
        if not path.exists():
            skipped.append((ticker, "missing bars_1m.csv"))
            continue

        data_lines = max(0, count_lines(path) - 1)
        if data_lines < args.min_lines:
            skipped.append((ticker, f"only {data_lines} data rows"))
            continue

        ticker_profiles = profile_ticker(ticker, path, args.history_days)
        profiles.extend(ticker_profiles)
        print(f"profiled {ticker}: {len(ticker_profiles)} session(s)")

    daily_path = output_root / "ticker_day_profile.csv"
    summary_path = output_root / "ticker_summary.csv"
    report_path = output_root / "profile_report.md"

    write_daily_profiles(daily_path, profiles)
    summaries = build_summaries(profiles)
    write_summaries(summary_path, summaries)
    write_report(report_path, profiles, summaries, skipped, candles_root)

    print(f"DailyProfile={daily_path.resolve()}")
    print(f"TickerSummary={summary_path.resolve()}")
    print(f"Report={report_path.resolve()}")
    return 0


def resolve_tickers(candles_root: Path, tickers_csv: str) -> list[str]:
    if tickers_csv.strip():
        return sorted({item.strip().upper() for item in tickers_csv.split(",") if item.strip()})

    return sorted(
        path.name.upper()
        for path in candles_root.iterdir()
        if path.is_dir() and (path / "bars_1m.csv").exists()
    )


def count_lines(path: Path) -> int:
    with path.open("r", encoding="utf-8", newline="") as handle:
        return sum(1 for _ in handle)


def profile_ticker(ticker: str, path: Path, history_days: int) -> list[DayProfile]:
    by_day: dict[str, list[Bar]] = {}
    with path.open("r", encoding="utf-8", newline="") as handle:
        reader = csv.DictReader(handle)
        for row in reader:
            try:
                timestamp_utc = datetime.fromisoformat(row["timestamp"]).astimezone(UTC)
                timestamp_ny = to_new_york(timestamp_utc)
                bar = Bar(
                    timestamp_utc=timestamp_utc,
                    timestamp_ny=timestamp_ny,
                    open=float(row["open"]),
                    high=float(row["high"]),
                    low=float(row["low"]),
                    close=float(row["close"]),
                    volume=float(row["volume"]),
                )
            except (KeyError, ValueError):
                continue

            by_day.setdefault(timestamp_ny.date().isoformat(), []).append(bar)

    profiles: list[DayProfile] = []
    previous_regular_close: float | None = None
    day_volume_history: deque[float] = deque(maxlen=history_days)
    first_30m_volume_history: deque[float] = deque(maxlen=history_days)
    cumulative_by_minute_history: deque[list[float]] = deque(maxlen=history_days)

    for day in sorted(by_day):
        bars = sorted(by_day[day], key=lambda item: item.timestamp_utc)
        profile = build_day_profile(
            ticker,
            day,
            bars,
            previous_regular_close,
            list(day_volume_history),
            list(first_30m_volume_history),
            list(cumulative_by_minute_history),
        )
        profiles.append(profile)

        if profile.regular_close is not None:
            previous_regular_close = profile.regular_close
        if profile.regular_volume > 0:
            day_volume_history.append(profile.regular_volume)
        if profile.first_30m_volume > 0:
            first_30m_volume_history.append(profile.first_30m_volume)

        cumulative = cumulative_regular_volume_by_minute(bars)
        if cumulative:
            cumulative_by_minute_history.append(cumulative)

    return profiles


def build_day_profile(
    ticker: str,
    day: str,
    bars: list[Bar],
    previous_regular_close: float | None,
    day_volume_history: list[float],
    first_30m_volume_history: list[float],
    cumulative_history: list[list[float]],
) -> DayProfile:
    regular = [bar for bar in bars if is_regular(bar.timestamp_ny.time())]
    premarket = [bar for bar in bars if bar.timestamp_ny.time() < REGULAR_OPEN]
    first_30m = [bar for bar in regular if minutes_from_open(bar) < 30]

    regular_open = regular[0].open if regular else None
    regular_close = regular[-1].close if regular else None
    regular_high_bar = max(regular, key=lambda bar: bar.high, default=None)
    regular_low_bar = min(regular, key=lambda bar: bar.low, default=None)
    regular_high = regular_high_bar.high if regular_high_bar else None
    regular_low = regular_low_bar.low if regular_low_bar else None
    regular_volume = sum(bar.volume for bar in regular)
    first_30m_volume = sum(bar.volume for bar in first_30m)
    full_day_volume = sum(bar.volume for bar in bars)
    premarket_volume = sum(bar.volume for bar in premarket)

    rolling_avg_day_volume = safe_mean(day_volume_history)
    rolling_avg_first_30m_volume = safe_mean(first_30m_volume_history)
    relative_day_volume = safe_ratio(regular_volume, rolling_avg_day_volume)
    relative_first_30m_volume = safe_ratio(first_30m_volume, rolling_avg_first_30m_volume)
    peak_cumulative_rvol = compute_peak_cumulative_rvol(regular, cumulative_history)

    gap_pct = percent_change(previous_regular_close, regular_open)
    open_to_high_pct = percent_change(regular_open, regular_high)
    open_to_close_pct = percent_change(regular_open, regular_close)
    prev_close_to_high_pct = percent_change(previous_regular_close, regular_high)
    max_15m_gain_pct = max_window_return(regular, 15, high_side=True)
    max_30m_gain_pct = max_window_return(regular, 30, high_side=True)
    max_15m_loss_pct = max_window_return(regular, 15, high_side=False)
    opening_range_30m_pct = range_pct(first_30m)
    close_location_value = close_location(regular_high, regular_low, regular_close)
    max_intraday_drawdown_pct = max_drawdown_from_running_high(regular)

    intraday_runner_score_pct = max_or_none([open_to_high_pct, max_30m_gain_pct])
    previous_close_runner_score_pct = max_or_none([prev_close_to_high_pct])
    event_move_type = classify_event_move(gap_pct, intraday_runner_score_pct, prev_close_to_high_pct)
    data_quality_flag = classify_data_quality(
        gap_pct,
        intraday_runner_score_pct,
        prev_close_to_high_pct,
        regular_open,
        previous_regular_close,
        len(regular))

    return DayProfile(
        ticker=ticker,
        date=day,
        bars=len(bars),
        regular_bars=len(regular),
        extended_bars=len(bars) - len(regular),
        previous_regular_close=previous_regular_close,
        regular_open=regular_open,
        regular_close=regular_close,
        regular_high=regular_high,
        regular_low=regular_low,
        high_time_ny=format_time(regular_high_bar),
        low_time_ny=format_time(regular_low_bar),
        premarket_volume=premarket_volume,
        first_30m_volume=first_30m_volume,
        regular_volume=regular_volume,
        full_day_volume=full_day_volume,
        rolling_avg_day_volume=rolling_avg_day_volume,
        rolling_avg_first_30m_volume=rolling_avg_first_30m_volume,
        relative_day_volume=relative_day_volume,
        relative_first_30m_volume=relative_first_30m_volume,
        peak_cumulative_rvol=peak_cumulative_rvol,
        gap_pct=gap_pct,
        open_to_high_pct=open_to_high_pct,
        open_to_close_pct=open_to_close_pct,
        prev_close_to_high_pct=prev_close_to_high_pct,
        intraday_runner_score_pct=intraday_runner_score_pct,
        max_15m_gain_pct=max_15m_gain_pct,
        max_30m_gain_pct=max_30m_gain_pct,
        max_15m_loss_pct=max_15m_loss_pct,
        opening_range_30m_pct=opening_range_30m_pct,
        close_location_value=close_location_value,
        max_intraday_drawdown_pct=max_intraday_drawdown_pct,
        event_move_type=event_move_type,
        data_quality_flag=data_quality_flag,
        runner_10pct=(intraday_runner_score_pct or 0.0) >= 10.0,
        runner_25pct=(intraday_runner_score_pct or 0.0) >= 25.0,
        prev_close_runner_10pct=(previous_close_runner_score_pct or 0.0) >= 10.0,
        prev_close_runner_25pct=(previous_close_runner_score_pct or 0.0) >= 25.0,
    )


def is_regular(local_time: time) -> bool:
    return REGULAR_OPEN <= local_time <= REGULAR_CLOSE


def classify_event_move(
    gap_pct: float | None,
    intraday_runner_score_pct: float | None,
    prev_close_to_high_pct: float | None,
) -> str:
    gap = gap_pct or 0.0
    intraday = intraday_runner_score_pct or 0.0
    prior_close_move = prev_close_to_high_pct or 0.0

    if intraday >= 25.0:
        return "major_intraday_runner"
    if intraday >= 10.0:
        return "intraday_runner"
    if gap >= 10.0 and intraday >= 5.0:
        return "gap_with_follow_through"
    if prior_close_move >= 25.0:
        return "gap_only_or_adjustment_sensitive"
    if intraday <= 0.0 and gap < -5.0:
        return "gap_down_no_reclaim"
    return "normal"


def classify_data_quality(
    gap_pct: float | None,
    intraday_runner_score_pct: float | None,
    prev_close_to_high_pct: float | None,
    regular_open: float | None,
    previous_regular_close: float | None,
    regular_bar_count: int,
) -> str:
    if regular_bar_count < 30:
        return "thin_regular_session"

    gap = abs(gap_pct or 0.0)
    intraday = intraday_runner_score_pct or 0.0
    prior_close_move = prev_close_to_high_pct or 0.0

    if previous_regular_close is not None and previous_regular_close < 0.25:
        return "sub_25c_previous_close"
    if regular_open is not None and regular_open < 0.25:
        return "sub_25c_regular_open"
    if prior_close_move >= 200.0 and intraday < 10.0:
        return "prior_close_dislocation_check"
    if gap >= 200.0 and intraday < 10.0:
        return "overnight_gap_dislocation_check"
    return "ok"


def to_new_york(timestamp_utc: datetime) -> datetime:
    if NY is not None:
        return timestamp_utc.astimezone(NY)

    # Windows Python may not have the IANA tzdata package installed. This
    # fallback covers US Eastern market hours with the current DST rule:
    # second Sunday in March through first Sunday in November.
    eastern_date = timestamp_utc.date()
    offset_hours = -4 if is_us_eastern_dst(eastern_date) else -5
    return timestamp_utc.astimezone(timezone(timedelta(hours=offset_hours), "US/Eastern"))


def is_us_eastern_dst(day: date) -> bool:
    dst_start = nth_weekday(day.year, 3, 6, 2)
    dst_end = nth_weekday(day.year, 11, 6, 1)
    return dst_start <= day < dst_end


def nth_weekday(year: int, month: int, weekday: int, occurrence: int) -> date:
    first = date(year, month, 1)
    days_until_weekday = (weekday - first.weekday()) % 7
    return first + timedelta(days=days_until_weekday + (occurrence - 1) * 7)


def minutes_from_open(bar: Bar) -> int:
    local = bar.timestamp_ny
    return (local.hour * 60 + local.minute) - (REGULAR_OPEN.hour * 60 + REGULAR_OPEN.minute)


def cumulative_regular_volume_by_minute(bars: Iterable[Bar]) -> list[float]:
    cumulative: list[float] = []
    running = 0.0
    for bar in sorted((item for item in bars if is_regular(item.timestamp_ny.time())), key=lambda item: item.timestamp_utc):
        running += bar.volume
        cumulative.append(running)
    return cumulative


def compute_peak_cumulative_rvol(regular: list[Bar], history: list[list[float]]) -> float | None:
    if not regular or not history:
        return None

    cumulative = cumulative_regular_volume_by_minute(regular)
    ratios: list[float] = []
    for index, current in enumerate(cumulative):
        historical_values = [series[index] for series in history if len(series) > index and series[index] > 0]
        baseline = safe_mean(historical_values)
        ratio = safe_ratio(current, baseline)
        if ratio is not None:
            ratios.append(ratio)

    return max(ratios) if ratios else None


def max_window_return(regular: list[Bar], window_bars: int, high_side: bool) -> float | None:
    if len(regular) < 2:
        return None

    best = -math.inf if high_side else math.inf
    for start in range(0, len(regular) - 1):
        start_price = regular[start].open
        if start_price <= 0:
            continue
        end = min(len(regular), start + window_bars)
        window = regular[start:end]
        if high_side:
            value = percent_change(start_price, max(bar.high for bar in window))
            best = max(best, value if value is not None else -math.inf)
        else:
            value = percent_change(start_price, min(bar.low for bar in window))
            best = min(best, value if value is not None else math.inf)

    if high_side:
        return None if best == -math.inf else best
    return None if best == math.inf else best


def max_drawdown_from_running_high(regular: list[Bar]) -> float | None:
    if not regular:
        return None

    running_high = regular[0].high
    worst = 0.0
    for bar in regular:
        running_high = max(running_high, bar.high)
        if running_high > 0:
            worst = min(worst, ((bar.low / running_high) - 1.0) * 100.0)
    return worst


def range_pct(bars: list[Bar]) -> float | None:
    if not bars:
        return None

    low = min(bar.low for bar in bars)
    high = max(bar.high for bar in bars)
    return percent_change(low, high)


def close_location(high: float | None, low: float | None, close: float | None) -> float | None:
    if high is None or low is None or close is None or high <= low:
        return None
    return (close - low) / (high - low)


def safe_mean(values: Iterable[float]) -> float | None:
    cleaned = [value for value in values if value > 0 and math.isfinite(value)]
    return mean(cleaned) if cleaned else None


def safe_ratio(numerator: float | None, denominator: float | None) -> float | None:
    if numerator is None or denominator is None or denominator <= 0:
        return None
    return numerator / denominator


def percent_change(start: float | None, end: float | None) -> float | None:
    if start is None or end is None or start <= 0:
        return None
    return ((end / start) - 1.0) * 100.0


def format_time(bar: Bar | None) -> str:
    return "" if bar is None else bar.timestamp_ny.strftime("%H:%M")


def write_daily_profiles(path: Path, profiles: list[DayProfile]) -> None:
    fields = list(DayProfile.__dataclass_fields__)
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        for profile in profiles:
            writer.writerow({field: getattr(profile, field) for field in fields})


def build_summaries(profiles: list[DayProfile]) -> list[dict[str, object]]:
    summaries: list[dict[str, object]] = []
    by_ticker: dict[str, list[DayProfile]] = {}
    for profile in profiles:
        by_ticker.setdefault(profile.ticker, []).append(profile)

    for ticker, rows in sorted(by_ticker.items()):
        tradeable = [row for row in rows if row.regular_bars > 0]
        runner_days = [row for row in tradeable if row.runner_10pct]
        major_runner_days = [row for row in tradeable if row.runner_25pct]
        rvol_values = [row.relative_day_volume for row in tradeable if row.relative_day_volume is not None]
        first30_values = [row.relative_first_30m_volume for row in tradeable if row.relative_first_30m_volume is not None]
        peak_cum_values = [row.peak_cumulative_rvol for row in tradeable if row.peak_cumulative_rvol is not None]
        max_30m_values = [row.max_30m_gain_pct for row in tradeable if row.max_30m_gain_pct is not None]
        intraday_scores = [row.intraday_runner_score_pct for row in tradeable if row.intraday_runner_score_pct is not None]
        drawdowns = [row.max_intraday_drawdown_pct for row in tradeable if row.max_intraday_drawdown_pct is not None]
        summaries.append({
            "ticker": ticker,
            "sessions": len(tradeable),
            "runner_10pct_days": len(runner_days),
            "runner_25pct_days": len(major_runner_days),
            "prev_close_runner_10pct_days": sum(1 for row in tradeable if row.prev_close_runner_10pct),
            "data_quality_flag_days": sum(1 for row in tradeable if row.data_quality_flag != "ok"),
            "best_open_to_high_pct": max_or_none(row.open_to_high_pct for row in tradeable),
            "best_prev_close_to_high_pct": max_or_none(row.prev_close_to_high_pct for row in tradeable),
            "best_intraday_runner_score_pct": max_or_none(intraday_scores),
            "best_30m_gain_pct": max_or_none(max_30m_values),
            "median_relative_day_volume": median_or_none(rvol_values),
            "p90_relative_day_volume": percentile_or_none(rvol_values, 90),
            "median_first30m_rvol": median_or_none(first30_values),
            "p90_first30m_rvol": percentile_or_none(first30_values, 90),
            "median_peak_cumulative_rvol": median_or_none(peak_cum_values),
            "p90_peak_cumulative_rvol": percentile_or_none(peak_cum_values, 90),
            "worst_intraday_drawdown_pct": min_or_none(drawdowns),
            "latest_close": latest_close(tradeable),
        })

    return summaries


def write_summaries(path: Path, summaries: list[dict[str, object]]) -> None:
    fields = [
        "ticker",
        "sessions",
        "runner_10pct_days",
        "runner_25pct_days",
        "prev_close_runner_10pct_days",
        "data_quality_flag_days",
        "best_open_to_high_pct",
        "best_prev_close_to_high_pct",
        "best_intraday_runner_score_pct",
        "best_30m_gain_pct",
        "median_relative_day_volume",
        "p90_relative_day_volume",
        "median_first30m_rvol",
        "p90_first30m_rvol",
        "median_peak_cumulative_rvol",
        "p90_peak_cumulative_rvol",
        "worst_intraday_drawdown_pct",
        "latest_close",
    ]
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        for summary in summaries:
            writer.writerow(summary)


def write_report(
    path: Path,
    profiles: list[DayProfile],
    summaries: list[dict[str, object]],
    skipped: list[tuple[str, str]],
    candles_root: Path,
) -> None:
    top_runner_days = sorted(
        [row for row in profiles if row.intraday_runner_score_pct is not None],
        key=lambda row: row.intraday_runner_score_pct or -math.inf,
        reverse=True,
    )[:25]
    top_tickers_by_runner_count = sorted(
        summaries,
        key=lambda row: (int(row["runner_10pct_days"]), float(row["best_intraday_runner_score_pct"] or 0)),
        reverse=True,
    )[:20]
    top_first30_rvol = sorted(
        summaries,
        key=lambda row: float(row["p90_first30m_rvol"] or 0),
        reverse=True,
    )[:20]

    lines = [
        "# Intraday Data Profile",
        "",
        "This profile is built from cached 1-minute candles only. Volume baselines are no-lookahead: each session is compared with earlier sessions for the same ticker.",
        "",
        f"- Candle root: `{candles_root}`",
        f"- Tickers profiled: {len({row.ticker for row in profiles})}",
        f"- Sessions profiled: {len(profiles)}",
        f"- Clean 10% intraday runner sessions: {sum(1 for row in profiles if row.runner_10pct)}",
        f"- Clean 25% intraday runner sessions: {sum(1 for row in profiles if row.runner_25pct)}",
        f"- Prior-close 10% runner sessions: {sum(1 for row in profiles if row.prev_close_runner_10pct)}",
        f"- Data-quality flagged sessions: {sum(1 for row in profiles if row.data_quality_flag != 'ok')}",
        "",
    ]

    if skipped:
        lines.extend(["## Skipped Symbols", ""])
        for ticker, reason in skipped:
            lines.append(f"- `{ticker}`: {reason}")
        lines.append("")

    lines.extend(["## Top Runner Days", ""])
    lines.append("| Ticker | Date | Intraday Score % | Open To High % | Best 30m Gain % | Prev Close To High % | High Time NY | Day RVOL | First 30m RVOL | Peak Cum RVOL | Type | Quality |")
    lines.append("|---|---:|---:|---:|---:|---:|---|---:|---:|---:|---|---|")
    for row in top_runner_days:
        lines.append(
            f"| {row.ticker} | {row.date} | {fmt(row.intraday_runner_score_pct)} | {fmt(row.open_to_high_pct)} | "
            f"{fmt(row.max_30m_gain_pct)} | {fmt(row.prev_close_to_high_pct)} | "
            f"{row.high_time_ny} | {fmt(row.relative_day_volume)} | {fmt(row.relative_first_30m_volume)} | "
            f"{fmt(row.peak_cumulative_rvol)} | {row.event_move_type} | {row.data_quality_flag} |"
        )

    lines.extend(["", "## Most Frequent 10% Runner Tickers", ""])
    lines.append("| Ticker | Sessions | Clean 10% Days | Clean 25% Days | Prior-Close 10% Days | Quality Flags | Best Intraday Score % | P90 First 30m RVOL | P90 Peak Cum RVOL |")
    lines.append("|---|---:|---:|---:|---:|---:|---:|---:|---:|")
    for row in top_tickers_by_runner_count:
        lines.append(
            f"| {row['ticker']} | {row['sessions']} | {row['runner_10pct_days']} | {row['runner_25pct_days']} | "
            f"{row['prev_close_runner_10pct_days']} | {row['data_quality_flag_days']} | "
            f"{fmt(row['best_intraday_runner_score_pct'])} | {fmt(row['p90_first30m_rvol'])} | {fmt(row['p90_peak_cumulative_rvol'])} |"
        )

    lines.extend(["", "## Strongest Early Volume Profiles", ""])
    lines.append("| Ticker | P90 First 30m RVOL | Median First 30m RVOL | Runner Days | Best 30m Gain % | Worst Intraday DD % |")
    lines.append("|---|---:|---:|---:|---:|---:|")
    for row in top_first30_rvol:
        lines.append(
            f"| {row['ticker']} | {fmt(row['p90_first30m_rvol'])} | {fmt(row['median_first30m_rvol'])} | "
            f"{row['runner_10pct_days']} | {fmt(row['best_30m_gain_pct'])} | {fmt(row['worst_intraday_drawdown_pct'])} |"
        )

    lines.extend([
        "",
        "## Design Implication",
        "",
        "- Use `relative_day_volume`, `relative_first_30m_volume`, and `peak_cumulative_rvol` as profile/scoring inputs, not one isolated bar gate.",
        "- Use `intraday_runner_score_pct` for day-trade research. Treat `prev_close_to_high_pct` as event/gap context, not the main runner score.",
        "- Review rows flagged `prior_close_dislocation_check` or `overnight_gap_dislocation_check` before training or optimizing against prior-close returns.",
        "- A hard `min_volume_spike` should only be a liquidity floor. The actual decision should combine early volume, price location, VWAP/EMA structure, and MACD.",
        "- For POET/RGTI-style names, optimize exits after separating days with real early participation from days with late/noisy participation.",
        "",
    ])

    path.write_text("\n".join(lines), encoding="utf-8")


def max_or_none(values: Iterable[float | None]) -> float | None:
    cleaned = [value for value in values if value is not None and math.isfinite(value)]
    return max(cleaned) if cleaned else None


def min_or_none(values: Iterable[float | None]) -> float | None:
    cleaned = [value for value in values if value is not None and math.isfinite(value)]
    return min(cleaned) if cleaned else None


def median_or_none(values: Iterable[float | None]) -> float | None:
    cleaned = [value for value in values if value is not None and math.isfinite(value)]
    return median(cleaned) if cleaned else None


def percentile_or_none(values: Iterable[float | None], percentile: float) -> float | None:
    cleaned = sorted(value for value in values if value is not None and math.isfinite(value))
    if not cleaned:
        return None
    if len(cleaned) == 1:
        return cleaned[0]
    position = (len(cleaned) - 1) * (percentile / 100.0)
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return cleaned[int(position)]
    weight = position - lower
    return cleaned[lower] * (1 - weight) + cleaned[upper] * weight


def latest_close(rows: list[DayProfile]) -> float | None:
    for row in reversed(rows):
        if row.regular_close is not None:
            return row.regular_close
    return None


def fmt(value: object) -> str:
    if value is None:
        return ""
    if isinstance(value, float):
        if math.isnan(value) or math.isinf(value):
            return ""
        return f"{value:.2f}"
    return str(value)


if __name__ == "__main__":
    raise SystemExit(main())
