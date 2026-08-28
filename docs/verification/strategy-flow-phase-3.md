# Strategy Flow Phase 3 Verification

**Status:** complete

**Date:** 2026-08-28

## Scope

Phase 3 makes Alpaca candle evidence the only RVOL authority used by strategy
decisions. Finviz RVOL remains provider-reported discovery metadata for display and
audit. Backtest, paper/live, API evaluation, mobile automation, and wishlist
monitoring all select RVOL through the same typed strategy contract.

## Frozen Market-Evidence Profile

Profile version: `us_equities_same_time_rvol_v1`.

| Rule | Production value | Meaning |
|---|---:|---|
| Primary participation value | cumulative same-time RVOL | Current cohort volume through the completed bar divided by the prior-session median through the same New York exchange clock time. |
| Local acceleration value | slot-bar RVOL | Current completed bar volume divided by the prior-session median for the exact cohort and exchange clock time. |
| Baseline statistic | median | Limits distortion from one abnormal historical session. |
| Lookback | 20 prior sessions | The current session is never part of its own denominator. |
| Minimum valid samples | 20 sessions | Missing evidence rejects as `rvol_baseline_not_ready`; no zero or threshold substitution is allowed. |
| Feed | Alpaca SIP | IEX, unspecified, or mixed-feed evidence is unavailable for strategy RVOL. |
| Adjustment | `all` | Unspecified, mixed, or unexpected adjustment policies are unavailable. |
| Partial bars | completed only | An in-progress bar cannot enter indicator or strategy state. |
| No-trade intervals | provider-confirmed only | No synthetic OHLCV is invented. Cumulative volume carries forward; exact-slot RVOL remains unavailable without an exact completed bar. |
| Extended hours | isolated cohorts | Overnight, premarket, regular, and postmarket volumes never share a denominator. |
| Time basis | `America/New_York` | DST is resolved by exchange local time. Overnight remains one trade-date cohort across midnight. |
| Early close | provider schedule override | The regular/postmarket boundary follows the supplied exchange schedule. |
| Provider revision | bar close + 2 minutes | Later corrections do not rewrite live decision state. |
| Restart recovery | 45 calendar days | Sufficient to reconstruct the 20 prior trading-session baseline. |

Production does not synthesize an exchange calendar. Alpaca's calendar endpoint must
return a complete inclusive date range, including explicit closed dates, before RVOL
or live stream state can become decision-ready. Test-only profiles may supply an
explicit deterministic fallback calendar.

## Decision Boundaries

- `min_volume_spike_source` is typed and accepts only `cumulative_same_time` or
  `slot_bar`. Legacy and vendor values fail configuration parsing.
- The secondary whole-session denominator and its model/config fields were removed.
- `SignalGenerator` may form technical structure before RVOL is ready; the shared
  `StrategyDecisionBrain` owns the authoritative volume admission and exact rejection.
- Volume-disabled/advisory strategies do not require an RVOL baseline unless another
  enabled rule explicitly consumes RVOL.
- Finviz discovery persists `vendor_reported_rvol`, normalized query, provider receipt
  time, and raw archive reference. That metadata has no dependency path into engine or
  backtest decision assemblies.
- Audit/chart/warmup output records source measure, median denominator, sample counts,
  profile version, cohort, feed, adjustment policy, and reliability.
- Sparse historical intervals become comparable only when provider coverage proves
  the session was observed through the requested completed slot.
- Local candle reads have an explicit as-of cutoff. Accepted provider revisions are
  stored as separate known-at versions and replayed in point-in-time order.
- Cached market slices require an atomic manifest binding exact UTC range, provider,
  feed, adjustment, schema, checksum, and coverage. Stale, corrupt, or mismatched
  slices are refreshed without destroying the last committed cache on fetch failure.
- Candle and calendar data files are immutable. A manifest switch is the commit point,
  and a per-manifest cross-process filesystem lock serializes publication and cleanup.
  Lock acquisition is cancellable and bounded to 30 seconds.
- Adjusted candle caches use the versioned `adjusted_history_24h_v1` freshness policy
  and an explicit restatement revision. The Alpaca calendar is cached separately as a
  complete authoritative date range so `use_cache` research can run offline without
  synthesizing weekdays; `refresh` always asks the provider for a new calendar.
- Capability-preserving cache wrappers expose the inner provider's calendar,
  completeness, and provenance contracts to the candle pipeline.
- Wishlist observation subscribes through the single Alpaca stream and consumes shared
  in-memory state. It uses the production 45-day recovery depth, provider calendar,
  and 20-session same-time RVOL; it cannot substitute one-bar volume growth. Each
  ticker fails independently, and complete evaluator evidence prevents unchanged bars
  from being reevaluated while still admitting revisions to preceding bars/baselines.

## Deterministic Acceptance Coverage

- Median behavior under an abnormal prior session.
- Cumulative same-time behavior independent of yesterday alone.
- Carry-forward of cumulative volume across a prior exact-slot no-trade minute while
  exact-slot RVOL remains unavailable.
- DST-aligned New York slots.
- isolated premarket and regular cohorts.
- overnight continuity across midnight.
- provider early-close boundary.
- exact prior exchange-session selection across holidays and missing observations.
- early-close exact-clock rejection when no comparable normal-session slot exists.
- incomplete provider coverage rejecting a cumulative sample rather than treating a
  missing bar as a confirmed no-trade interval.
- mixed SIP/IEX, IEX-only, and mixed-adjustment fail-closed behavior.
- 19-of-20 sample readiness rejection.
- restart reconstruction preserving the same 20-session RVOL.
- revision-aware store reads and replay as-of behavior.
- store-to-replay ordering of an original bar and its accepted revision, including an
  earlier as-of query after the revision has already entered memory.
- split-adjusted cache refresh, checksum/provenance validation, and live-bar precedence
  over overlapping REST recovery bars.
- offline authoritative-calendar reuse, refresh-failure preservation, and incomplete
  provider-calendar rejection.
- warm SIP state rejecting an IEX stream event and requiring repair.
- source-boundary checks excluding Finviz and legacy full-session values from strategy
  decision code.
- unchanged wishlist evidence across repeated passes and host restart, acknowledged
  signal deduplication, penultimate-bar revision reevaluation, and ticker-failure
  isolation.
- immutable cache commit switching, malformed/null manifest members, cross-timeframe
  cleanup containment, cross-process lock cancellation, and manifest-commit failure
  preservation.

## Verification Commands

```powershell
dotnet build TradingFlow.slnx --no-restore --disable-build-servers
dotnet test src\TradingFlow.Tests\TradingFlow.Tests.csproj --no-build --disable-build-servers
dotnet build src\TradingFlow.Mobile\TradingFlow.Mobile.csproj -f net10.0-android --no-restore --disable-build-servers
git diff --check
```

Post-remediation verification on 2026-08-28:

- Solution tests: 1,292 passed, 0 failed, 0 skipped.
- Android `net10.0-android` build: succeeded with 0 warnings and 0 errors.
- `git diff --check`: clean.

Three independent review passes found and then verified remediation for cache
capability forwarding, revision history, wishlist observation, cache freshness,
warm-up calendar wiring, stale evidence idempotency, ticker failure isolation, cache
path containment, and cross-process publication. The final review reported no P1
commit blocker. Its remaining disk-hygiene observation is bounded to harmless orphaned
calendar data after process death; readers follow only the committed manifest.
