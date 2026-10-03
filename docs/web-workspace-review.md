# TradingFlow web navigation review and cleanup

28 September 2026. Scope: web page structure and navigation. The selected landing experience is the daily workspace, with preparation results and candidates first.

## Findings

The previous header exposed eight peer destinations (nine for administrators), clipped links at ordinary desktop widths, and hid navigation on phones outside the Desk. The Desk stacked two different candidate/discovery interfaces. Preparation led with settings and buried results. Paper was grouped under Operations, while the command palette sent Operations to Paper. Several links discarded preparation or candidate-run context.

## Delivered structure

| Main area | Pages and responsibility |
|---|---|
| Workspace | Daily review, Candidates, Sources, News, Earnings |
| Trading | Positions, Orders, Paper runs |
| Research | Backtests and linked run/results details |
| System | Health, Warm-up, administrator Users |

The same main and local navigation is available on desktop and phone. Login and the root address open Daily review. Existing detail URLs remain available.

Daily review separates latest results, history, workflow settings and alerts. It loads the latest saved result where available, keeps technical provenance expandable, and separates manual collection from review. Candidate review and market exploration are separate views rather than stacked dashboards.

Paper authorization-mode changes, refresh and environment navigation retain accepted preparation membership. Candidate handoff opens the complete candidate run; audit and ticket return links retain its identifier. Market-detail links use the actual ticker parameter.

Clean latest reviews advance on refresh. Historical or edited reviews stay identified and retained, with an explicit action to discard review changes and open a newer result. Missing exact notification revisions show an unavailable state instead of substituting another result. Expired or ineligible preparation cannot continue via the research or paper buttons; server checks remain authoritative.

## Verification

- Web build: zero warnings, zero errors.
- 19 browser tests passed: preparation, workspace navigation, and affected candidate/order/backtest workflows.
- Checked 412px phone, 1280px desktop and 1440px desktop navigation, overflow and page headings.
- Regression coverage includes root landing, exact snapshot binding, latest-versus-edited review, unavailable notification links, candidate-run return links, retained drafts and exact retries.
- Senior requirements and independent code/design reviewers approved the scoped changes after their findings were addressed.
- `git diff --check` passed.

Screenshots use the isolated acceptance account and fixture data, not the operator's trading database.

## Scope limits

This is the web structure cleanup. Native Android screens, trading rules, strategy authorization and Market Predictor integration were not changed. Research and Paper retain their existing detailed editors; migrating experiment registration out of Paper is a separate deeper redesign, not claimed here. No strategy was promoted, workflow enabled or order placed. Existing secrets, operational data and prior uncommitted work were preserved. No GitHub synchronization occurred.

The isolated preview is stopped after verification; the normal local site is restarted for use.
