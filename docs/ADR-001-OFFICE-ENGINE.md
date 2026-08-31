# ADR-001 - Office Engine

Status: **ACCEPTED FOR THIS BUILD, REPLACEABLE**

Decision: use **LibreOffice as an unmodified out-of-process local Office engine** behind `IOfficeEngine`. Do not bundle LibreOffice binaries in `KHZ-Workstation.zip`.

Tested engine: `LibreOffice 25.2.3.2 520(Build:2)` on the Linux build environment.

Windows 11 runtime: **UNVERIFIED in this package**. LibreOffice's published system requirements state Windows 10/11 support.

## Evaluation method

Scores are 1 (poor fit) to 5 (strong fit) for the KHZ v1 constraints. Scores combine documented capabilities and local spike evidence where available. Only LibreOffice received the full local corpus spike in this build; other fidelity scores are not presented as empirical KHZ test results.

| Candidate | Local/offline | DOCX/XLSX/PPTX editor | Embedding | Windows deployment | License fit for unmodified external use | Server/Docker need | KHZ result |
|---|---:|---:|---:|---:|---:|---:|---|
| LibreOffice Desktop | 5 | 4 | 2 | 5 | 5 | 5 | **Selected external adapter** |
| LibreOfficeKit | 5 | 4 | 4 | 2 | 4 | 5 | Future native embedding spike |
| Collabora Office Desktop | 5 | 4 | 2 | 4 | 3 | 5 | Promising, but 26.04 desktop release was still described by Collabora as not yet enterprise-supported |
| Collabora Online | 4 | 4 | 4 | 2 | 3 | 1 | Server/runtime complexity conflicts with simple standalone Windows deployment |
| ONLYOFFICE Desktop Editors | 5 | 5 documented | 2 | 5 | 2 for proprietary embedded branding; 4 as separate unmodified tool subject to AGPL obligations | 5 | External fallback only, not bundled |
| ONLYOFFICE Docs Community | 4 | 5 documented | 5 | 2 | 1 for closed-source derivative/integration without AGPL compliance | 1 | Not selected |
| ONLYOFFICE Docs Developer | 4 | 5 documented | 5 | 2 | 4 with commercial agreement | 1 | Credible future licensed embedded option |
| Univer core | 5 | 3 | 5 | 4 | 5 Apache-2.0 core | 4 | Strong embeddability, but advanced/pro import/export and institutional parity require additional licensing/evidence |
| ClosedXML | 5 | XLSX manipulation only | 5 library | 5 | 5 MIT | 5 | Not an interactive editor/formula UI |
| Open XML SDK | 5 | File manipulation only | 5 library | 5 | 5 MIT | 5 | Not an interactive productivity editor |

## Decision drivers

### Windows support

LibreOffice publishes Windows 10/11 support. ONLYOFFICE Desktop Editors also publishes Windows 11 installers. Both are mature local desktop suites.

### Offline behavior

The selected LibreOffice interaction is local-file/out-of-process. The KHZ adapter does not require cloud identity or server infrastructure.

### Compatibility spike

The synthetic corpus demonstrated:

- DOCX: open/edit/save/reopen succeeded; comment and tracked insertion survived; the synthetic TOC field did not survive in the same inspectable form, so DOCX is **PARTIAL**.
- XLSX: open/edit/save/reopen succeeded; 4,807 formulas, pivot definitions/cache, chart, table, validation, conditional formatting, named range, comment, protection, and six worksheets survived the tested round-trip.
- Formula matrix: 59/64 representative formulas calculated in the tested engine when serialized using Excel-compatible `_xlfn` forms where required. `SORTBY`, `TAKE`, `DROP`, `HSTACK`, and `VSTACK` returned unsupported-name results in this engine version.
- PPTX: open/edit/save/reopen succeeded; chart, slide master, six transitions, and six speaker-note parts survived. Object animations were not represented in the fixture and remain unverified.

Evidence is in `acceptance/reports/`.

### Licensing

