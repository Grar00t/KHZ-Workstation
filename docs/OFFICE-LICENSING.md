# Office Licensing Boundary

This document records the current technical integration shapes for legal and distribution review. It is not legal advice.

## KHZ source

KHZ Workstation source is Apache-2.0 unless a file states otherwise.

Third-party Office engines remain separate projects with their own licenses, notices, trademarks, and distribution terms.

## Two evidence classes currently exist

### 1. LibreOffice acceptance baseline

The original acceptance build detected and launched an administrator/user-installed, unmodified LibreOffice executable.

For that baseline:

- LibreOffice binaries were not bundled in the KHZ package;
- KHZ did not link to LibreOffice libraries;
- KHZ did not embed LibreOfficeKit;
- KHZ did not fork or modify LibreOffice source;
- the external-process integration was the engine used for the compatibility corpus in that build.

This remains historical verification evidence.

### 2. ONLYOFFICE embedded spike

KHZ now also contains an experimental local ONLYOFFICE Document Server integration under `tools/office-spike/` plus a WPF WebView2 host that reaches the KHZ Office gateway on loopback port 8090.

This is a materially different integration form from launching an unmodified desktop executable.

The current spike:

- pins an `onlyoffice/documentserver` container image by digest;
- runs it on an internal Docker network;
- exposes the editor API to the host through loopback;
- disables plugins and metrics in the spike container;
- enables JWT and separate KHZ browser-session authentication;
- does not bundle the Document Server image or alter/white-label its source or UI.

Therefore the spike must not be represented as a production-approved packaging or licensing model.

## Integration forms are not equivalent

| Integration form | KHZ status |
|---|---|
| Use unmodified external LibreOffice executable | Historical acceptance baseline |
| Link to LibreOffice libraries | No |
| Embed LibreOfficeKit | No |
| Fork/modify LibreOffice source | No |
| Bundle LibreOffice binaries inside KHZ | No in the reviewed baseline |
| Detect unmodified ONLYOFFICE Desktop Editors as an external tool | Existing fallback path |
| Network-integrate ONLYOFFICE Document Server | Yes, experimental local spike |
| Modify ONLYOFFICE source | No evidence in this repository |
| White-label third-party Office UI as KHZ-owned technology | No |

## ONLYOFFICE review requirement

ONLYOFFICE Desktop Editors and Community/Document Server source are published under AGPLv3, and ONLYOFFICE also offers commercial editions/licenses for other integration and distribution models.

The exact obligations depend on the exact product, source modifications, packaging, network deployment, distribution model, and branding used by KHZ. Those questions must be reviewed against the current upstream license and commercial terms before KHZ distributes an embedded configuration.

The existence of an open-source spike does not by itself establish that every future KHZ distribution model is cleared.

## Branding and provenance

KHZ should not hide third-party authorship or imply ownership of the Office editor.

If ONLYOFFICE is shown or distributed as part of an approved integration, documentation should identify ONLYOFFICE as the document-editing layer and preserve applicable notices and trademarks.

If LibreOffice is used, the same principle applies.

The KHZ-owned product layer is the surrounding workspace: files, search, tasks, structured data, policy, audit/activity, backup/restore, repositories, terminal boundaries, and integrations.

## Production distribution gate

Before shipping any embedded Office engine with KHZ, record and review:

1. exact upstream product/edition and version;
2. exact license text and commercial agreement, if any;
3. whether binaries or source are redistributed;
4. whether source is modified or linked;
5. whether the integration is network/server based;
6. required attribution/notices;
7. trademark/branding treatment;
8. source-offer or corresponding-source obligations, where applicable;
9. update/security responsibility;
10. a reproducible inventory in the KHZ SBOM.

Until that review is complete, the local ONLYOFFICE path remains an integration spike rather than a declared shipping model.
