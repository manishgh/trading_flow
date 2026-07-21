# Dormant eToro Reference Client

This project is retained as reference source only. It is intentionally excluded from:

- `TradingFlow.sln` and `TradingFlow.slnx`;
- all production project references and dependency-injection composition;
- paper/live startup validation and order routing;
- deployment manifests and runtime secret loading; and
- CI build and release artifacts.

Alpaca is the only production broker integration. Activating eToro requires an approved
production-spec deviation, a separate security review, current API-contract verification,
and dedicated tests. Do not add this project to a production host as a convenience.
