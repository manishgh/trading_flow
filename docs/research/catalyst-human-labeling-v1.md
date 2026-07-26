# Catalyst Human Labeling v1

This document is the binding annotation guide for the
`tradingflow.catalyst-human-labels.v1` exchange format. It implements the
human ground-truth requirements in NWS-11. Human judgment is required; no
provider or language model may generate these labels.

## Immutable Input

Label only work items from one deterministic
`tradingflow.catalyst-label-work-items.v2` export. Each work item identifies:

- the committed source news dataset;
- provider article and revision identifiers;
- the SHA-256 hash of the exact revision content;
- provider publication/update time and the observed availability time;
- headline, summary, URL, symbols, and provider categories.

Do not edit a work item. If source text changes, export the new immutable
revision as a different work item. Labels are rejected when the work-item ID or
revision-content hash does not match the export.

The production sampling contract is `year-symbol-round-robin-v1`:

- only the earliest available stored revision of each provider article is eligible;
- candidates are stratified by availability year and primary symbol;
- deterministic round-robin selection maximizes year/symbol coverage;
- the exact source dataset, sampling version, target size, and export hash are
  embedded in the work-item header and label template;
- requesting more items than are eligible fails instead of silently shrinking the
  sample.

Generate or replay the current package with:

```powershell
dotnet run --project src/TradingFlow.Cli/TradingFlow.Cli.csproj -- `
  evidence-export-catalyst-label-sample `
  --dataset-id <committed-news-dataset-id> `
  --sample-size 500 `
  --catalog data/research/evidence/catalog.db `
  --artifact-root data/research/evidence/objects `
  --output-dir data/research/labeling
```

Replaying the command verifies exact existing bytes. It refuses to overwrite a
different work-item, label, or package file.

## Categories

Choose exactly one category:

- `earnings`: reported revenue, earnings, margins, or other completed-period results.
- `guidance`: forward guidance, outlook, forecasts, or guidance withdrawal.
- `merger_acquisition`: mergers, acquisitions, divestitures, takeover bids, or deal termination.
- `regulatory`: regulator decisions, approvals, investigations, or compliance actions.
- `capital_structure`: offerings, buybacks, dividends, debt, dilution, or restructuring.
- `major_commercial_event`: material contracts, customers, partnerships, launches, or cancellations.
- `management`: executive or board appointment, departure, incapacity, or governance event.
- `analyst_action`: analyst rating, target, initiation, or estimate change.
- `legal`: litigation, judgment, settlement, or formal legal claim.
- `operational`: production, supply, safety, outage, capacity, or operating disruption.
- `macro_sector`: macroeconomic or sector information whose principal subject is not one issuer.
- `promotional_low_information`: promotional, recycled, or attention-seeking content with little new evidence.
- `unknown`: insufficient evidence to assign another category.

Use the event’s economic substance, not the provider’s channel label.

## Direction And Materiality

`direction` is the expected directional implication at the item’s observed
availability time:

- `positive`
- `negative`
- `neutral`
- `ambiguous`

Set `direction_is_clear` to `true` only when a reasonable annotator can infer
one clear issuer-price direction from the available revision without future
market data. Never use later candles, returns, revisions, commentary, or
outcomes to decide a label.

`materiality` is coarse and ex ante:

- `low`: unlikely to alter a reasonable investor’s valuation or risk view.
- `medium`: meaningful but not thesis-changing.
- `high`: plausibly changes valuation, solvency, strategic control, guidance,
  regulatory status, or a major commercial/operational assumption.

## Annotation Document

Import one UTF-8 JSON document:

```json
{
  "format_version": "tradingflow.catalyst-human-labels.v1",
  "source_news_dataset_id": "<dataset id from export>",
  "work_item_export_sha256": "<export sha256>",
  "annotations": [
    {
      "work_item_id": "<work item id>",
      "news_revision_content_sha256": "<exact revision sha256>",
      "annotator_id": "<stable human annotator id>",
      "labeled_at_utc": "2026-07-25T12:00:00Z",
      "category": "earnings",
      "direction": "positive",
      "direction_is_clear": true,
      "materiality": "high"
    }
  ],
  "adjudications": []
}
```

Use stable, non-secret annotator IDs and real UTC completion timestamps.
Unknown fields, local timestamps, duplicate annotations, conflicting labels
from the same annotator, unknown work items, and mismatched content hashes are
invalid.

## Double Labeling And Adjudication

At least 100 distinct work items must be independently labeled by two or more
annotators. Annotators must not coordinate before submitting their labels.

Matching independent annotations resolve by consensus. Any disagreement in
category, direction, direction clarity, or materiality remains excluded from
the published resolved rows until an adjudicator records:

- stable adjudicator ID;
- UTC adjudication timestamp after all contributing annotations;
- final category, direction, clarity, and materiality;
- a substantive rationale based only on the exact immutable revision.

Do not adjudicate an item whose independent labels already agree.

## Readiness

Catalyst promotion remains fail-closed until the verified committed dataset has:

- at least 500 valid resolved labels;
- coverage of every category above;
- at least 100 independently double-labeled articles;
- zero unresolved annotation conflicts;
- immutable work-item, label-document, and Parquet artifacts that pass
  content-hash and catalog-lineage verification.

The verifier recomputes counts from committed rows and compares them with the
immutable manifest. Missing, weakened, malformed, or inconsistent readiness
metadata blocks promotion. NWS-12 classifier-accuracy requirements are a
separate later gate and are not satisfied by completing NWS-11.

After labeling and adjudication, publish the resolved ground truth with:

```powershell
dotnet run --project src/TradingFlow.Cli/TradingFlow.Cli.csproj -- `
  evidence-import-catalyst-ground-truth `
  --dataset-id <committed-news-dataset-id> `
  --sample-size 500 `
  --label-document <completed-label-document.json> `
  --catalog data/research/evidence/catalog.db `
  --artifact-root data/research/evidence/objects `
  --run-id <frozen-run-id> `
  --config-hash <lowercase-sha256> `
  --code-version <git-commit-or-build-id> `
  --builder-version catalyst-ground-truth-builder-v1 `
  --created-at-utc <utc-timestamp>
```

The import recreates the exact deterministic sample before validating the label
document. A document bound to any other export hash is rejected.
