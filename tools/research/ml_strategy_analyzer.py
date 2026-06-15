#!/usr/bin/env python3
"""
Dependency-free research analyzer for TradingFlow backtest results.

This is intentionally not a trading model. It is a microscope for asking:
which features correlate with winning trades, which exits lose money, and which
volume definitions help or hurt. The script reads backtest JSON, optionally
reconstructs entry-time indicators from normalized 1m candles, and writes CSV/MD
reports that can guide the next deterministic strategy config.
"""

from __future__ import annotations

import argparse
import csv
import glob
import json
import math
import statistics
from collections import defaultdict
from dataclasses import dataclass
from datetime import date, datetime, time, timedelta, timezone
from pathlib import Path
from typing import Any, Iterable


try:
    from zoneinfo import ZoneInfo
except ImportError:  # pragma: no cover - Python 3.9+ includes zoneinfo.
    ZoneInfo = None  # type: ignore[assignment]

try:
    NY = ZoneInfo("America/New_York") if ZoneInfo is not None else None
except Exception:
    NY = None

REGULAR_OPEN_NY = time(9, 30)
REGULAR_CLOSE_NY = time(16, 0)
LOOKBACK_SESSIONS = 63
LEAKY_OR_OUTCOME_COLUMNS = {
    "is_win",
    "net_profit",
    "realized_r",
    "return_pct",
    "exit_price",
    "exit_reason",
    "exit_timestamp",
    "holding_minutes",
    "share_quantity",
    "stop_loss_price",
    "take_profit_price",
}

FEATURE_DEFINITIONS: dict[str, tuple[str, str, str]] = {
    "entry_price": ("market", "Execution price at entry; known at entry.", "entry"),
    "entry_session_minute": ("time", "Minutes from 09:30 New York regular open; premarket values are negative.", "entry"),
    "session_gain_to_entry_pct": ("price", "Entry close versus first bar of the New York market date, using only bars up to entry.", "entry"),
    "entry_close_location": ("price", "Entry bar close location inside its own high/low range, 0=low and 1=high.", "entry"),
    "entry_bar_range_pct": ("price", "Entry bar high-low range divided by entry close.", "entry"),
    "entry_bar_body_pct": ("price", "Absolute entry bar body divided by entry close.", "entry"),
    "entry_vwap_distance_pct": ("trend", "Entry close distance from session VWAP computed only through the entry bar.", "entry"),
    "entry_ema10_distance_pct": ("trend", "Entry close distance from EMA10 computed only through the entry bar.", "entry"),
    "entry_ema20_distance_pct": ("trend", "Entry close distance from EMA20 computed only through the entry bar.", "entry"),
    "entry_ema50_distance_pct": ("trend", "Entry close distance from EMA50 computed only through the entry bar.", "entry"),
    "ema10_minus_ema20_pct": ("trend", "EMA10 distance above/below EMA20 at entry.", "entry"),
    "macd_histogram": ("momentum", "MACD histogram computed from closes available through entry.", "entry"),
    "atr_pct": ("volatility", "ATR14 through entry divided by entry close.", "entry"),
    "bar_volume": ("volume", "Raw entry-bar volume.", "entry"),
    "volume_sma5": ("volume", "Average volume of the latest five bars ending at entry.", "entry"),
    "volume_sma5_rise_pct": ("volume", "Current volume SMA5 versus the preceding comparable SMA5 window.", "entry"),
    "slot_rvol": ("volume", "Entry-bar volume versus prior sessions' same local-minute bar volume.", "entry_prior_history"),
    "same_time_cumulative_rvol": ("volume", "Cumulative current-day volume through entry versus prior sessions through the same local minute.", "entry_prior_history"),
    "session_vs_average_day_rvol": ("volume", "Cumulative current-day volume through entry versus prior sessions' full-day volume.", "entry_prior_history"),
    "entry_cumulative_volume": ("volume", "Cumulative volume from market-date start through entry.", "entry"),
    "average_cumulative_same_time": ("volume", "Prior-session average cumulative volume through the entry local minute.", "prior_history"),
    "average_slot_volume": ("volume", "Prior-session average volume for the entry local minute.", "prior_history"),
    "average_session_volume": ("volume", "Prior-session average full market-date volume.", "prior_history"),
    "prior_session_samples": ("data_quality", "Number of prior market dates used for rolling baselines.", "prior_history"),
    "profile_prior_sessions": ("profile", "Prior daily-profile rows available before the entry date.", "prior_history"),
    "profile_prior_20d_runner10_rate": ("profile", "Share of prior profile sessions with clean intraday runner score >= 10%.", "prior_history"),
    "profile_prior_20d_runner25_rate": ("profile", "Share of prior profile sessions with clean intraday runner score >= 25%.", "prior_history"),
    "profile_prior_20d_quality_flag_rate": ("profile", "Share of prior profile sessions with any non-ok data-quality flag.", "prior_history"),
    "profile_prior_20d_gap_context_rate": ("profile", "Share of prior profile sessions where prior-close move was a gap/context event.", "prior_history"),
    "profile_prior_20d_median_intraday_score_pct": ("profile", "Median clean intraday runner score across prior profile sessions.", "prior_history"),
    "profile_prior_20d_p90_intraday_score_pct": ("profile", "90th percentile clean intraday runner score across prior profile sessions.", "prior_history"),
    "profile_prior_20d_median_first30m_rvol": ("profile", "Median prior first-30-minute RVOL from no-lookahead daily profile.", "prior_history"),
    "profile_prior_20d_p90_peak_cumulative_rvol": ("profile", "90th percentile prior peak cumulative RVOL from no-lookahead daily profile.", "prior_history"),
    "profile_prior_20d_median_day_rvol": ("profile", "Median prior full-day RVOL from no-lookahead daily profile.", "prior_history"),
    "profile_prior_20d_median_open_to_close_pct": ("profile", "Median prior open-to-close percentage move.", "prior_history"),
}


