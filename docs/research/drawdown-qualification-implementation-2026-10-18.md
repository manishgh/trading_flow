# Drawdown observation implementation contract — 18 October 2026

Status: feature-only implementation; no economic attempts or enabled overlays.
Contract identity: `drawdown-observation-v1`.

At completed, available native daily decision t, the feature anchor is the latest
equal-high occurrence of the highest high in t-20 through t-1. Twenty prior rows
are mandatory. A decline requires C(t)<H(p). Its low-so-far excludes the peak bar
and includes p+1 through t. Depth is a fraction, never percentage points. Predecline
depth divides H(p)-C(t) by ATR14(p-1); current lagged volatility is ATR14(t-1).
Signed one/three-session moves divide C(t)-C(t-k) by ATR14(t-k). True range uses
the previous close and divides by ATR14(t-1). Volume divides by the median of
all preceding twenty volumes, including zeros. Zero/negative denominators and
zero-range close location are unknown. No numeric cutoff is selected here.

The census scans the completed prefix in order from its first eligible decline.
That observation freezes its episode peak and onset. The episode retains that
anchor even when the rolling feature anchor changes or expires. Recovery is the
first completed close >= the frozen high. Another episode cannot start until
both recovery and the five-session outcome window have completed; the next
observation may start it. Thus label intervals never overlap. Equal highs select
the latest prior high; no within-bar high/low ordering is inferred. Episode IDs
bind contract, symbol, feed/adjustment identity, peak and onset timestamps.

One/three/five-session labels are gross close returns plus favorable/adverse
OHLC excursions after onset. All three remain unpublished until the entire
five-session window is available. These are diagnostic labels, not cost-adjusted
trade returns. A mature unrecovered episode is retained alongside recoveries;
the current active episode is excluded from its own prior-history count.
Unmatured windows are right-censored. Expected-session omissions make an
interval missing, not a shorter horizon. A history eligibility claim additionally
requires a stable security ID, price-adjustment version and the exact expected
session calendar. Without those inputs census rows remain observed-row
diagnostics and the history gate is unknown. Current runtime adapters do not
supply that historical identity/calendar contract, so history gating remains
unavailable. Minimum independent count is explicit configuration, never guessed.

Input rows and snapshots must align, increase strictly, share symbol/feed/
adjustment identity, contain valid OHLCV, and be completed and known by the
decision. Future suffix rows are ignored by index. Unknown observation reasons
are retained. Changed feed/adjustment identity is rejected rather than joined.
Corporate action adjustment revisions and identity changes cannot be established
from candle prices alone and require the history contract above.

An absent qualification block leaves all admission rules and semantic decision
hashes unchanged. Daily observations live in canonical audit JSON outside the
semantic object. An explicit `observe` block also never filters. Optional `gate`
uses inclusive scalar bounds and returns qualifies/rejected/unknown; a required
unknown fails closed. Failed qualifiers close the candidate in Rejected state
at the original trigger; Armed-to-Rejected is an explicit state transition so
the runner cannot retry the opportunity on later evidence. Sector and event requirements return unknown until their
separate point-in-time evidence contracts are implemented. Configuring them does
not infer no-event or substitute SPY for a sector. No strategy file is changed.

The shared kernel records feature eligibility and qualifier verdict even for
baseline setup/trigger rejections. Baseline strategy admission and execution
trigger remain separately recorded rules. Backtest candidate decision journals
retain those canonical audits for every evaluated symbol-session; candidate IDs
link to the existing portfolio admission and fill audits. The observation does
not simulate rejected opportunities or claim a complete economic comparison.
Existing baseline artifacts and prior execution work remain untouched.
