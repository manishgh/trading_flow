# Production Specification Deviations

This register records every deliberate deviation from a `SHOULD` or `SHOULD NOT`
requirement in the binding production specification. A deviation does not waive a
`MUST` or `MUST NOT` requirement.

## Active Deviations

### D3 - Swing-first strategy scope

- **Status:** Active
- **Decision date:** 2026-07-21
- **Owner:** TradingFlow
- **Affected requirements:** DAY infrastructure and SWG strategy-family rollout
- **Decision:** Build and validate the production safety infrastructure for both day
  and swing trading, but promote swing strategies first. Day-trading strategy
  families remain disabled in production until their research evidence satisfies the
  promotion gates.
- **Rationale:** The repository has stronger retained evidence for the V4 trend and
  catalyst-drift swing families than for the current day-trading experiments. Enabling
  unvalidated day strategies would confuse infrastructure completeness with evidence
  of an edge.
- **Controls:** PDT accounting, day-position classification, session flattening, risk
  limits, execution gates, and audit support are still implemented. Strategy configs
  cannot convert a day position into a swing hold or the reverse.
- **Review point:** S9 strategy-family registration and every subsequent strategy
  promotion review.
- **Removal criteria:** At least one day strategy passes the repository's backtest,
  paper-trading, cost, drawdown, and audit promotion gates.

### D4 - Production host remains abstract until operations phase

- **Status:** Active
- **Decision date:** 2026-07-21
- **Owner:** TradingFlow
- **Affected requirements:** OPS-01, OPS-08, RSK-04
- **Decision:** Keep the runtime host-agnostic through S10. Select the concrete
  production supervisor and host integration in S11.
- **Rationale:** Startup gating, graceful shutdown, health reporting, kill-file
  polling, persistence, and recovery semantics are application contracts and should
  not depend on choosing Linux `systemd`, a Windows service, or a container host too
  early.
- **Controls:** Paths are configuration-driven; lifecycle code uses the .NET generic
  host; no business component calls host-specific service APIs.
- **Review point:** S11 operations implementation.
- **Removal criteria:** A production host is selected and its thin supervision wrapper,
  deployment checks, NTP policy, and runbook are verified.

## Deviation Template

Copy this section for every future deviation.

### Dn - Short title

- **Status:** Proposed | Active | Retired
- **Decision date:** YYYY-MM-DD
- **Owner:** Name or team
- **Affected requirements:** Stable requirement IDs
- **Decision:** Exact behavior that differs from the specification default
- **Rationale:** Evidence-based reason for the deviation
- **Controls:** Compensating safeguards and observability
- **Review point:** Phase, date, or measurable trigger
- **Removal criteria:** Conditions that retire the deviation

## Resolved Production Boundaries

These decisions do not waive specification requirements; they constrain what is
allowed in the production composition root.

### PB1 - Alpaca-only production broker

- **Decision date:** 2026-07-21
- **Decision:** Retain the eToro client source code, but do not register or load it in
  paper/live production profiles. Production startup, secrets, account validation,
  market data, and order routing use Alpaca only.
- **Verification point:** S3 adds a production-composition test proving no eToro
  service or credential is resolved by paper/live startup.
