# Azure Deployment

Primary Azure deployment is AKS. Use [azure-aks-deployment.md](azure-aks-deployment.md) for the Kubernetes deployment.

This document keeps the Azure Container Apps option for smaller/simple environments. It deploys three TradingFlow services to Azure Container Apps:

- `TradingFlow.Web`: ASP.NET Core UI for backtests and paper validation.
- `TradingFlow.Cli`: C# service/worker container used as an Azure Container Apps Job.
- `news-sentiment-go`: Go news/sentiment sidecar service.

The deployment keeps one implementation for demo/live but separates secrets by environment variables and Azure Container App secrets.

## Local Service

The local UI is currently available at:

```text
http://127.0.0.1:5088
```

Run it manually:

```powershell
dotnet run --project src\TradingFlow.Web --urls http://127.0.0.1:5088
```

## Azure Shape

```text
Azure Container Registry
  tradingflow-web
  tradingflow-service
  tradingflow-news

Azure Container Apps Environment
  tradingflow-web      external ingress
  tradingflow-news     internal ingress
  tradingflow-job      manual/scheduled job

Azure Storage Files
  /app/data
  /app/configs/backtest/ui-runs

Log Analytics
  app logs and job logs

Container App Secrets
  ETORO_DEMO_API_KEY
  ETORO_DEMO_USER_KEY
  optional live keys kept disabled until approved
```

## Prerequisites

```powershell
az login
az extension add --name containerapp --upgrade
az provider register --namespace Microsoft.App
az provider register --namespace Microsoft.OperationalInsights
az provider register --namespace Microsoft.Storage
```

Docker must be available locally, or use `az acr build`.

## Build Images

Set names:

```powershell
$sub = "<subscription-id>"
$rg = "rg-tradingflow-dev"
$loc = "eastus"
$acr = "tradingflowdevacr"
$prefix = "tradingflow-dev"

az account set --subscription $sub
az group create -n $rg -l $loc
az acr create -g $rg -n $acr --sku Basic --admin-enabled true
```

Build in ACR:

```powershell
az acr build -r $acr -t tradingflow-web:latest -f deploy/docker/Dockerfile.web .
az acr build -r $acr -t tradingflow-service:latest -f deploy/docker/Dockerfile.trading-service .
az acr build -r $acr -t tradingflow-news:latest -f deploy/docker/Dockerfile.news .
```

Image names:

```powershell
$loginServer = az acr show -g $rg -n $acr --query loginServer -o tsv
$webImage = "$loginServer/tradingflow-web:latest"
$serviceImage = "$loginServer/tradingflow-service:latest"
$newsImage = "$loginServer/tradingflow-news:latest"
```

## Deploy Container Apps

The included Bicep template creates:

- Log Analytics workspace
- Storage account and Azure Files shares
- Container Apps environment
- Web Container App
- News Container App
- Manual trading/backtest Container Apps Job

Deploy:

```powershell
$demoApiKey = Read-Host "ETORO_DEMO_API_KEY"
$demoUserKey = Read-Host "ETORO_DEMO_USER_KEY"

az deployment group create `
  -g $rg `
  -f deploy/azure/main.bicep `
  -p prefix=$prefix `
  -p webImage=$webImage `
  -p tradingServiceImage=$serviceImage `
  -p newsImage=$newsImage
```

Get the UI URL:

```powershell
az containerapp show -g $rg -n "$prefix-web" --query properties.configuration.ingress.fqdn -o tsv
```

## Run The C# Trading Job

The job defaults to:

```text
configs/backtest/finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry.yaml
```

Start it:

```powershell
az containerapp job start -g $rg -n "$prefix-trading-job"
```

View executions:

```powershell
az containerapp job execution list -g $rg -n "$prefix-trading-job" -o table
```

## Go News Service

The Go news service is internal-only in the Container Apps environment. Its health path is:

```text
GET /health
```

Runtime settings:

```text
PORT=8080
NEWS_ENABLED=true
NEWS_WORKER_COUNT=4
NEWS_CHANNEL_BUFFER=1024
NEWS_VETO_TTL_MINUTES=360
NEWS_NEGATIVE_VETO_THRESHOLD=-0.6
```

The web/trading services should call it by internal FQDN from the Bicep output.

## Secrets

Use separate secrets for demo and live:

```text
ETORO_DEMO_API_KEY
ETORO_DEMO_USER_KEY
ETORO_LIVE_API_KEY
ETORO_LIVE_USER_KEY
TRADINGVIEW_WEBHOOK_SECRET
```

Live trading must remain disabled until:

- paper trading is stable,
- order-router dry runs are reviewed,
- duplicate order protection is tested,
- live `allow_trading` is intentionally flipped,
- secrets are rotated out of local/dev values.

For production, prefer Azure Key Vault plus managed identity. The current Bicep passes secrets as Container App secrets for a first deployment.

## Storage

The deployment mounts Azure Files into:

```text
/app/data
/app/configs/backtest/ui-runs
```

This preserves:

- backtest raw/normalized/result data,
- paper result data,
- generated UI run configs.

## Logging

Container Apps logs go to Log Analytics.

Examples:

```powershell
az containerapp logs show -g $rg -n "$prefix-web" --follow
az containerapp logs show -g $rg -n "$prefix-news" --follow
az containerapp job logs show -g $rg -n "$prefix-trading-job"
```

## Deployment Safety

- Keep `live.allow_trading: false` in config until explicitly approved.
- Keep paper and live Alpaca credentials in separate Key Vault secrets.
- Do not auto-retry broker writes unless the order idempotency path can prove no duplicate order will be submitted.
- Use Container App revisions for rollbacks.

## Known Follow-Ups

- Add a dedicated long-running C# worker host instead of using `TradingFlow.Cli` as the job entry point.
- Add Key Vault references in Bicep once the Azure tenant/resource policies are known.
- Add GitHub Actions or Azure DevOps pipeline after repository sync is approved.
- Add health probes for paper broker readiness and Go news queue depth.