@dataclass(frozen=True)
class Bar:
    ticker: str
    timestamp: datetime
    open: float
    high: float
    low: float
    close: float
    volume: float


def parse_timestamp(value: str) -> datetime:
    cleaned = value.replace("Z", "+00:00")
    return datetime.fromisoformat(cleaned).astimezone(timezone.utc)


def parse_float(value: Any, default: float = math.nan) -> float:
    try:
        if value is None or value == "":
            return default
        return float(value)
    except (TypeError, ValueError):
        return default


def to_new_york(timestamp_utc: datetime) -> datetime:
    if NY is not None:
        return timestamp_utc.astimezone(NY)

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


def regular_session(bar: Bar) -> bool:
    tod = to_new_york(bar.timestamp).time()
    return REGULAR_OPEN_NY <= tod <= REGULAR_CLOSE_NY


def market_date(bar: Bar) -> str:
    return to_new_york(bar.timestamp).date().isoformat()


def local_minute_of_day(timestamp: datetime) -> int:
    local = to_new_york(timestamp)
    return local.hour * 60 + local.minute


def minutes_from_regular_open(timestamp: datetime) -> int:
    return local_minute_of_day(timestamp) - (REGULAR_OPEN_NY.hour * 60 + REGULAR_OPEN_NY.minute)


