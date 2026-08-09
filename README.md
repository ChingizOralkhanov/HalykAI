# Halyk AI Challenge — agent

Covenant checker: reads the document pack, recomputes each covenant from the ledger and
writes `submission.json`.

## Layout

| Project | Contains |
|---|---|
| `src/Halyk.Core` | ledger and submission models, template-shaped answer sheet, validator, scorer |
| `src/Halyk.Ingest` | ledger CSV reader, scenario/account map, PdfPig text extraction, document inventory |
| `src/Halyk.Llm` | model client, disk cache, extraction schemas (next) |
| `src/Halyk.Rules` | covenant rule engine, leave-one-out evidence search (next) |
| `src/Halyk.Cli` | commands |
| `tests/Halyk.Tests` | unit tests over synthetic fixtures only |

## Commands

```
dotnet run --project src/Halyk.Cli -- ledger   --dataset <dataset-dir>
dotnet run --project src/Halyk.Cli -- ingest   --dataset <dataset-dir> --out work [--force]
dotnet run --project src/Halyk.Cli -- init     --template <template.json> --out work/submission.json --team <t> --email <e> --model <m>
dotnet run --project src/Halyk.Cli -- validate --submission work/submission.json --template <template.json>
dotnet run --project src/Halyk.Cli -- score    --submission work/submission.json --key <ground_truth.json>
```

`ingest` caches by file hash, so re-runs only touch documents that changed.

## Rules that the code has to keep

- The scenario list comes from the submission template, never from a constant. The private
  dataset may carry a different set of borrowers.
- Nothing borrower-specific is ever hardcoded: no thresholds, no transaction ids, no lookups
  into a ground truth file. Every number in the submission is computed at run time.
- The model extracts facts from documents; all arithmetic, threshold comparisons and evidence
  selection happen in code.
- An empty cell and a wrong cell score the same, so every cell is filled before upload.

## Dataset facts found so far

- 12 borrowers in the template (`P1`–`P10`, `B1`, `B4`); the ledger also holds ~550 unrelated
  `ACC-9xxx` accounts as noise.
- 1473 ledger rows, 1445 USD and 28 EUR, so an FX rate has to come from the documents.
- Two rows ship with a blank amount (`TXN-P7-0033`, `TXN-P8-0031`); they stay null rather than
  being zeroed, because zeroing silently changes every aggregate.
- 202 documents, 845 pages. Four of them carry image-only pages and need a vision-capable
  model: `f3fa6d20c8a1.pdf` (all 3 pages), plus `2ed0b2ee4b57.pdf` (pages 3-4),
  `63e162bd710b.pdf` (page 2) and `aaf665cbc612.pdf` (page 2), which are text documents with a
  scanned page inside. A whole-document average would have hidden the last three.

## Environment notes

- Smart App Control is on. It blocks the unsigned `Release` build of the CLI with
  `An Application Control policy has blocked this file (0x800711C7)`; the `Debug` build runs.
  Either stay on `Debug` or turn Smart App Control off well before the run, not during it.
- Timings on this machine: cold ingest 1.7 s, warm 0.12 s. Ingest is parallel over the file
  list and the CLI runs with Server GC — the two must stay together, because parallel
  extraction under workstation GC is measurably slower than serial.
