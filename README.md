# Unity Pipeline Project Auditor Extension

Structured Project Auditor access for **official Unity Pipeline**.

This package is for tools that need to run Unity Project Auditor through Pipeline and consume findings programmatically without managing CSV files. It exposes audits as bounded, paginated JSON with stable scan IDs and explicit lifecycle control, making it useful for editor tooling, CI, automation, and other Pipeline clients.

Unity Pipeline already provides native `audit` / `audit_status` commands with CSV output. This extension complements those commands with a structured API-oriented interface; it does not replace the native integration.

## Install

In Unity Package Manager, choose **Install package from Git URL**:

```text
https://github.com/Ju1ians/unity-pipeline-project-auditor.git#v0.1.0
```

Requires Pipeline 0.6.0-exp.1 and a compatible Project Auditor public API.

On Editors with built-in Project Auditor, install `com.unity.project-auditor-rules` as well. Missing Auditor/rules is reported as unavailable, never zero findings.

The optional API adapter allows this package to compile without Auditor installed. It uses public API reflection because the standalone package and built-in module use different assembly names and category representations. No private API is used.

## Commands

| Command | Arguments | Result |
| --- | --- | --- |
| `project_audit_start` | optional comma-separated `categories` | `scan_id`, queued status |
| `project_audit_status` | optional `scan_id` (latest if omitted) | state, timestamps, issue count, module/scan evidence |
| `project_audit_results` | `scan_id`, `offset=0`, `limit=50`, optional `category`, `severity` | frozen structured items, totals, `next_offset` |
| `project_audit_dispose` | `scan_id` | releases terminal results, never deletes project files |

Use any official Pipeline client to discover these commands and invoke them.

Start a scan, poll until it reaches a terminal state, then page through `project_audit_results` until `next_offset` is null. A page may contain fewer items than `limit` because response bytes are bounded.

Category and severity filters match exact names case-insensitively. Invalid start categories return the valid category names.

Findings include:

- rule ID
- category
- severity
- description
- relative path
- filename
- line
- log level
- recommendation
- areas
- custom properties
- upgrade metadata

Unavailable optional fields are null and listed in `unsupported_fields`.

Values are explicitly mapped rather than serialized through arbitrary Unity object graphs. Large strings and arrays are marked as truncated, non-scalar custom values are omitted with warnings, and large integers are represented as exact strings.

Only diagnostic issues are retained, not raw inventory insights.

## Native Pipeline vs this extension

| Capability | Native Pipeline `audit` | This extension |
| --- | --- | --- |
| Run Unity Project Auditor | Yes | Yes |
| Poll scan status | Yes | Yes |
| Category selection | Yes | Yes |
| Result transport | CSV file | Structured JSON |
| Caller-selected export path | Yes | No |
| Paginated findings | No | Yes |
| Retained scan identity | Latest audit state | Up to 3 retained scans |
| Immutable result snapshot | No dedicated API | Yes |
| Explicit disposal | No | Yes |
| Module / coverage evidence | Limited | Yes |
| Additional optional Auditor fields | Limited CSV schema | Yes, when supported |

The native Pipeline commands remain available and unchanged. Use them when CSV output is the better fit.

## Lifecycle and limits

- One queued/running scan per Editor domain; start returns before analysis.
- `AuditAsync` can still occupy the main thread during compilation. Status uses a thread-safe snapshot, but Editor/Pipeline scheduling can affect responsiveness.
- Start requires stopped Play Mode and no active import/compilation.
- No fixes, caller-selected export paths, platform switching, project-setting writes, or arbitrary code-execution API are exposed by this extension.
- Terminal states are `completed`, `failed`, and `unavailable`.
- No completed module evidence means unavailable, not a clean report.
- At most 3 terminal/active scan records are retained.
- Terminal scan TTL is 30 minutes.
- At most 10,000 diagnostic rows and 8 MiB estimated snapshot storage are retained per scan.
- Oversized scans fail explicitly; narrow categories rather than trusting partial results.
- Page limit is 100 items and approximately 128 KiB JSON.
- Individual fields are bounded.
- Completed reports are converted to bounded immutable JSON snapshots.
- Raw Report dependency graphs are **not retained**.
- Project Auditor's own analysis allocations are outside this retention bound.
- IDs are session-qualified and never reused.
- Domain reload, Editor restart, eviction, expiry, or disposal yields `AUDIT_SCAN_NOT_FOUND` for the old ID.
- Disposal cannot cancel active scans.
- A client timeout is not cancellation.
- Scans/results are shared by clients of the same Editor.
- Findings are historical static-analysis evidence, not an authoritative statement about later changes, runtime correctness, or a linked repository not loaded by Unity.

## Tests

Add this package to `testables` in a disposable Unity project's manifest and run:

```text
UnityPipeline.ProjectAuditor.Tests
```

in EditMode.

Tests cover lifetime, bounds, immutable pagination, mapping, and a real ProjectSetting scan. The live test requires Project Auditor and its rules package.

Do not use a user's working project as the test fixture.

Initial validation targets:

- Unity 6000.6.0f1
- Pipeline 0.6.0-exp.1
- Project Auditor Rules 2.0.0

Older standalone Auditor deployments are API-adapted but are not part of the validated matrix.

This package is community-maintained and is not an official Unity product.
