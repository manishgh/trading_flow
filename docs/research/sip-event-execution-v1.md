# SIP event execution and source assessment

This checkpoint adds an explicit event path to the shared C# execution simulator.
It does not replace the published `daily-rth5m-nextslice-ioc-v1` results or silently
enable a new policy in the ordinary portfolio runner.

`DeterministicExecutionSimulator.AdvanceEvent` consumes full Unix-nanosecond quote
and trade batches. A missing OHLC candle is not an input requirement. All events
at one nanosecond remain one batch; their internal arrival order is not invented.
An order cannot consume the observation that created it. A stop is elected by an
eligible trade inside strictly prior, fresh NBBO and can fill only on a later
observation. Quote condition and trade condition eligibility must be supplied by
a separately frozen, supported normalization contract.

Entry, stop, target, fee, OCO and position changes use the existing shared book.
Entry-fill reports and exit-fill reports have separate occurrence and knowledge
clocks. Reducing commands require a same-symbol, opposite-side entry parent and
its remaining reported exposure. Matching also caps against that parent's actual
remaining shares. A causally valid reduction that races an unreported exit is
recorded as a broker-route rejection; it is not silently resized using knowledge
the controller lacks. An exit cannot borrow another entry's shares.

`ExecutionProtectionActivation` is mandatory. Native children require the entire
parent to have filled and its report to be available. Custom per-fill protection
is a distinct primitive and must not be described as a native bracket. A native
parent partially filled at route expiry has no assumed repair or child orders.

`EventBacktestExecutionReplay.ReplayAsync` is the additive single-opportunity
research coordinator. Its inputs are a frozen raw-unit intent from the shared
strategy brain, a complete event timeline, explicit future daily liquidation
decisions with their true knowledge times, and declared controller latency. It
supports native full-parent protection. It requires an exclusive entry-route
expiry. The expiry is committed with the entry, is applied by the book before a
print at that exact time, survives entry replacement, and cannot be extended by
omitting a replacement field. A generic DAY label alone does not supply a market
calendar or expiration policy.

Empty/short evidence, unresolved orders, open positions, missing fresh quotes,
same-nanosecond ambiguity and broker-route rejection remain explicit unavailable
results. `Flat` describes the physical position only; it is not a completeness or
profitability assertion. The coordinator does not synthesize candles, ignore an
unknown interval as a no-touch, discover strategy signals, or admit portfolio
positions. Connecting it to a new portfolio experiment requires a separately
frozen policy and experiment ledger, matched source controls, and verified price
and quantity conversions. Current asset identifiers and a numeric factor of one
do not prove historical identity or adjustment history.

## Source archives

`SipExecutionArchiveCollector` issues read-only SIP quote/trade requests. It
preserves raw JSON in gzip pages, compressed and uncompressed SHA256, exact
pagination tokens, actual capture times and request identity. An exclusive end is
converted to an inclusive provider end by subtracting one nanosecond, rather than
one .NET tick. It refuses future intervals, repeated tokens, timestamp regression,
changed cached bytes and disk space below the declared reserve. Successful
pagination describes the returned historical snapshot, not original live receipt.

`SipExecutionArchiveReader` pins manifests, requests and page receipts, then
verifies and streams their raw bytes. Full verification requires consuming the
whole stream. Source timestamps and the requested interval must precede every
page's actual capture. Same-time observations, IDs, conditions, tape and exchanges
are retained. No row is discarded because it was ineligible to form an OHLC bar.

Quote sizes in this contract are shares for sources on/after 3 November 2025.
Older round-lot sources need a different explicit conversion contract. See the
[Alpaca size change](https://docs.alpaca.markets/us/v1.1/changelog/marketdata-bid-and-ask-size-display-change).

`execution-source-audit --quotes <archive> --trades <archive> --output <new.json>
--max-quote-age-ms <declared-scenario>` performs an offline source measurement.
It publishes only after fully consuming both paired archives. It reports spread,
displayed-size, condition, tape, exchange and strictly prior quote-age
distributions, including missing/stale/invalid/ambiguous comparisons. Unknown
conditions are inventoried, not automatically granted fill eligibility. Replaying
the same output requires identical bytes; changed output is refused.

These are retrospective source measurements. Source quote age is not network or
fill-report latency. SIP displayed sizes and historical prints do not reveal
queue position or the impact our hypothetical order would have had. Alpaca paper
order and fill activities can verify paper order lifecycle/quantity reconciliation
but do not establish live impact, latency slippage or queue position; see
[Alpaca paper assumptions](https://docs.alpaca.markets/us/docs/paper-trading).

## News and historical versions

`news-assessment` exports immutable, blind human review assignments, freezes the
existing display-rule baseline, imports actual human judgments and evaluates
held-out predictions. The supplemental archive packet is not an existing
catalog ground-truth export. The display taxonomy abstains from the thirteen
economic catalyst labels, direction, materiality and impact scope, and cannot
pass their economic accuracy gate. No human labels or successful scores are
generated by the exporter. The binding human guide remains unchanged.

`EconomicNewsRuleClassifier` is a separate, versioned research classifier for the
thirteen economic categories. It records the exact supporting phrase and text
field, distinguishes headline-only from fuller text, and abstains on weak or
conflicting assertions. Direction is bound to the matched event rather than any
positive/negative word elsewhere in the story. Ticker metadata alone does not
prove that a company is the event's subject. Materiality remains unknown; these
rules do not establish financial magnitude or expected stock-price impact.
Classification uses its actual UTC completion time. Neither the old display
baseline nor this new classifier generates human ground truth or proves its own
accuracy.

`news-category-pilot predict-rules --packet <packet.json> --packet-sha256 <sha>
--output <new.json>` freezes rule predictions and the classifier assembly hash
before review. This mode accepts no human labels. The initial blind workbook
contains twenty-five development stories, not the untouched held-out set. Only
the category is requested; unknown and incomplete review are accepted. The small
pilot guides development and is not a replacement for the full economic news
assessment required for promotion.

`news-category-pilot import --workbook <completed.xlsx> --selection <selection.json>
--selection-sha256 <sha> --output <new.json>` imports real category judgments.
It binds source identities, excerpts, publication timestamps, provider links and
the Context/Categories sheets to the hashed selection and original source packet.
Formula cells are rejected rather than executed. Workbook styles, the category
cells and optional reviewer/notes fields can be edited; source rows can be
reordered together. Blank categories remain unreviewed. No missing judgments,
direction labels or successful accuracy scores are filled in by the importer.

`HistoricalEvidenceManifestBuilder` and `HistoricalEvidenceResolver` retain exact
source versions and distinguish actual capture, unproved original delivery,
explicit provider-update diagnostic reconstruction, official dated archive facts,
and current-only snapshots. Original-delivery booleans or free text are rejected
by the raw capture importer and manifest validator. Current earnings results do
not establish a pre-entry schedule/estimate. Current Finviz sectors do not
establish old memberships; issuer CIK/SIC is not a share-class/Finviz-sector
history. Corporate actions are retained without inventing complete adjustment
factors. No reconstruction mode changes a current capture into an old receipt or
authorizes trading.
