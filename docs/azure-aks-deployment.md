# Azure Kubernetes Service Deployment

This is the primary Azure deployment path for TradingFlow.

Services:

- ASP.NET Core UI: `TradingFlow.Web`
- C# trading/backtest worker: `TradingFlow.Worker` as a Kubernetes `Job`
- Go news/sentiment service: `sidecars/news-sentiment-go`

## Architecture

```text
Azure Container Registry
  tradingflow-web:latest
  tradingflow-service:latest
  tradingflow-news:latest

AKS
  namespace trading-flow
  deployment/trading-flow-web
  deployment/trading-flow-news
  job/trading-flow-backtest
  pvc/trading-flow-data
  pvc/trading-flow-ui-runs
  ingress/trading-flow-web

Azure Files CSI
  /app/data
  /app/configs/backtest/ui-runs

Kubernetes Secrets
  trading-flow-demo-secrets
    ALPACA_KEY_ID
    ALPACA_SECRET_KEY
    TRADINGVIEW_WEBHOOK_SECRET
  trading-flow-live-secrets
    ALPACA_KEY_ID
    ALPACA_SECRET_KEY
    TRADINGVIEW_WEBHOOK_SECRET
```

## 1. Create Azure Resources

```powershell
$sub = "<subscription-id>"
$rg = "rg-tradingflow-aks-dev"
$loc = "eastus"
$aks = "aks-tradingflow-dev"
$acr = "tradingflowdevacr"

az account set --subscription $sub
az group create -n $rg -l $loc
az acr create -g $rg -n $acr --sku Basic
az aks create `
  -g $rg `
  -n $aks `
  --node-count 2 `
  --node-vm-size Standard_D4s_v5 `
  --enable-managed-identity `
  --attach-acr $acr `
  --enable-addons monitoring `
  --generate-ssh-keys

az aks get-credentials -g $rg -n $aks
```

## 2. Build Images

```powershell
az acr build -r $acr -t tradingflow-web:latest -f deploy/docker/Dockerfile.web .
az acr build -r $acr -t tradingflow-service:latest -f deploy/docker/Dockerfile.trading-service .
az acr build -r $acr -t tradingflow-news:latest -f deploy/docker/Dockerfile.news .
```

Get the ACR login server:

```powershell
$loginServer = az acr show -g $rg -n $acr --query loginServer -o tsv
```

## 3. Prepare Manifests

Copy the template secret:

```powershell
Copy-Item deploy/aks/secret.template.yaml deploy/aks/secret.yaml
```

Edit `deploy/aks/secret.yaml` and set only the secret block for the environment you are deploying.

Demo/paper:

```text
ALPACA_KEY_ID
ALPACA_SECRET_KEY
TRADINGVIEW_WEBHOOK_SECRET
```

Live:

```text
ALPACA_KEY_ID
ALPACA_SECRET_KEY
```

Do not mount paper and live Alpaca secrets into the same pod. Paper and live workloads should use separate Kubernetes secrets or Key Vault references.

Replace placeholder images:

```powershell
(Get-ChildItem deploy/aks/*.yaml) | ForEach-Object {
  (Get-Content $_.FullName) `
    -replace 'REPLACE_ACR_LOGIN_SERVER', $loginServer |
    Set-Content $_.FullName
}
```

For real environments, use Azure Key Vault CSI driver or External Secrets instead of committing Kubernetes secret files.

## 4. Install Ingress

Use NGINX ingress:

```powershell
helm repo add ingress-nginx https://kubernetes.github.io/ingress-nginx
helm repo update
helm upgrade --install ingress-nginx ingress-nginx/ingress-nginx `
  --namespace ingress-nginx `
  --create-namespace
```

Get the public IP:

```powershell
kubectl get svc -n ingress-nginx ingress-nginx-controller
```

Point DNS for `trading-flow.example.com` to that IP, then update `deploy/aks/web-ingress.yaml`.

## 5. Deploy

Apply base manifests:

```powershell
kubectl apply -f deploy/aks/namespace.yaml
kubectl apply -f deploy/aks/configmap.yaml
kubectl apply -f deploy/aks/secret.yaml
kubectl apply -f deploy/aks/storage.yaml
kubectl apply -f deploy/aks/news.yaml
kubectl apply -f deploy/aks/web.yaml
kubectl apply -f deploy/aks/web-ingress.yaml
```

Check status:

```powershell
kubectl get pods,svc,ingress,pvc -n trading-flow
kubectl logs deploy/trading-flow-web -n trading-flow
kubectl logs deploy/trading-flow-news -n trading-flow
```

## 6. Run C# Backtest Job

Run the one-shot job:

```powershell
kubectl apply -f deploy/aks/trading-service-job.yaml
kubectl logs job/trading-flow-backtest -n trading-flow
```

The job runs the CLI service image. Before applying the template, persist a
wishlist-resolved run config and replace `REPLACE_WITH_GENERATED_RUN_CONFIG`
in `deploy/aks/trading-service-job.yaml` with its mounted path:

```text
TRADINGFLOW_RUN_CONFIGS=REPLACE_WITH_GENERATED_RUN_CONFIG
TRADINGFLOW_RESULT_OWNER=worker
TRADINGFLOW_MAX_PARALLEL_RUNS=1
```

Rerun it:

```powershell
kubectl delete job trading-flow-backtest -n trading-flow
kubectl apply -f deploy/aks/trading-service-job.yaml
```

For schedules, convert it to a `CronJob` after you decide the cadence.

## 7. Verify

Web:

```powershell
kubectl port-forward svc/trading-flow-web 5088:80 -n trading-flow
```

Open:

```text
http://127.0.0.1:5088
```

News:

```powershell
kubectl port-forward svc/trading-flow-news 8080:8080 -n trading-flow
Invoke-WebRequest http://127.0.0.1:8080/health
```

Paper validation:

- Open `/Paper`
- Run `Validate Paper Environment`
- Confirm Alpaca account and positions checks return OK

## 8. Production Safety

- Keep live Alpaca secrets empty until explicitly approved.
- Mount either paper secrets or live secrets, never both.
- Keep live trading config `allow_trading: false` until paper is stable.
- Do not enable automatic retry for broker writes unless idempotency proves the first write did not reach the broker.
- Keep separate Polly bulkheads for market data reads and broker writes so one flow cannot starve the other.
- Web UI and worker results use separate owners (`web`, `worker`) under `data/backtest/results/{owner}/portfolio`.
- Generated UI run configs are persisted on `pvc/trading-flow-ui-runs`; market data and result JSON are persisted on `pvc/trading-flow-data`.
- Use namespace separation for `dev`, `paper`, and `live`.
- Rotate demo keys before any shared deployment.
- Use Key Vault CSI or External Secrets for production.
- Add network policies after service communication is finalized.

## 9. Current Local URL

The local development server is running at:

```text
http://127.0.0.1:5088
```
