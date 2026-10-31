# Swing family cycle 3 implementation and execution receipt

Plan: [volatile swing optimization](../research/volatile-swing-optimization-2026-10-09.md).

Output root: `C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/swing-family-cycle03-20261009`.

## Implementation

Optional `require_ordered_reclaim_pullback` uses the most recent highest high in the completed prior window and requires a later completed-bar low before the trigger. Same-bar high/low ordering is never assumed. False remains omitted from JSON, preserving existing strategy identities and behavior. No canonical strategy or catalog disposition changed.

RunConfigWriter now saves all existing step/base parameters, including non-default values when another setup is selected, preventing generated variants from silently reverting to parser defaults. No new optimizer, news classifier, sector model or UI was added.

The replay source and generator are retained under the output's `replay-source`. Its executable source derives from the active research RSI6 artifact; archived YAMLs are separately hashed reference specifications. Exact resolved rules and identity are in `trial-registry.json`. Driver checks input, code and configuration hashes plus runtime identity before execution; existing summaries cannot silently count as successful resumed work.

## Verification tier: component

- `dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter 'FullyQualifiedName~SwingFamilyConfigurationTests|FullyQualifiedName~SignalGeneratorStepBreakoutTests|FullyQualifiedName~TechnicalFactorTests|FullyQualifiedName~StrategyArtifact' --verbosity minimal`:39passed,0failed after fixture corrections.
- `dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~SwingCausalityAndMethodTests|FullyQualifiedName~RunConfigParsingTests|FullyQualifiedName~BacktestSimulationCharacterizationTests|FullyQualifiedName~ChronologicalBacktestExecutionCoordinatorTests' --verbosity minimal`:51passed,0failed.
- Driver and affected Web/project dependencies compiled with0warnings/0errors. Driver build required scoped .NET execution permission; ordinary sandbox driver builds exited before compilation without diagnostics.
- New counterexamples: low before highest peak, post-peak low versus lower earlier low, tied most-recent peak, signal-bar exclusion, future-bar perturbation, nondefault step YAML roundtrip and default JSON omission.
- Independent requirements and engine reviews cleared the bounded diagnostic phase; exact pre-run counts:28strategy identities,140configuration hashes,59code/binary/registry hashes and28captured-input hashes.
- Full release suite, Android, operational broker acceptance, finer execution/quote validation, unseen holdout and paper shadow were not run. This is a research component checkpoint.

## Data evidence

All28market/calendar files and all26canonical symbol series match cycle2. Original25stocks remain in five cohorts, with BXDC/FPS/SIVE explicitly unavailable under the unchanged260-session requirement and CRCL/MIAX entering only after actual warm-up. No replacement stock or operator-list mutation is permitted.

The read-only data audit found first-cohort operator news onlyOctober6–8 and zero classified catalyst result rows. The archived news dataset covers a different76-symbol universe throughJuly24 and declares historical availability/revision completeness unproven. Classified catalyst and sector experiments are not executed; missing data is not treated as neutral news or zero returns.

## Results and cleanup

All 140 frozen runs completed successfully, one original cohort at a time. The final output contains 980 unique monthly strategy/cohort rows, including partial April/October, and all 70 exact-control comparisons reproduce. `trial-registry.json` is authoritative for this cycle; `frozen-study.json` retains parent-study metadata.

`replay-source/verify-results.py --complete` independently reconciled 616 successful work items, 1,316 artifact references, 2,984 fills, 17,780 close marks and 980 monthly rows. Fees match the frozen formula; positions finish flat; the largest exact-ledger versus rounded-summary difference is $0.000238. `monthly-attribution.py` reconciled 980 monthly symbol-contribution rows. `decision-diagnostics.py` retained 490 monthly candidate-reason rows at base costs. These are ordered rejection reasons, not independent filter ablations.

Independent engine and requirements reviewers cleared diagnostic publication. Representative audits checked candidate identity/timing, fill quantities/costs, source-open/stop/target prices and cash. Trailing-stop paths were not fully reconstructed independently. The final review receipt records sample counts and remaining limits. Full entry-day protection, intrabar ordering and first-receipt price lineage remain unproven; this is not a no-bias or promotion certificate.

The preferred next research comparison is unchanged broad-stretch mean reversion with SMA5 recovery exit, preserving the exact RSI5 control. SMA5 raises monthly return in 18/25 full-month cohort cells at both costs but leaves 11 losing cells, including July in all five cohorts. Narrowing entry is mixed. Looser reclaim increases monthly close drawdown in 20/25 cells at either cost; do not advance that relaxation. Base breakout has only ten/six trades and cannot support parameter selection. All results remain research; no catalog authorization or broker orders changed.

The original operator membership hash matches after execution: two lists, 30 members, SHA256 `1ad9f964ba274eccf10a33b49abcf8260a7ef8166951e6c066845aae7dc8b07d`. Cleanup removes only the owned `.input-preflight` (28 files) and `.tmp/family03-driver` (114 files) after both reviews and all analysis scripts finish. Published results, configurations, manifests, replay sources and receipts remain. `cleanup-receipt.json` is the authority for post-cleanup artifact checks.

The human-readable conclusion is `research-assessment.md`; `monthly-comparison.md` contains every strategy's monthly returns/drawdowns. Supporting JSON retains costs, exposure, holding durations, positive/negative symbol contributions, all conditional entry/exit effects and interactions. No accumulated or pooled return is reported. The 20 bps scenario reruns execution, so sizing/path changes can make individual stressed cells higher.

The bounded 140-trial cycle is finished. Next implementation should address only demonstrated shared-engine gaps in finer swing execution, followed by new chronological evidence with the frozen definition. Do not keep tuning the viewed period. No GitHub synchronization, deployment, Android work or broker activation occurred.