LibreOffice is made available under MPLv2 with additional component licenses. KHZ does not link to or redistribute LibreOffice in this ZIP; it detects an administrator-installed executable and launches it.

ONLYOFFICE Desktop Editors and Community Docs are AGPLv3. ONLYOFFICE's licensing materials state source integration into another application under the open license brings AGPL obligations; Developer editions exist for commercial integration. KHZ therefore does not silently bundle or white-label ONLYOFFICE.

Univer's core repository is Apache-2.0, while Univer Pro documentation describes production licensing for advanced capabilities. That split must be evaluated feature-by-feature before choosing it as the institutional Office engine.

### Embedding

The current integration is intentionally honest: the Office editor window is external. KHZ owns workspace, policy, versions, provenance, backup, search, Git, terminal, and deterministic automation. It does not pretend a Tk grid or text box is Microsoft Office compatibility.

A future embedded engine decision should spike:

1. LibreOfficeKit native embedding on Windows;
2. licensed ONLYOFFICE Developer local deployment if a non-server deployment is supportable;
3. Univer for web-embedded workflows where exact Office round-trip can be proven.

## References checked 2026-08-31

- LibreOffice licenses: https://www.libreoffice.org/licenses/
- LibreOffice system requirements: https://www.libreoffice.org/get-help/system-requirements/
- ONLYOFFICE license FAQ: https://www.onlyoffice.com/license-faq
- ONLYOFFICE Desktop Editors repository: https://github.com/ONLYOFFICE/DesktopEditors
- ONLYOFFICE edition comparison: https://www.onlyoffice.com/compare-editions
- Collabora Office Desktop: https://www.collaboraonline.com/collabora-office/
- Collabora Office 26.04 release note: https://www.collaboraonline.com/blog/collabora-office-26-04-release/
- Univer repository: https://github.com/dream-num/univer
- Univer Pro licensing: https://docs.univer.ai/guides/pro/license
- ClosedXML: https://github.com/ClosedXML/ClosedXML
- Open XML SDK: https://github.com/dotnet/Open-XML-SDK

## Full decision-gate scorecard

Legend: 5 = strong fit, 1 = weak fit, `N/E` = not an editor / not applicable. These are architecture-fit scores, not vendor quality rankings. Fidelity for candidates other than LibreOffice was not locally spike-tested in this package.

### Compatibility and productivity

| Candidate | Windows 11 | DOCX fidelity | XLSX fidelity | PPTX fidelity | Institutional sheet | Formula compatibility | Pivot tables | Charts | Track changes | Presentation animations | PDF capability | English UI | RTL secondary |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| ONLYOFFICE Desktop Editors | 5 | 5 | 5 | 5 | 5 | 5 | 4 | 5 | 5 | 4 | 5 | 5 | 4 |
| ONLYOFFICE Docs Community | 4 | 5 | 5 | 5 | 5 | 5 | 4 | 5 | 5 | 4 | 5 | 5 | 4 |
| ONLYOFFICE Docs Developer | 4 | 5 | 5 | 5 | 5 | 5 | 4 | 5 | 5 | 4 | 5 | 5 | 4 |
| Collabora Office Desktop | 5 | 4 | 4 | 4 | 5 | 5 | 5 | 5 | 5 | 5 | 4 | 5 | 4 |
| Collabora Online | 3 | 4 | 4 | 4 | 5 | 5 | 5 | 5 | 5 | 5 | 4 | 5 | 4 |
| LibreOffice Desktop | 5 | 4 | **5 tested** | 4 | 5 | **4 tested** | **5 tested** | **5 tested** | **4 tested** | 4 | 5 | 5 | 4 |
| LibreOfficeKit | 4 | 4 | 5 | 4 | 5 | 5 | 5 | 5 | 5 | 4 | 5 | depends on host | depends on host |
| Univer | 5 | 3 | 4 | 3 | 4 | 4 | 3 | 4 | 3 | 2 | 3 | 5 | 4 |
| ClosedXML | 5 | N/E | 4 file API | N/E | N/E | 2 calculation/editor | 2 manipulation | 4 manipulation | N/E | N/E | N/E | N/E | N/E |
| Open XML SDK | 5 | 4 file API | 4 file API | 4 file API | N/E | N/E | 3 low-level | 4 low-level | 4 low-level | 4 low-level | N/E | N/E | N/E |

