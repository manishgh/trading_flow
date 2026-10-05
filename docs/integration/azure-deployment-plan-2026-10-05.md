# TradingFlow Azure deployment plan

Draft dated 5 October 2026. This plans an initial paper deployment of the current C# Web/API and hosted runtime. No Azure resources were created. Android and broker execution acceptance remain separate gates; Firebase still needs project setup.

## Recommended first deployment

Use one TradingFlow runtime owner in an AKS paper namespace, with an Azure managed disk for SQLite and mutable files. Keep the current strategy engine, durable jobs, order journal, fencing leases and recovery logic together initially. This accepts a maintenance interruption during owner changes; it does not promise active-active availability.

SQLite WAL requires readers and writers on one host. Azure Disk supports ReadWriteOnce access to one node; that alone does not prevent several pods on that node, so also enforce one replica and updates without overlapping runtime owners. Shared Azure Files plus independently writing Web/worker replicas are unsuitable for the current database. [SQLite WAL](https://www.sqlite.org/wal.html), [AKS CSI storage](https://learn.microsoft.com/en-us/azure/aks/csi-storage-drivers).

`TradingFlow.Web/Program.cs` currently registers preparation, backtest, optimization and paper handlers and runtime hosted services. A Kubernetes label or environment variable does not establish process-role separation. Splitting these roles needs implementation and tests for queue ownership, state fencing and broker recovery.

## Existing files need changes before deployment

| Existing item | Required change |
| --- | --- |
| `deploy/aks/production/web.yaml`: two replicas | One owner using StatefulSet ownership or a controlled deployment with no overlap. No HPA for this runtime. |
| `storage.yaml`: shared SMB data PVC | Azure Disk for SQLite/WAL/SHM, keys and operational state. Retain the disk on workload deletion; verify mount ownership and permissions. |
| Separate paper shards and warmup replicas | Do not launch additional owners against the same SQLite file. Start with Web-hosted handlers; inspect separate services before enabling them. |
| Historic Go news and TradingView examples | Retain current declared C# collection ownership. A provider migration needs separate verification. |
| NGINX ingress examples | Choose supported Gateway API ingress; verify region, cluster version and required features. |
| Renderer/image defaults | Pin image digests and source/configuration identities. Render into a fresh directory; verify absolute paths before replacing generated outputs. |
| `/health` as sole readiness evidence | Separate process health from `/health/trading-readiness`, which reports synchronization, dispatch recovery and admission blocks. Provider outages must not trigger restart loops. |

Prefer managed application-routing Gateway API where its supported features meet the application requirements. Azure's managed NGINX support ends after November 2026. Test long-lived SSE, proxy timeouts, forwarded HTTPS and cookie behavior. Add explicit supported controls for rate/request-size limits where needed; the managed implementation documents limitations. [AKS Gateway API](https://learn.microsoft.com/en-us/azure/aks/app-routing-gateway-api).

## Infrastructure and secrets

Proposed resources are AKS, ACR, Key Vault, Azure Disk, Blob Storage for asynchronous archival/backups, and Azure Monitor. Subscription, region, DNS, cost ceiling, retention and recovery objectives need owner decisions before provisioning. Size resources from measured preparation/backtest workloads, rather than the historic node-size examples.

Use AKS OIDC and Microsoft Entra Workload ID with a dedicated paper service account and narrowly scoped vault/storage permissions. Key Vault CSI can mount secrets, but a mounted file does not automatically become an application environment setting: implement a supported loader or review the secret-to-environment path. Keep future live identities and credentials separate; the first deployment receives no live keys. [AKS Workload ID](https://learn.microsoft.com/en-us/azure/aks/workload-identity-overview), [Key Vault CSI](https://learn.microsoft.com/en-us/azure/aks/csi-secrets-store-identity-access).

Persist ASP.NET Data Protection keys under the data root with application name `TradingFlow.Web`. Restart and rollback must preserve cookies and protected review tokens. Encrypt/restrict backups containing those keys. Exclude local settings, review passwords, databases, provider keys and Firebase service-account keys from images and source control.

## Storage and shutdown

Persist the database and journals, durable job/command state, keys, candles, raw evidence, immutable strategies, result manifests and generated configurations referenced by jobs. Separate disposable caches from evidence required for resumption. Test Linux paths, case handling, container permissions and published configuration availability; the existing Web Dockerfile alone does not establish them.

Archive closed immutable files to Blob asynchronously with hashes, manifests and retry receipts. Keep Blob writes outside the candle/strategy/broker hot path.

Set Kubernetes termination grace longer than the configured durable drain timeout plus measured broker reconciliation/protection time. Stop new admission, drain, verify broker-resting protection for exposure, then relinquish ownership. Pod restart and a disk mount do not prove outstanding orders were reconciled.

Run EF migrations once before the sole runtime resumes, with a consistent backup and reviewed schema rollback. An older image may not understand a newer schema. Never reset current order/account state to make an image rollback work.

## Deployment sequence

1. **Prepare the artifact.** Close independent review, offline/browser checks and container/Linux startup checks. Record source commit, image digest, schema version and configuration identities. Keep Android and Firebase acceptance separate.
2. **Resolve runtime configuration gaps.** Establish a supported production role and seed-free preview mode. `TRADINGFLOW_UI_TEST_MODE` seeds a test account and disables workers; it must not become Azure production configuration. Normal startup requires the expected paper account binding. Do not invent a toggle absent from source.
3. **Prepare infrastructure changes.** Update Bicep/manifests for one owner, disk storage, identities, ingress, probes, limits and shutdown. Render into a new directory; run Bicep checks, Kubernetes schema checks and Azure what-if. Resource creation follows review of that concrete result.
4. **Deploy isolated preview.** Use an isolated database and no live secrets. Verify public Workspace/News/Earnings, sign-in/CSRF, private API access, SSE/reconnect, retained commands and restoration. Do not expose the original local trading database as a preview.
5. **Review paper operation.** Bind the exact account and strategies. Verify provider entitlements, previous-close values, warmup, Finviz discovery and strategy-required daily/sub-daily evidence. Inventory existing broker orders/positions before moving their owner.
6. **Transfer ownership.** Gracefully stop the old owner, preserve state/evidence and confirm exposure protection. Transfer consistent state, start exactly one Azure owner, and require synchronization, recovery and reconciliation before entry admission. No overlapping old/new owners.
7. **Run separately authorized execution acceptance.** Discovery → preparation → candidate → reviewed order → fill → protection → exit → reconciliation needs explicit permission to place paper orders. This plan does not grant it. Keep live locked under existing promotion rules.
8. **Rehearse failure and rollback.** Test network loss, token expiry, leases, crash/restart, drain and restore. Preserve current journal/account state during image rollback. Reconcile ambiguous broker outcomes instead of restoring an older DB over them.

## Backup and monitoring

Use application-consistent SQLite backups or a drained consistent snapshot. Do not copy only the main DB during WAL writes. Include job references, strategies, keys and evidence manifests; encrypt uploads and restore into an isolated runtime that cannot submit orders. AKS Backup covers supported resources/volumes but does not establish database consistency on its own. Agree RPO/RTO after a restore drill. [AKS Backup](https://learn.microsoft.com/en-us/azure/backup/azure-kubernetes-service-cluster-backup).

Observe provider failures, queue age, lease renewal, recovery, stream lag, pending commands, disk/WAL growth, backup age and reconciliation/protection failures. Use structured logs, bounded metric labels, secret/token redaction, retention and cost limits. [Azure Monitor](https://learn.microsoft.com/en-us/azure/azure-monitor/containers/kubernetes-monitoring-overview).

## Android, ngrok and Firebase

Keep local review on the active ngrok HTTPS address for now, as requested. The old `2b04-…` address expired and tunnel restarts may allocate a new one. Native Settings must match the active tunnel. Azure later gets a stable DNS endpoint.

Android was paused while the phone was needed and resumed on the latest instruction. ADB showed no device at resume; the phone subsequently reconnected. Its foreground was Gmail, so current-source installation and phone checks await TradingFlowReview being left open and idle. Build success and connectivity do not establish accessibility or reconnect acceptance.

Firebase needs project setup and package-specific app registrations: production `com.manishgh.tradingflow`, review `com.manishgh.tradingflow.integration`. Use matching `google-services.json` files. Authenticated device registration, durable outbox, FCM HTTP v1, token rotation/revocation and foreground/background/cold-launch behavior still require implementation and delivery tests. Connected polling is not background push. Keep push disabled until configuration exists; see `firebase-background-push.md`.

## Later scaling

Before multiple owners, select and implement a server database and verify transactional, idempotent and fenced repository behavior against it. Then separate Web/UI from workers, assign ticker shards through durable ownership, and verify provider limits and restores. Kubernetes leases or shared storage alone do not migrate database semantics. The separate Python ML system remains advisory, with its own deployment and model-approval process.
