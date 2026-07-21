# Credential Security

## Supported secret sources

Production and deployed paper environments load credentials from Azure Key Vault via
environment-specific managed identity. A process receives credentials for exactly one
profile; paper and live credentials are never mounted together.

Local hosted development uses .NET user-secrets:

```powershell
dotnet user-secrets --project src/TradingFlow.Web set "Alpaca:KeyId" "<paper-key-id>"
dotnet user-secrets --project src/TradingFlow.Web set "Alpaca:SecretKey" "<paper-secret-key>"

dotnet user-secrets --project src/TradingFlow.WarmupService set "Alpaca:KeyId" "<paper-key-id>"
dotnet user-secrets --project src/TradingFlow.WarmupService set "Alpaca:SecretKey" "<paper-secret-key>"
```

Command-line and service deployments may use these environment variables:

```text
ALPACA_KEY_ID
ALPACA_SECRET_KEY
FINVIZ_API_KEY
FINBERT_SENTIMENT_URL
```

Ignored `appsettings.local.json` files are a temporary migration fallback for existing
paper-test setups. They are not a production secret store.

## 2026-07 credential exposure record

Alpaca, eToro, and Finviz credentials were previously placed in a local launch profile
and must be treated as exposed because repository history and external clones cannot
be made trustworthy by deleting the current file. No credential values are repeated
in this record.

Required operator action:

1. Revoke and rotate the affected Alpaca paper/live credentials.
2. Revoke and rotate the affected eToro credentials even though eToro is dormant.
3. Revoke and rotate the Finviz Elite token.
4. Store replacements only in user-secrets for local development or Key Vault for
   deployment.
5. Verify paper read-only account access, Finviz ingestion, and SIP authentication with
   the replacement credentials before arming paper trading.

The current repository ignores launch profiles, local settings, environment files,
private-key files, rendered Kubernetes secrets, runtime databases, and logs. CI scans
tracked content for new secret material.
