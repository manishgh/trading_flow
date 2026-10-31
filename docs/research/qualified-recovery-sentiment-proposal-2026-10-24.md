# Qualify recovery candidates, then assess company news

Date: 24 October 2026. Status: the user approved implementation and the retained pilot assessment on 24 October. Economic backtests remain a separate checkpoint after showing the sample judgments. Independent read-only quant/code review: Sol 6.1 High, `news_selection_review`; parent incorporated its corrections below.

## Approved target amendment — 25 October 2026

The user approved changing the planned profit target from 2.5R to 3R for all four upcoming qualified recovery recipes: technical, assessed coverage, negative veto and positive required. Set only `exit_rules.target_r_multiple` to `3.0` through the existing recipe configuration. Retain swing-low stops, the tick buffer, position sizing and risk limits, ranking, costs, SMA5 early exit and five-bar holding limit. The 3R value is a planned target relative to initial risk; early exits and costs mean realized reward-to-risk may differ.

Register the amended recipes as semantic version `1.1.0` in the authoritative strategy catalog; the existing YAML filenames and legacy numeric `version: 1` remain unchanged. The normal catalog computes each new canonical content hash from the amended recipe, semantic version and admission profile. Preserve earlier pilot receipts and published identities/results unchanged, including the original `technical.recovery-sma5-exit@2.0.0` comparison with its 2.5R target. No economic runs of these four qualified recipes have started, and this amendment starts none or grants paper/live authorization.

The older 2.5R baseline no longer isolates the technical band's effect when compared with the amended 3R qualified technical recipe; that comparison now changes both the band and target. The three comparisons between qualified recipes retain their common target. Any future economic comparison must disclose this difference and still pass the separate execution checkpoint below.

## What the completed comparison tells us

The October 22 comparison deliberately tested recent-news presence, not sentiment, because original historical delivery/revisions and evaluated company-event classifications were not established. It does not answer whether positive or negative news improves entries. See `historical-news-diagnostic-design-2026-10-22.md` and `../../outputs/historical-news-diagnostic-20261022/independent-quant-final-review.md`.

Avoidance improved monthly return in 12 of 20 supported May–September cohort/month pairs and reduced it in eight. Daily-close drawdown improved in 17, worsened in two and tied in one. Evidence checking alone changed nothing. Reduced exposure can itself reduce drawdown; these results do not establish that the excluded news was adverse. Group 3 July improved from -5.32% to -2.51%, while September fell from +5.15% to +2.34%. Group 4 August changed from +0.68% to -1.40%. Do not assign hypothetical profit to rejected signals.

Working hypothesis: distinguish a technically suitable recovery from a price decline associated with adverse company information. Positive news may support an already qualified entry, but cannot make an unsuitable chart eligible. These are testable hypotheses, not proven improvements.

## Research supporting the question, not a profit promise

