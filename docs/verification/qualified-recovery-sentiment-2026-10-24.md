# Qualified recovery and company news — implementation checkpoint

24 October 2026. User approved `docs/research/qualified-recovery-sentiment-proposal-2026-10-24.md` and requested real news samples before economic backtests. No economic backtest, trading activation or core-data deletion is authorized by this checkpoint.

## Implemented behavior

- Four immutable Research recipes reuse the existing SMA5 recovery strategy and its portfolio runner. All require the original completed-bar stretch/rebound/trend setup plus an inclusive 1–3 pre-decline ATR remaining drawdown. Ranking, sizing, stops and exits are unchanged.
- Qualified technical has no news dependency. The three news arms share the same completeness/uncertainty check, then respectively ignore direction, veto supported negative company stories, or require positive company news while retaining the negative veto. Positive news cannot bypass technical qualification or cancel negative evidence.
- The existing archive/pilot command, economic classifier and pinned FinBERT service produce immutable per-company assessments. No new database, provider, cohort runner or run-specific executable was added. The initial supported language is deliberately narrow: explicit literal ticker subjects and supported earnings/guidance/analyst assertions. Other language remains uncertain.
- Original headline, summary and body remain retained. Each publication is rebound to the original selected archive rows, receipts and hashes when published/read. FinBERT evidence checks exact input hash, complete token coverage, loaded model revision and actual loaded weight hash. It never silently truncates research input or falls back to VADER.
- Actual October receipt and classification clocks remain distinct from the historical provider-update-plus-24-hours/next-session assumption. Diagnostic assessments cannot authorize strict historical, paper or live execution.
- An authenticated, explicit sample-read endpoint and separate News preview serve existing published assessments. Web and Android show company direction, text tone, confidence, full evidence, text support and publication/assessment clocks. Reading samples does not invoke providers or inference. Live news is unchanged.

## Independent reviews

Sol 6.1 High `news_selection_review` reviewed strategy/quant semantics independently; `retention_code_review` reviewed engineering/design independently. Neither ran economic jobs or altered data. Parent reviewed the implementation and performed serialized checks.

Findings fixed before publication:

1. Wildcard direction text could attribute a second company's event to the first company. Separate anchored subject/assertion rules now reject mixed clauses and beneficiary ambiguity.
2. An 8,000-character input bound did not prevent FinBERT's token truncation. Strict research inference counts actual tokens, including special tokens, and rejects over-capacity inputs without truncating.
3. A declared weight hash did not prove loaded weights. The loader now fingerprints the selected cached weight file, checks load stability and returns its actual hash.
4. A preview could contain internally consistent invented source/assessment pairs. Publication/read now rederive the exact original selected source population.
5. Invalid destinations were checked after inference. Both output destinations now undergo containment/reparse checks before source reads/model work, with zero-network/zero-created-file regression cases.
6. Android initially omitted several review fields. Its sample display now includes the evidence and confidence fields used by Web.
7. The first real pilot exposed a long supporting article being reused as a short headline assertion. Supporting fields over 32,000 characters now remain unchanged and produce an explicit Unknown reason; the batch continues. This is separate from FinBERT's token-capacity check.
8. A corrupt-publication exception escaped the preview's failure handler. It now returns an explicit unavailable response without displaying or overwriting corrupted data.

Quant source review found the common uncertainty mask, negative veto, positive requirement, strict execution fences and exact recipe lineage consistent with the approved proposal. Engineering source review found no remaining actionable blocker. A paged preview still verifies the whole source graph; pagination bounds display rows, not verification cost. This explicit 25-story pilot does not establish large-archive UI performance.

## Verification and pilot

- [x] Earlier component slice: 222 C# tests passed after the corruption-handler/catalog-expectation fixes.
- [x] Python sidecar: 17 unit tests passed.
- [x] Final Android target build after sample-display parity change: zero warnings/errors. No phone deployment or device interaction.
- [x] Both changed JavaScript files pass `node --check`.
- [x] Final C# slice including long-supporting-field and early-destination cases: 232 passed, zero failed/skipped. TRX: `../../outputs/qualified-recovery-sentiment-20261024/tests/qualified-news-component-final-232.trx`.
- [x] Retained 25-story pilot publication: 78 per-company assessments, all Unknown, zero model inferences. Independent artifact review verified all 4,930 dependency hashes, exact original selected payload rows and issuer associations, and truthful clocks. See `../../outputs/qualified-recovery-sentiment-20261024/independent-review.md`.
- [x] API preview smoke check: health 200; anonymous samples 401; authenticated samples 78 total/25 per page on pages 1 and 2; invalid page size 400; authenticated News HTML 200 and includes assessment panel. `../../outputs/qualified-recovery-sentiment-20261024/api-preview-check.json`. No automated browser visual or phone device check was performed.