def load_result(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def load_bars(normalized_roots: list[Path], ticker: str) -> list[Bar]:
    path = next((root / ticker / "bars_1m.csv" for root in normalized_roots if (root / ticker / "bars_1m.csv").exists()), None)
    if path is None:
        return []

    bars: list[Bar] = []
    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        for row in csv.DictReader(handle):
            bars.append(
                Bar(
                    ticker=row.get("ticker", ticker),
                    timestamp=parse_timestamp(row["timestamp"]),
                    open=parse_float(row["open"]),
                    high=parse_float(row["high"]),
                    low=parse_float(row["low"]),
                    close=parse_float(row["close"]),
                    volume=parse_float(row["volume"], 0.0),
                )
            )
    return sorted(bars, key=lambda bar: bar.timestamp)


def group_market_date_bars(bars: Iterable[Bar]) -> dict[str, list[Bar]]:
    grouped: dict[str, list[Bar]] = defaultdict(list)
    for bar in bars:
        grouped[market_date(bar)].append(bar)
    return {date: sorted(items, key=lambda bar: bar.timestamp) for date, items in grouped.items()}


def ema(values: list[float], period: int) -> float:
    if not values:
        return math.nan
    multiplier = 2.0 / (period + 1)
    current = values[0]
    for value in values[1:]:
        current = (value - current) * multiplier + current
    return current


def macd_histogram(values: list[float]) -> float:
    if len(values) < 35:
        return math.nan
    macd_values: list[float] = []
    for index in range(len(values)):
        prefix = values[: index + 1]
        if len(prefix) >= 26:
            macd_values.append(ema(prefix, 12) - ema(prefix, 26))
    if len(macd_values) < 9:
        return math.nan
    macd_line = macd_values[-1]
    signal = ema(macd_values, 9)
    return macd_line - signal


def atr(bars: list[Bar], period: int = 14) -> float:
    if len(bars) < period + 1:
        return math.nan
    true_ranges: list[float] = []
    for index in range(1, len(bars)):
        bar = bars[index]
        prev = bars[index - 1]
        true_ranges.append(max(bar.high - bar.low, abs(bar.high - prev.close), abs(bar.low - prev.close)))
    return statistics.mean(true_ranges[-period:])


def find_entry_index(day_bars: list[Bar], entry_time: datetime) -> int | None:
    for index, bar in enumerate(day_bars):
        if bar.timestamp >= entry_time:
            return index
    return None


def prior_sessions(grouped: dict[str, list[Bar]], entry_date: str) -> list[list[Bar]]:
    dates = sorted(date for date in grouped if date < entry_date)
    return [grouped[date] for date in dates[-LOOKBACK_SESSIONS:]]


def cumulative_volume_until(bars: list[Bar], index: int) -> float:
    return sum(bar.volume for bar in bars[: index + 1])


def cumulative_volume_until_local_minute(bars: list[Bar], local_minute: int) -> float:
    return sum(bar.volume for bar in bars if local_minute_of_day(bar.timestamp) <= local_minute)


def volume_at_local_minute(bars: list[Bar], local_minute: int) -> float:
    return sum(bar.volume for bar in bars if local_minute_of_day(bar.timestamp) == local_minute)


def session_vwap_until(bars: list[Bar], index: int) -> float:
    pv = 0.0
    volume = 0.0
    for bar in bars[: index + 1]:
        typical = (bar.high + bar.low + bar.close) / 3.0
        pv += typical * bar.volume
        volume += bar.volume
    return pv / volume if volume > 0 else math.nan


def safe_ratio(numerator: float, denominator: float) -> float:
    return numerator / denominator if denominator and not math.isnan(denominator) else math.nan


def mean_or_nan(values: list[float]) -> float:
    clean = [value for value in values if not math.isnan(value)]
    return statistics.mean(clean) if clean else math.nan


def percentile(values: list[float], pct: float) -> float:
    clean = sorted(value for value in values if not math.isnan(value))
    if not clean:
        return math.nan
    position = (len(clean) - 1) * pct
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return clean[int(position)]
    return clean[lower] * (upper - position) + clean[upper] * (position - lower)


def compute_entry_features(ticker_bars: list[Bar], trade: dict[str, Any]) -> dict[str, float]:
    grouped = group_market_date_bars(ticker_bars)
    entry_time = parse_timestamp(trade["EntryTimestamp"])
    entry_date = to_new_york(entry_time).date().isoformat()
    day_bars = grouped.get(entry_date, [])
    entry_index = find_entry_index(day_bars, entry_time)
    if entry_index is None or not day_bars:
        return {}

    entry_bar = day_bars[entry_index]
    entry_local_minute = local_minute_of_day(entry_bar.timestamp)
    bars_to_entry = day_bars[: entry_index + 1]
    closes = [bar.close for bar in bars_to_entry]
    volumes = [bar.volume for bar in bars_to_entry]
    prior = prior_sessions(grouped, entry_date)

    same_slot_cumulative = []
    same_slot_volume = []
    full_session_volume = []
    for session in prior:
        cumulative = cumulative_volume_until_local_minute(session, entry_local_minute)
        if cumulative > 0:
            same_slot_cumulative.append(cumulative)
        slot_volume = volume_at_local_minute(session, entry_local_minute)
        if slot_volume > 0:
            same_slot_volume.append(slot_volume)
        if session:
            full_session_volume.append(sum(bar.volume for bar in session))

    day_open = day_bars[0].open
    current_cumulative_volume = cumulative_volume_until(day_bars, entry_index)
    average_cumulative_same_time = mean_or_nan(same_slot_cumulative)
    average_slot_volume = mean_or_nan(same_slot_volume)
    average_session_volume = mean_or_nan(full_session_volume)
    vwap = session_vwap_until(day_bars, entry_index)
    ema10 = ema(closes, 10)
    ema20 = ema(closes, 20)
    ema50 = ema(closes, 50)
    volume_sma5 = mean_or_nan(volumes[-5:])
    prior_volume_sma5 = mean_or_nan(volumes[-8:-3]) if len(volumes) >= 8 else math.nan

    return {
        "entry_session_minute": float(minutes_from_regular_open(entry_bar.timestamp)),
        "session_gain_to_entry_pct": safe_ratio(entry_bar.close - day_open, day_open) * 100.0,
        "entry_close_location": safe_ratio(entry_bar.close - entry_bar.low, entry_bar.high - entry_bar.low),
        "entry_bar_range_pct": safe_ratio(entry_bar.high - entry_bar.low, entry_bar.close) * 100.0,
        "entry_bar_body_pct": safe_ratio(abs(entry_bar.close - entry_bar.open), entry_bar.close) * 100.0,
        "entry_vwap_distance_pct": safe_ratio(entry_bar.close - vwap, vwap) * 100.0,
        "entry_ema10_distance_pct": safe_ratio(entry_bar.close - ema10, ema10) * 100.0,
        "entry_ema20_distance_pct": safe_ratio(entry_bar.close - ema20, ema20) * 100.0,
        "entry_ema50_distance_pct": safe_ratio(entry_bar.close - ema50, ema50) * 100.0,
        "ema10_minus_ema20_pct": safe_ratio(ema10 - ema20, ema20) * 100.0,
        "macd_histogram": macd_histogram(closes),
        "atr_pct": safe_ratio(atr(bars_to_entry), entry_bar.close) * 100.0,
        "bar_volume": entry_bar.volume,
        "volume_sma5": volume_sma5,
        "volume_sma5_rise_pct": safe_ratio(volume_sma5 - prior_volume_sma5, prior_volume_sma5) * 100.0,
        "slot_rvol": safe_ratio(entry_bar.volume, average_slot_volume),
        "same_time_cumulative_rvol": safe_ratio(current_cumulative_volume, average_cumulative_same_time),
        "session_vs_average_day_rvol": safe_ratio(current_cumulative_volume, average_session_volume),
        "entry_cumulative_volume": current_cumulative_volume,
        "average_cumulative_same_time": average_cumulative_same_time,
        "average_slot_volume": average_slot_volume,
        "average_session_volume": average_session_volume,
        "prior_session_samples": float(len(prior)),
    }


def load_daily_profile(path: Path | None) -> dict[str, list[dict[str, Any]]]:
    if path is None or not path.exists():
        return {}

    grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        for row in csv.DictReader(handle):
            ticker = str(row.get("ticker", "")).upper()
            if not ticker or not row.get("date"):
                continue
            grouped[ticker].append(row)

    return {
        ticker: sorted(rows, key=lambda row: str(row.get("date", "")))
        for ticker, rows in grouped.items()
    }


def profile_history_features(
    daily_profiles: dict[str, list[dict[str, Any]]],
    ticker: str,
    entry_date: str,
    lookback_days: int,
) -> dict[str, float]:
    rows = [
        row for row in daily_profiles.get(ticker, [])
        if str(row.get("date", "")) < entry_date
    ][-lookback_days:]
    if not rows:
        return {}

    intraday_scores = [parse_float(row.get("intraday_runner_score_pct")) for row in rows]
    first30_rvol = [parse_float(row.get("relative_first_30m_volume")) for row in rows]
    peak_cumulative_rvol = [parse_float(row.get("peak_cumulative_rvol")) for row in rows]
    day_rvol = [parse_float(row.get("relative_day_volume")) for row in rows]
    open_to_close = [parse_float(row.get("open_to_close_pct")) for row in rows]
    quality_flag_count = sum(
        1 for row in rows
        if str(row.get("data_quality_flag", "ok")) != "ok"
    )
    gap_context_count = sum(
        1 for row in rows
        if str(row.get("event_move_type", "")) == "gap_only_or_adjustment_sensitive"
    )
    runner10_count = sum(1 for row in rows if truthy(row.get("runner_10pct")))
    runner25_count = sum(1 for row in rows if truthy(row.get("runner_25pct")))

    return {
        "profile_prior_sessions": float(len(rows)),
        "profile_prior_20d_runner10_rate": runner10_count / len(rows),
        "profile_prior_20d_runner25_rate": runner25_count / len(rows),
        "profile_prior_20d_quality_flag_rate": quality_flag_count / len(rows),
        "profile_prior_20d_gap_context_rate": gap_context_count / len(rows),
        "profile_prior_20d_median_intraday_score_pct": median_or_nan(intraday_scores),
        "profile_prior_20d_p90_intraday_score_pct": percentile(intraday_scores, 0.90),
        "profile_prior_20d_median_first30m_rvol": median_or_nan(first30_rvol),
        "profile_prior_20d_p90_peak_cumulative_rvol": percentile(peak_cumulative_rvol, 0.90),
        "profile_prior_20d_median_day_rvol": median_or_nan(day_rvol),
        "profile_prior_20d_median_open_to_close_pct": median_or_nan(open_to_close),
    }


def truthy(value: Any) -> bool:
    return str(value).strip().lower() in {"true", "1", "yes", "y"}


def median_or_nan(values: list[float]) -> float:
    clean = [value for value in values if not math.isnan(value)]
    return statistics.median(clean) if clean else math.nan


def trade_rows(
    results: list[tuple[Path, dict[str, Any]]],
    normalized_roots: list[Path],
    daily_profiles: dict[str, list[dict[str, Any]]],
    profile_lookback_days: int,
) -> list[dict[str, Any]]:
    bars_cache: dict[str, list[Bar]] = {}
    rows: list[dict[str, Any]] = []

    for path, result in results:
        run_name = result.get("RunName", path.stem)
        for trade in result.get("CompletedTrades", []):
            ticker = str(trade.get("Ticker", "")).upper()
            direction = str(trade.get("Direction", "long")).lower()
            entry_price = parse_float(trade.get("EntryPrice"))
            exit_price = parse_float(trade.get("ExitPrice"))
            qty = parse_float(trade.get("ShareQuantity"), 0.0)
            stop = parse_float(trade.get("StopLossPrice"))
            net_profit = parse_float(trade.get("NetProfit"), 0.0)
            risk_dollars = abs(entry_price - stop) * qty if not math.isnan(stop) else math.nan
            raw_return = safe_ratio(exit_price - entry_price, entry_price) * 100.0
            signed_return = -raw_return if direction == "short" else raw_return
            entry = parse_timestamp(trade["EntryTimestamp"])
            exit_time = parse_timestamp(trade["ExitTimestamp"])

            row: dict[str, Any] = {
                "run_name": run_name,
                "result_file": path.name,
                "strategy_name": trade.get("StrategyName", ""),
                "ticker": ticker,
                "direction": direction,
                "entry_timestamp": trade.get("EntryTimestamp", ""),
                "exit_timestamp": trade.get("ExitTimestamp", ""),
                "holding_minutes": (exit_time - entry).total_seconds() / 60.0,
                "entry_price": entry_price,
                "exit_price": exit_price,
                "share_quantity": qty,
                "stop_loss_price": stop,
                "take_profit_price": parse_float(trade.get("TakeProfitPrice")),
                "exit_reason": trade.get("ExitReason", ""),
                "net_profit": net_profit,
                "return_pct": signed_return,
                "realized_r": safe_ratio(net_profit, risk_dollars),
                "is_win": 1.0 if net_profit > 0 else 0.0,
            }

            if normalized_roots:
                if ticker not in bars_cache:
                    bars_cache[ticker] = load_bars(normalized_roots, ticker)
                row.update(compute_entry_features(bars_cache[ticker], trade))
            if daily_profiles:
                row.update(profile_history_features(
                    daily_profiles,
                    ticker,
                    to_new_york(entry).date().isoformat(),
                    profile_lookback_days))

            rows.append(row)
    return rows


def strategy_summary(results: list[tuple[Path, dict[str, Any]]]) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for path, result in results:
        for strategy in result.get("StrategyResults", []):
            rows.append(
                {
                    "run_name": result.get("RunName", path.stem),
                    "result_file": path.name,
                    "strategy_name": strategy.get("StrategyName", ""),
                    "total_return_pct": parse_float(strategy.get("TotalReturnPct")),
                    "net_profit": parse_float(strategy.get("NetProfit")),
                    "max_drawdown_pct": parse_float(strategy.get("MaxDrawdownPct")),
                    "accepted_trades": parse_float(strategy.get("AcceptedTradeCount"), 0.0),
                    "rejected_trades": parse_float(strategy.get("RejectedTradeCount"), 0.0),
                    "winning_trades": parse_float(strategy.get("WinningTradeCount"), 0.0),
                    "losing_trades": parse_float(strategy.get("LosingTradeCount"), 0.0),
                    "win_rate_pct": safe_ratio(
                        parse_float(strategy.get("WinningTradeCount"), 0.0),
                        parse_float(strategy.get("AcceptedTradeCount"), 0.0),
                    )
                    * 100.0,
                }
            )
    return rows


def numeric_columns(rows: list[dict[str, Any]]) -> list[str]:
    columns: set[str] = set()
    for row in rows:
        for key, value in row.items():
            if isinstance(value, (int, float)) and not math.isnan(float(value)):
                columns.add(key)
    return sorted(columns)


def pearson(xs: list[float], ys: list[float]) -> float:
    pairs = [(x, y) for x, y in zip(xs, ys) if not math.isnan(x) and not math.isnan(y)]
    if len(pairs) < 3:
        return math.nan
    x_values, y_values = zip(*pairs)
    x_mean = statistics.mean(x_values)
    y_mean = statistics.mean(y_values)
    numerator = sum((x - x_mean) * (y - y_mean) for x, y in pairs)
    x_den = math.sqrt(sum((x - x_mean) ** 2 for x in x_values))
    y_den = math.sqrt(sum((y - y_mean) ** 2 for y in y_values))
    return numerator / (x_den * y_den) if x_den > 0 and y_den > 0 else math.nan


def feature_correlations(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    targets = ["is_win", "net_profit", "realized_r", "return_pct"]
    output: list[dict[str, Any]] = []
    for feature in numeric_columns(rows):
        if feature in LEAKY_OR_OUTCOME_COLUMNS:
            continue
        xs = [parse_float(row.get(feature)) for row in rows]
        for target in targets:
            ys = [parse_float(row.get(target)) for row in rows]
            corr = pearson(xs, ys)
            if not math.isnan(corr):
                output.append(
                    {
                        "feature": feature,
                        "target": target,
                        "pearson": corr,
                        "abs_pearson": abs(corr),
                        "sample_count": sum(1 for x, y in zip(xs, ys) if not math.isnan(x) and not math.isnan(y)),
                    }
                )
    return sorted(output, key=lambda row: row["abs_pearson"], reverse=True)


def validate_feature_rows(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    diagnostics: list[dict[str, Any]] = []
    numeric = numeric_columns(rows)
    feature_columns = [column for column in numeric if column not in LEAKY_OR_OUTCOME_COLUMNS]
    for feature in feature_columns:
        values = [parse_float(row.get(feature)) for row in rows]
        present = [value for value in values if not math.isnan(value)]
        diagnostics.append({
            "feature": feature,
            "rows": len(rows),
            "present": len(present),
            "missing": len(rows) - len(present),
            "coverage_pct": safe_ratio(len(present), len(rows)) * 100.0,
            "min": min(present) if present else math.nan,
            "max": max(present) if present else math.nan,
            "mean": statistics.mean(present) if present else math.nan,
            "category": FEATURE_DEFINITIONS.get(feature, ("unknown", "", ""))[0],
            "availability": FEATURE_DEFINITIONS.get(feature, ("", "", "unknown"))[2],
            "definition": FEATURE_DEFINITIONS.get(feature, ("", "Feature is not documented yet.", ""))[1],
        })
    return sorted(diagnostics, key=lambda row: (row["availability"], row["category"], row["feature"]))


def feature_manifest_rows(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    columns = [column for column in numeric_columns(rows) if column not in LEAKY_OR_OUTCOME_COLUMNS]
    output: list[dict[str, Any]] = []
    for column in columns:
        category, definition, availability = FEATURE_DEFINITIONS.get(
            column,
            ("unknown", "Feature is not documented yet.", "unknown"))
        output.append({
            "feature": column,
            "category": category,
            "availability": availability,
            "is_leaky_or_outcome": False,
            "definition": definition,
        })
    for column in sorted(LEAKY_OR_OUTCOME_COLUMNS):
        output.append({
            "feature": column,
            "category": "label_or_outcome",
            "availability": "after_exit",
            "is_leaky_or_outcome": True,
            "definition": "Outcome/label column. Never use as an ML input feature.",
        })
    return output


def feature_buckets(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    output: list[dict[str, Any]] = []
    targets = ["net_profit", "is_win", "realized_r"]
    for feature in numeric_columns(rows):
        if feature in LEAKY_OR_OUTCOME_COLUMNS:
            continue
        values = [parse_float(row.get(feature)) for row in rows]
        cuts = [percentile(values, pct) for pct in (0.25, 0.5, 0.75)]
        if any(math.isnan(cut) for cut in cuts) or len(set(cuts)) < 3:
            continue
        buckets: dict[str, list[dict[str, Any]]] = {"q1": [], "q2": [], "q3": [], "q4": []}
        for row in rows:
            value = parse_float(row.get(feature))
            if math.isnan(value):
                continue
            if value <= cuts[0]:
                buckets["q1"].append(row)
            elif value <= cuts[1]:
                buckets["q2"].append(row)
            elif value <= cuts[2]:
                buckets["q3"].append(row)
            else:
                buckets["q4"].append(row)
        for bucket, bucket_rows in buckets.items():
            if not bucket_rows:
                continue
            output.append(
                {
                    "feature": feature,
                    "bucket": bucket,
                    "min": min(parse_float(row.get(feature)) for row in bucket_rows),
                    "max": max(parse_float(row.get(feature)) for row in bucket_rows),
                    "count": len(bucket_rows),
                    "win_rate_pct": statistics.mean(parse_float(row.get("is_win"), 0.0) for row in bucket_rows) * 100.0,
                    "avg_net_profit": statistics.mean(parse_float(row.get("net_profit"), 0.0) for row in bucket_rows),
                    "avg_realized_r": mean_or_nan([parse_float(row.get("realized_r")) for row in bucket_rows]),
                }
            )
    return output


def write_csv(path: Path, rows: list[dict[str, Any]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if not rows:
        path.write_text("", encoding="utf-8")
        return
    columns = sorted({key for row in rows for key in row.keys()})
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=columns)
        writer.writeheader()
        for row in rows:
            writer.writerow(row)


def write_markdown(
    path: Path,
    summaries: list[dict[str, Any]],
    correlations: list[dict[str, Any]],
    buckets: list[dict[str, Any]],
    diagnostics: list[dict[str, Any]],
) -> None:
    lines = [
        "# ML Research Analyzer Report",
        "",
        "This report is descriptive research only. It ranks correlations from completed backtest trades and should feed deterministic strategy changes, not live trading decisions.",
        "",
        "## Strategy Ranking",
        "",
        "| Strategy | Run | Return % | Net Profit | Max DD % | Trades | Win % |",
        "|---|---|---:|---:|---:|---:|---:|",
    ]
    for row in sorted(summaries, key=lambda item: (parse_float(item.get("net_profit")), -parse_float(item.get("max_drawdown_pct"))), reverse=True)[:25]:
        lines.append(
            f"| {row['strategy_name']} | {row['run_name']} | {parse_float(row['total_return_pct']):.2f} | "
            f"{parse_float(row['net_profit']):.2f} | {parse_float(row['max_drawdown_pct']):.2f} | "
            f"{parse_float(row['accepted_trades']):.0f} | {parse_float(row['win_rate_pct']):.1f} |"
        )

    lines.extend(["", "## Top Feature Correlations", "", "| Feature | Target | Pearson | Samples |", "|---|---|---:|---:|"])
    for row in correlations[:25]:
        lines.append(f"| {row['feature']} | {row['target']} | {row['pearson']:.3f} | {row['sample_count']} |")

    lines.extend(["", "## Feature Coverage", "", "| Feature | Availability | Coverage % | Min | Max | Definition |", "|---|---|---:|---:|---:|---|"])
    for row in sorted(diagnostics, key=lambda item: parse_float(item.get("coverage_pct")), reverse=True)[:35]:
        lines.append(
            f"| {row['feature']} | {row['availability']} | {parse_float(row['coverage_pct']):.1f} | "
            f"{parse_float(row['min']):.3f} | {parse_float(row['max']):.3f} | {row['definition']} |"
        )

    lines.extend(["", "## Best Buckets By Avg Net Profit", "", "| Feature | Bucket | Range | Count | Win % | Avg Net | Avg R |", "|---|---|---|---:|---:|---:|---:|"])
    for row in sorted(buckets, key=lambda item: parse_float(item.get("avg_net_profit")), reverse=True)[:25]:
        lines.append(
            f"| {row['feature']} | {row['bucket']} | {parse_float(row['min']):.3f}..{parse_float(row['max']):.3f} | "
            f"{row['count']} | {parse_float(row['win_rate_pct']):.1f} | {parse_float(row['avg_net_profit']):.2f} | {parse_float(row['avg_realized_r']):.2f} |"
        )

    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def expand_paths(patterns: list[str]) -> list[Path]:
    paths: list[Path] = []
    for pattern in patterns:
        matches = glob.glob(pattern)
        if matches:
            paths.extend(Path(match) for match in matches)
        else:
            paths.append(Path(pattern))
    return sorted(set(path for path in paths if path.exists()))


def main() -> int:
    parser = argparse.ArgumentParser(description="Analyze TradingFlow backtest results for strategy/feature correlations.")
    parser.add_argument("--results", nargs="+", required=True, help="Result JSON path(s) or glob(s).")
    parser.add_argument("--normalized-root", nargs="*", default=[], help="Optional normalized candle root(s) containing TICKER/bars_1m.csv.")
    parser.add_argument("--daily-profile", default="", help="Optional ticker_day_profile.csv from intraday_data_profile.py.")
    parser.add_argument("--profile-lookback-days", type=int, default=20, help="Prior profile rows used for no-lookahead profile features.")
    parser.add_argument("--output-dir", required=True, help="Directory for CSV and markdown reports.")
    args = parser.parse_args()

    result_paths = expand_paths(args.results)
    if not result_paths:
        raise SystemExit("No result JSON files matched --results.")

    results = [(path, load_result(path)) for path in result_paths]
    normalized_roots = [Path(root) for root in args.normalized_root]
    daily_profiles = load_daily_profile(Path(args.daily_profile)) if args.daily_profile else {}
    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    summaries = strategy_summary(results)
    trades = trade_rows(results, normalized_roots, daily_profiles, args.profile_lookback_days)
    correlations = feature_correlations(trades)
    buckets = feature_buckets(trades)
    diagnostics = validate_feature_rows(trades)
    manifest = feature_manifest_rows(trades)

    write_csv(output_dir / "strategy_summary.csv", summaries)
    write_csv(output_dir / "trade_features.csv", trades)
    write_csv(output_dir / "feature_correlations.csv", correlations)
    write_csv(output_dir / "feature_buckets.csv", buckets)
    write_csv(output_dir / "feature_diagnostics.csv", diagnostics)
    write_csv(output_dir / "feature_manifest.csv", manifest)
    write_markdown(output_dir / "research_report.md", summaries, correlations, buckets, diagnostics)

    print(json.dumps({
        "results": len(result_paths),
        "strategies": len(summaries),
        "trades": len(trades),
        "output_dir": str(output_dir),
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