- [Heston and Sinha, Federal Reserve, 2016](https://www.federalreserve.gov/econres/feds/news-versus-sentiment-predicting-stock-returns-from-news-stories.htm): positive and negative news had different response speeds in their sample. This supports testing direction separately; it does not establish an optimal holding period or threshold for our stocks.
- [Chan, 2003](https://www.sciencedirect.com/science/article/abs/pii/S0304405X03001466): news-associated moves and no-news moves showed different drift/reversal behavior. The study's formation periods and universe differ from this daily recovery strategy.
- [Tetlock, 2011](https://business.columbia.edu/faculty/research/all-news-thats-fit-reprint-do-investors-react-stale-information): repeated/stale information has different effects from fresh information. Preserve revision identities and avoid counting repeated captures as independent confirming events.
- [Araci, FinBERT, 2019](https://arxiv.org/abs/1908.10063) and [official model card](https://huggingface.co/ProsusAI/finbert): financial-text polarity is a useful starting component, not a probability that a trade wins.
- [FinEntity, EMNLP 2023](https://aclanthology.org/2023.emnlp-main.956/): sentiment must be associated with the specific entity. Whole-story positive tone can describe a competitor rather than our stock.

## What currently qualifies a technical entry

`SignalGenerator.Setups.cs` requires a completed close above the preceding bar's high and, within the prior five bars, at least one of: three consecutive down closes, RSI2 below 10, or a close below the lower Bollinger band. The recipe also has stock/SMA200 and SPY/SMA200 conditions. This is not blind buying after every decline, but it is a broad stretch-and-rebound test.

There is no active `drawdown_qualification` gate in the current recovery recipes. The analyzer records observations; recording a metric does not filter candidates. Adjacent 8–35% reclaim settings belong to a different setup and do not constrain this strategy. No existing component estimates a calibrated probability of recovery.

### Proposed single technical change

Reuse `predecline_atr_depth` with an inclusive 1.0–3.0 range, frozen before execution. Keep the existing stretch, rebound, trend, ranking and risk conditions. This is one combined-band hypothesis, not separately measured floor/ceiling improvements and not an optimized or academically established range. No threshold grid or post-result adjustment is included.

The exact existing metric is `(highest high in the prior 20 completed sessions - current completed close) / ATR14 measured before that peak`. It measures the decline still remaining at the rebound close, not the deepest preceding decline. A strong rebound may already have moved above the band's lower bound and be rejected. Record maximum prior decline separately; do not mislabel the metric.

Example: prior peak $100 and pre-peak ATR $4; a qualifying rebound close between $88 and $96 meets the depth band. A close at $99 is too shallow; $84 is too deep for this hypothesis. Both still require the original technical trigger. Display the actual percent decline alongside ATR depth. ATR is a scale of usual price movement, not a forecast or guaranteed risk bound.

Missing warmup or required prices means unknown/ineligible with a reason. No new pressure, volume, sector, matured-history, ranking, exit or position-sizing feature in this first test. Volume and existing price measurements remain available for explanation. Being below a prior high alone is not proof of a falling trend; the original stretch-and-rebound rules remain necessary.

## Reusable news feed and assessment method

Reuse the retained provider archives, revision selector, evidence catalog, `EvidenceSentimentAssessmentBuilder`, pinned FinBERT sidecar, `EconomicNewsRuleClassifier`, existing category pilot/assessment workflow and shared strategy decision kernel.

Current limitations are concrete: FinBERT receives whole text and ignores the supplied symbols; the operational catalyst attachment selects one latest event; the diagnostic presence adapter deliberately rejects strict sentiment inputs; economic rule categories are not independently measured accuracy and materiality is often unknown. Merely turning on `require_positive_news` would not solve these problems.

Smallest proposed addition: a revision-bound, per-company assessment adapter and a multi-story decision context, extending existing contracts rather than creating a second news database or execution runner.

1. Bind exact archived text to its company subject. Start with explicit, unambiguous company/ticker references and the existing rule evidence excerpts. A provider symbol tag alone is insufficient. Ambiguous aliases, pronouns, multi-company beneficiary relationships and unsupported subject mappings remain unknown. Keep full surrounding text and the extracted evidence spans; do not remove negation or contradictory sentences to obtain a label.
2. Run the pinned FinBERT model on supported issuer-specific evidence text. Reuse the existing economic rules for event category and explicit direction cues (initially the clear earnings/guidance/analyst cases they actually support). Store textual tone separately from asserted economic direction. No new language-model provider is required for the initial adapter. Freeze the company-name/alias mapping source and version; if reliable mapping is unavailable, begin with explicit literal ticker subjects. The current rule classifier derives direction from headline assertions: passing an extracted sentence only as summary/body is insufficient. The adapter must explicitly bind the classified assertion to its exact original field/span, preserving the original headline, or deliberately extend that classifier interface with focused tests.
3. An actionable positive/negative assessment requires unambiguous company relevance, supported explicit directional assertion, and agreement with the frozen model's top polarity label. The existing rules need subject binding before they can supply that assertion. Disagreement, unsupported direction or mixed issuer assertions remain uncertain. This conservative agreement rule is an unvalidated diagnostic hypothesis; FinBERT confidence is displayed, never represented as recovery probability. No confidence cutoff is optimized from profit or a tiny review sample.
4. Retain category, company/sector/global scope, evidence text, positive/negative/neutral/uncertain states, model identity, text support and any known materiality. Unknown materiality stays unknown and is not used as an entry condition in this first comparison. Sector/global stories are visible contextual information; they do not become company buy/sell evidence without a reviewed mapping.
5. Assess every relevant, fresh, selected article revision, not just the latest headline. Exact duplicate captures count once. Preserve conflicting stories. One supported negative story vetoes even if another is positive; positive sentiment cannot cancel negative information. No broad weighted average conceals disagreement.
6. Distinguish complete coverage with no articles, confirmed irrelevant content, neutral information, uncertain relevance/direction, mixed supported positive/negative stories, missing text and model failure. Uncertain relevance/direction or failed required assessment withholds entry in every classification-dependent arm, including the coverage-only control. A supported positive story plus a supported negative story is known mixed evidence: it passes completeness and fails the negative veto. Provider/inference failure is never neutral or no news. Do not silently combine VADER fallback scores with the pinned FinBERT treatment.

Preserve the existing publication-age window of 120 hours and latest-assumed-available revision selection. Do not introduce recency weights or change to a faster news clock in the same experiment. This delayed daily test cannot establish the usefulness of immediate breaking news. The initial direction comparison covers supported earnings/guidance/analyst assertions, not every adverse company event. Other relevant stories may remain uncertain and withhold entries in all assessed arms; report that loss of coverage explicitly. The pilot must reveal whether this restriction leaves a useful sample before portfolio execution.

For historical research, retain actual October receipt and assessment times. A new explicit diagnostic policy must bind the classifier/model artifact, training-data provenance, text/extraction/rule versions, and the separately assumed historical availability (provider update + 24 hours, then next captured session). Today's classifications are retrospective measurements, never April-observed events. Unknown model training provenance or future-trained knowledge cannot be silently accepted as historically available. The strict provider and paper/live paths must continue rejecting this diagnostic substitution. Prospective capture uses actual news receipt and assessment-completion times.

## Bounded assessment before portfolio execution

Reuse the existing 25-story pilot identities rather than assigning 500 stories now. Preserve its original category workbook; a versioned review copy can add company relevance and positive/negative/neutral/uncertain judgments. Ten overlapping stories should receive independent human judgments where available. Blind human views must not reveal later prices or model suggestions. Models may precompute predictions for comparison, but cannot author human truth labels.

This small pilot finds extraction/subject/negation failures; it cannot calibrate confidence, prove rare-event recall, or satisfy the existing 500 resolved / 100 double-labeled promotion requirements in `catalyst-human-labeling-v1.md`. Existing category-only labels cannot be silently reused as sentiment labels. If the existing pilot lacks supported positive, negative or multistock examples, report those gaps and propose a bounded addition before claiming those cases were checked. Fix text/rule defects and freeze the method before viewing new trade returns. A separate labeled evaluation is needed for classifier accuracy claims; no passed flag is invented.

Present a few feed examples and pilot errors to the user before authorizing the economic matrix. Implementation of the adapter can proceed after design approval; economic execution remains a later explicit checkpoint because the issuer-specific method is not yet measured. A small pilot is not a promise that classification will be adequate.

## Four treatments, twenty attempts at most after the checkpoint

| Treatment | Entry rule beyond the fixed technical setup |
|---|---|
| Qualified technical | New 1–3 ATR band; no news dependency |
| Qualified technical with assessed news available | Same band; require complete coverage and usable assessment states; ignore direction for admission |
| Qualified technical avoiding negative company news | Same as assessed control; reject any supported negative company story; no-news and neutral states may pass |
| Qualified technical requiring positive company news | Same negative veto; additionally require at least one supported positive company story |

The last treatment tests the user's positive-news hypothesis; it is not the default recommendation that every rebound needs positive news. All four keep RSI2 ranking. There is no new positive-news ranker.

Reuse the original technical baseline only after exact input, period, cost, execution and original rule parity are verified; otherwise obtain approval for additional attempts. Technical-band effect is qualified technical versus that baseline; completeness effect is assessed control versus qualified technical; negative-news effect is veto versus assessed control; positive requirement is positive-required versus veto.

Five original cohorts, separate $10,000 per treatment/cohort; 24 original selected stocks excluding SOXL in the plan only. Same April 21–October 21 New York window, prepared prices, warmup, fees/slippage, stops, SMA5 exit, five-bar holding limit, four slots and risk limits as the audited comparison. Twenty includes failed attempts; no automatic retries or tuning. SIVE remains in Group 5 and missing required prices remain unavailable unless independently resolved. Do not silently drop or replace it.

This already-examined window is development research. Report every monthly/cohort row, partial and unavailable months, entries rejected at each step, fills, costs, time invested/cash exposure, per-symbol contribution and daily-close drawdown. Lower drawdown alone may just mean fewer positions. Report effects even when adverse and preserve the original acceptance screen; do not change success criteria after seeing results. A later untouched/prospective period is needed to test generalization. No combined six-month or pooled-cohort returns.

## Implementation surfaces, review and preservation

Expected surfaces: existing Domain evidence/decision contracts; Data evidence packaging and subject-bound assessment adapter; FinBERT sidecar transport where needed; shared Engine news evaluator; normal saved-plan/catalog/CLI orchestration; Contracts/API; Web News/Desk/backtest summaries and matching Android response/display consumers. Extend existing immutable recipe/catalog artifacts, not run-specific code or hand-authored cohort configs. This proposal adds no new database, collection service, model vendor or custom runner.

Simple user-facing stock explanation: `Eligible: 8% below recent peak (2 ATR); oversold and closed above prior day's high. News: positive guidance, company subject confirmed. Entry: permitted under selected strategy.` A rejection names the exact failed condition. No invented umbrella score. Web and Android use the same server explanation and distinguish diagnostic history from actual observed news.

Flow: saved Volatile/Finviz source -> data/warmup check -> selected strategy's technical qualification -> exact news revision and company assessment -> selected news rule -> unchanged portfolio/risk/execution -> monthly results and reasons. News cannot bypass technical failure. Failed original triggers do not wait for future evidence and re-enter under the same setup.

Focused tests: exact band boundaries; rebound close versus deepest low; unknown ATR/history; multi-company stories; negation/mixed cues; positive not cancelling negative; duplicate/revised articles; original publication age; failure/fallback identity; complete/no-news versus unknown; retrospective clocks; every treatment's common assessment mask; risk/fill parity; API/Web/Android contract and explanation parity. Independent senior engineer/design reviewer checks reuse and complexity; independent quant reviews both eligibility and the eventual monthly accounting. No custom output-folder executables.

Resource estimate, conditional on local model availability: one bounded adapter/UI implementation checkpoint plus review; classify the frozen archive once and reuse it across all books. Benchmark a small existing batch before estimating full inference time. The last 15-book start spent about 19 minutes preparing and about 15 minutes replaying; that is not a guarantee for this new pipeline. Do not add a performance redesign within this proposal. Preserve core news, prices, receipt archives, wishlists, Finviz sources and all results; delete only exact generated intermediates after independent quant approval.

## Checklist

- [x] Read actual strategy, qualification and news paths.
- [x] Review prior monthly results and primary research.
- [x] Independent Sol 6.1 High quant/code inspection and final proposal review; incorporate issuer mapping, classified-input and supported-category limitations. No blocking design finding remained; implementation and classifier accuracy are not yet verified.
- [x] Present this proposal before implementation.
- [x] User approves the concrete band and news assessment/feed design (24 October 2026).
- [x] Implement through existing components; complete independent code/design review, 232 focused C# tests, 17 Python tests and Android build. See `../verification/qualified-recovery-sentiment-2026-10-24.md`.
- [x] Generate the retained pilot judgments and preserve the frozen policy: 25 stories, 78 issuer rows, all Unknown, zero model inferences. Sample findings are presented in the verification report. This reveals unusable sentiment coverage; it does not pass the economic-run checkpoint.
- [ ] Approve/register at most twenty attempts; run normal saved plans.
- [ ] Independent quant review; monthly comparison and exact safe cleanup.

Verification of the 25 October 3R amendment: affected build passed (zero warnings/errors); 37 focused catalog, recipe-parity, order-planning and risk tests passed in outputs/company-assessment-reuse-20261025/tests/three-r-target-rebuilt.trx. Two earlier invocations used a stale test binary with old version expectations; a forced test-project rebuild resolved those failures. Independent engineering/quant review accepted the limited configuration change. The restarted authenticated API exposes all four 1.1.0 identities (three-r-api-catalog.json); public ngrok login returned HTTP 200. Trading remains paused. No economic backtest was run for this amendment: the existing full assessment memory/storage gate still applies.
