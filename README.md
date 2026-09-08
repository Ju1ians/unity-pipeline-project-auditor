# Unity Pipeline Project Auditor Extension

Standalone structured Project Auditor integration for **official Unity Pipeline**.
No Gateway, Bridge, existing Pipeline Extensions, CSV parser, or external service is required.

## Install

In Unity Package Manager, choose **Install package from Git URL**:

```text
https://github.com/Ju1ians/unity-pipeline-project-auditor.git#v0.1.0
```

Requires Pipeline 0.6.0-exp.1 and a compatible Project Auditor public API.
On Editors with built-in Project Auditor, install `com.unity.project-auditor-rules`
as well. Missing Auditor/rules is reported as unavailable, never zero findings.
The optional API adapter allows this package to compile without Auditor installed.
It uses public API reflection because the standalone package and built-in module
use different assembly names and category representations. No private API is used.

## Commands

| Command | Arguments | Result |
| --- | --- | --- |
| `project_audit_start` | optional comma-separated `categories` | `scan_id`, queued status |
| `project_audit_status` | optional `scan_id` (latest if omitted) | state, timestamps, issue count, module/scan evidence |
| `project_audit_results` | `scan_id`, `offset=0`, `limit=50`, optional `category`, `severity` | frozen structured items, totals, `next_offset` |
| `project_audit_dispose` | `scan_id` | releases terminal results, never deletes project files |

Use any official Pipeline client to discover these commands and invoke them.
Start, poll until terminal, then page until `next_offset` is null. A page may be
shorter than `limit` because response bytes are bounded. Match category/severity
filters by exact name, case-insensitively. Invalid start categories return valid names.

Items include rule ID, category, severity, description, relative path, filename,
line, log level, recommendation, areas, custom properties, and upgrade metadata.
Unavailable optional fields are null and listed in `unsupported_fields`. Values
are explicitly mapped, not serialized through arbitrary Unity object graphs.
Large strings/arrays are marked truncated; non-scalar custom values are omitted
with warnings, and large integers are represented as exact strings.
Only diagnostic issues are retained, not raw inventory insights.

## Lifecycle and limits

- One queued/running scan per Editor domain; start returns before analysis.
- `AuditAsync` can still occupy the main thread during compilation. Status uses
  a thread-safe snapshot, but Editor/Pipeline scheduling can affect responsiveness.
- Start requires stopped Play Mode and no active import/compilation. No fixes,
  export paths, platform switching, project-setting writes, or code execution API.
- Terminal states: completed, failed, unavailable. No completed module evidence
  means unavailable, not a clean report.
- Retain at most 3 terminal/active scan records, terminal TTL 30 minutes.
- At most 10,000 diagnostic rows and 8 MiB estimated snapshot storage per scan.
  Oversized scans fail explicitly; narrow categories rather than trusting partial results.
- Page limit 100 items and approximately 128 KiB JSON. Individual fields are bounded.
- Completed reports are converted to bounded immutable JSON snapshots. Raw Report
  dependency graphs are **not retained**. Auditor's own analysis allocations are
  outside this retention bound.
- IDs are session-qualified and never reused. Domain reload, Editor restart,
  eviction, expiry, or disposal yields `AUDIT_SCAN_NOT_FOUND` for the old ID.
- Disposal cannot cancel active scans. A client timeout is not cancellation.
- Scans/results are shared by clients of the same Editor. Retain evidence before
  disposal. There is no per-agent ownership in this standalone Unity package.
- Findings are historical static-analysis evidence, not an authoritative statement
  about later changes, runtime correctness, or a linked repository not loaded by Unity.

## Gateway integration (optional)

Gateway discovers the four commands through the existing command endpoint; no new
ChatGPT Action operations are needed. The companion Gateway integration gives
Planner, Builder, and Reviewer `unity_audit` for start/dispose and `unity_read` for
status/results. It requires compatibility for start and rejects these commands
inside batch. Legacy Pipeline `audit`/CSV behavior is left untouched.

## Tests

Add this package to `testables` in a disposable Unity project's manifest and run
`UnityPipeline.ProjectAuditor.Tests` in EditMode. Tests cover lifetime, bounds,
immutable pagination, mapping, and a real ProjectSetting scan. The live test
requires Auditor/rules. Do not use a user's working project as the test fixture.

Initial validation targets Unity 6000.6.0f1, Pipeline 0.6.0-exp.1 and rules 2.0.0.
Older standalone Auditor deployments are API-adapted but not a validated matrix.

This package is community-maintained, not an official Unity product.
