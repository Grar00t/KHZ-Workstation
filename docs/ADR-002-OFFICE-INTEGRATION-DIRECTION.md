# ADR-002 - Office Integration Direction

Status: **PROTOTYPE DIRECTION, NOT PRODUCTION APPROVAL**

Date: 2026-09-01

## Context

ADR-001 selected an unmodified external LibreOffice process for the original acceptance build because it was the Office engine that received the full local compatibility corpus and deterministic round-trip verification in that build.

That evidence remains valid for the exact tested baseline. It is not rewritten or discarded by this ADR.

After that baseline, KHZ added an experimental ONLYOFFICE embedding path under `tools/office-spike/` and a WPF WebView2 host that talks only to the local KHZ Office gateway on port 8090.

The product direction is therefore no longer accurately described as "LibreOffice is the only current Office path". The current architecture should instead be described as a **replaceable Office-engine boundary with two different evidence classes**:

- LibreOffice: historical external-process acceptance baseline.
- ONLYOFFICE: current embedded integration spike.

## Decision

KHZ will keep the Office layer replaceable.

For current Windows UI development, the preferred embedding experiment is the local ONLYOFFICE Document Server spike behind the KHZ loopback gateway.

This is **not** a production deployment decision and it does not replace the LibreOffice compatibility evidence.

The Office layer must remain separable from KHZ-owned concerns:

```text
KHZ owns
- workspace identity
- files and folders
- search
- tasks
- structured data
- repositories
- terminal policy
- activity/audit
- backup/restore
- capability policy

Office engine owns
- DOCX editing semantics
- XLSX editing semantics
- PPTX editing semantics
- PDF/form capabilities provided by that engine
```

## Current ONLYOFFICE spike boundary

The spike launcher:

- pins the `onlyoffice/documentserver` image by digest;
- creates an internal Docker network;
- exposes the Document Server API to the host through loopback only;
- exposes the KHZ gateway at `127.0.0.1:8090`;
- disables plugins and metrics in the spike container;
- prints `JWT_DISABLED_SPIKE_ONLY` explicitly.

The WPF host allows Office WebView navigation only to `localhost` / `127.0.0.1` on port 8090 under the local-office capability policy.

These controls are useful prototype boundaries. They are **not sufficient evidence of a production-hardened deployment**.

## Production gate

Before KHZ can describe an embedded ONLYOFFICE configuration as production-ready, the following require direct evidence:

1. supported deployment topology for the intended Windows/local environment;
2. JWT/authentication enabled and verified;
3. document fetch/callback authorization reviewed;
4. no unintended non-loopback egress under the chosen deployment profile;
5. update and vulnerability-management path documented;
6. license/trademark/distribution obligations reviewed for the exact packaging model;
7. backup/restore behavior proven with actively edited Office files;
8. real Windows runtime capture and save/reopen proof;
9. failure behavior proven when the Office runtime is unavailable;
10. the `IOfficeEngine` or equivalent replaceable boundary preserved.

Until those gates are satisfied, documentation must call this an **embedded spike**, **prototype**, or **integration experiment**.

## Relationship to ADR-001

ADR-001 remains historical evidence for the original build and its LibreOffice corpus.

This ADR supersedes only the product-direction statement that LibreOffice is the sole selected path for future UI integration.

It does not claim that ONLYOFFICE has already passed the same compatibility corpus or security verification.

## Branding and provenance

KHZ must not imply that it created the third-party Office editor.

Where ONLYOFFICE is used, the editor remains ONLYOFFICE and its notices, licenses, and trademarks remain attributable to their respective owners. The same principle applies to LibreOffice or any future replaceable Office engine.

KHZ's product value is the surrounding local-first workspace, not ownership of the Office editing engine.