### Deployment and isolation

| Candidate | Fully local/offline | Embedding feasibility | Desktop deployment complexity | Docker/WSL/server required | Process isolation fit | Network behavior control | Update model | Security support posture | Source availability |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| ONLYOFFICE Desktop Editors | 5 | 2 | 4 | 5 (not required) | 5 external process | 3 | 4 | 4 | 5 |
| ONLYOFFICE Docs Community | 4 | 5 | 1 | 1 | 4 server process | 3 | 3 | 4 | 5 |
| ONLYOFFICE Docs Developer | 4 | 5 | 1 | 1 | 4 server process | 4 | 4 | 5 | 4/5 by edition |
| Collabora Office Desktop | 5 | 2 | 4 | 5 | 5 external process | 3 | 3 | 3 while desktop line is new | 5 |
| Collabora Online | 4 | 5 | 1 | 1 | 4 server process | 4 | 3 | 5 with supported offering | 5 source form |
| LibreOffice Desktop | 5 | 2 | 5 | 5 | 5 external process | 3 | 4 admin-controllable | 4 | 5 |
| LibreOfficeKit | 5 | 4 | 2 | 5 | 2 in-process unless separately hosted | 4 | 3 | 4 | 5 |
| Univer | 5 client-side core | 5 | 3 | 4 core / lower for Pro server features | 2 same process unless isolated | 4 host controlled | 4 npm/package | 4 | 5 core |
| ClosedXML | 5 | 5 | 5 | 5 | 2 in-process | 5 host controlled | 5 package | 4 | 5 |
| Open XML SDK | 5 | 5 | 5 | 5 | 2 in-process | 5 host controlled | 5 package | 5 | 5 |

### Licensing, redistribution, branding, maintenance

| Candidate | Primary license model | Redistribution rights fit | Branding rights fit | Commercial license required for proprietary embedded/white-label use | Long-term maintenance cost |
|---|---|---:|---:|---:|---:|
| ONLYOFFICE Desktop Editors | AGPLv3 | 2 for closed proprietary bundling; obligations apply | 2 trademark restrictions | 4/5 likely for proprietary embedding; review Developer license | 3 |
| ONLYOFFICE Docs Community | AGPLv3 | 1 for closed derivative/integration without AGPL compliance | 2 | 5 for proprietary Developer-style integration | 2 |
| ONLYOFFICE Docs Developer | commercial | 4 subject to agreement | 4 subject to agreement | 5 - this is the commercial integration route | 3 |
| Collabora Office Desktop | MPLv2/open-source components plus product/distribution terms | 3 | 2 trademark/product terms require review | 3 depending distribution/support | 3 |
| Collabora Online | MPLv2 source form plus executable/subscription terms | 3 | 2 | 3/4 for supported commercial deployment | 3 |
| LibreOffice Desktop | MPLv2 plus component licenses | **5 for unmodified external install** | 4 when keeping LibreOffice branding separate | 1 - no commercial license identified for current external-use design | **4** |
| LibreOfficeKit | MPLv2 plus component licenses | 4 | 4 | 1/2, legal review still required | 2 due integration engineering |
| Univer | Apache-2.0 core; Pro licensed | 5 core | 5 host branding | 3 for advanced Pro functionality | 3 |
| ClosedXML | MIT | 5 | 5 | 1 | 4 |
| Open XML SDK | MIT | 5 | 5 | 1 | 5 |

## Why ClosedXML/Open XML SDK are not selected as editors

Both are strong deterministic file-manipulation technologies. Open XML SDK's own project description explicitly says it is not intended to provide higher-level productivity tools. ClosedXML is an XLSX/XLSM manipulation library. KHZ may use this class of library later for inspection, auditing, mail merge, or targeted transformations, but not to fake Word/Excel/PowerPoint editing.
