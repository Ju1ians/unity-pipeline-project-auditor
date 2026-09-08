# Unity Pipeline Project Auditor Extension

Structured Project Auditor access for **official Unity Pipeline**.

This package is for tools that need to run Unity Project Auditor through Pipeline and consume findings programmatically without managing CSV files. It exposes audits as bounded, paginated JSON with stable scan IDs and explicit lifecycle control, making it useful for editor tooling, CI, automation, and other Pipeline clients.

Unity Pipeline already provides native `audit` / `audit_status` commands with CSV output. This extension complements those commands with a structured API-oriented interface; it does not replace the native integration.

## Install

In Unity Package Manager, choose **Install package from Git URL**:

```text
https://github.com/Ju1ians/unity-pipeline-project-auditor.git#v0.1.0