The initial sandbox .NET run could not establish the testhost connection. A sandbox Python temporary-file permission error and Android Java packaging error were also rerun successfully on the host. These were not silently counted as passes. The failed synthetic Python directory was confirmed absent. Existing synthetic test fixtures dispose their owned directories; core archives and prior result packages are preserved.

The first real pilot command aborted on the supporting-text bound before publication. Its failure is retained here rather than represented as a completed assessment. No economic attempt was consumed.

The corrected pilot used the existing `news-assessment assess-company` command, original `pilot-selection.json`, frozen pre-period FinBERT policy and ordinary immutable evidence directory. Publication SHA-256: `4464ae1b1ad2dc456e6e61819b9beafa48594d4ebb2a586a7fe362dab0c10c66`. Final command receipt: `../../outputs/qualified-recovery-sentiment-20261024/pilot-assessment-final.json`. Full source-bound results and `SAMPLES.md` are under `C:\project\trading_flow\data\research\evidence\company-news-assessments\4464ae1b1ad2dc456e6e61819b9beafa48594d4ebb2a586a7fe362dab0c10c66`. A blank blind review JSON is retained alongside them; zero human labels were invented. Original category workbook is unchanged.

Actual receipt times: 19 October 2026, 12:31:44.313737–12:58:09.990305 UTC. Actual assessment times: 24 October 2026, 15:32:14.7531154–15:32:15.7422173 UTC. Pilot rows have no assumed historical availability. All 78 rows failed the company-assertion rules before model inference. This does not measure FinBERT prediction accuracy or speed.

## Real sample findings

| Company | Archived headline content | Actual system assessment and gap |
|---|---|---|
| TMUS | T-Mobile Q2 EPS beats estimates; sales miss estimates | Unknown. Company-name mapping and mixed financial assertions are unsupported. Do not turn the positive clause into a wholly positive story. |
| DTM | Wolfe Research upgrades DT Midstream to Outperform; announces $145 target | Unknown. The supported event category is AnalystAction, but the company name and analyst-first sentence are not bound by the literal-ticker grammar. |
| USAR | USA Rare Earth announces secondary offering of 126,476,950 shares | Unknown. Company-name mapping and offering direction are unsupported. A secondary offering headline alone does not establish new-share dilution. |
| OABI/LLY | OmniAb collaboration with Lilly; possible upfront/milestone payments | Unknown separately for each company. Company-specific beneficiary relationships and this event type are unsupported. |
| NVTS | Navitas selected by U.S. government to develop power semiconductors | Unknown. Name mapping and contract direction are outside this initial supported subset. |

Across company rows, 77/78 lack the literal ticker in the headline; all 78 fail the complete supported assertion grammar. 58 have markup in supplied supporting text; one retained article body exceeds the short-assertion reuse limit. These reasons overlap and must not be added as independent rejected-story counts.

**Pilot conclusion: zero usable sentiment coverage.** The software completed the approved bounded assessment, but the initial mapping/rules are too narrow for these real articles. Running the news portfolios now would primarily test exclusion by unsupported text. The result does not show that positive/negative news is unhelpful, and it does not establish a usable general sentiment feed.

The paused Web app was restored on its existing port 53021 with the existing operator data root/account binding; trading and provider collection retain their prior disabled settings. The first restart lacked the existing account binding and exited before serving; the successful restart loaded it from the existing canonical local configuration into process environment without copying secrets. No live-feed check is claimed. Task-owned FinBERT was stopped after the pilot to release memory; model weights/cache remain retained. Web remains available as previously requested.

## Scope left pending

No classifier accuracy claim is made: independent human relevance/direction labels are not yet available. This pilot is a coverage/error check, not the 500/100 promotion evaluation. The April–October price period has already been examined and remains development research. The twenty-attempt comparison requires a later explicit checkpoint after showing sample judgments; no jobs or custom configurations have been created for it.

The next proposed change, not implemented, is a bounded extension of this adapter: freeze company-name/ticker mappings with source/version and historical limitations; support analyst-first and ordinary financial headline syntax; preserve separate positive/negative clauses and named beneficiaries; map cleaned markup back to original evidence spans rather than dropping contradictory context. Test these on the same retained pilot and targeted adversarial fixtures before proposing any expansion of event categories. Whole-story tone cannot substitute for a company-specific assertion. A few independent human judgments are still needed to check meaning. This is a material extension of the initial approved method and must be reviewed before implementation under `AGENTS.md`.
