# Third-Party Notices

This ZIP does not bundle LibreOffice or ONLYOFFICE binaries.

## LibreOffice - selected external Office engine

- Project: LibreOffice
- Use: detected administrator/user-installed external executable
- Tested version: 25.2.3.2 on Linux
- License: Mozilla Public License 2.0 plus component licenses
- Source/licensing: https://www.libreoffice.org/licenses/
- Bundled in KHZ ZIP: NO

A copy of MPL-2.0 is included in `LICENSES/MPL-2.0.txt` for reference. The installed LibreOffice distribution contains its own authoritative notices for bundled components.

## ONLYOFFICE - optional external integrations

- Project: ONLYOFFICE Desktop Editors
- License published by project: GNU AGPL v3
- Source: https://github.com/ONLYOFFICE/DesktopEditors
- Bundled in KHZ ZIP: NO
- Linked/embedded: NO

The repository also contains an experimental gateway/launcher for a pinned external ONLYOFFICE Document Server container image. The image is not bundled in this source package. It retains its upstream AGPLv3 licensing, notices, branding, and any additional applicable terms. See `docs/OFFICE-LICENSING.md` before any distribution.

Commercial Developer licensing is a separate option for proprietary embedded integration and is not included here.

## llama.cpp and optional model weights

- Project: llama.cpp
- Use: optional user-installed `llama-download` and `llama-server` runtime
- License published by project: MIT
- Source: https://github.com/ggml-org/llama.cpp
- Bundled in KHZ ZIP: NO

KHZ's model catalog can request external Llama, Qwen, or Phi GGUF weights. No weights are bundled. Each model retains its upstream license and distribution terms. KHZ records model source and license metadata in its local manifest; the user or deployment owner must review those terms before download or redistribution. Llama requires explicit `--accept-license` confirmation in the KHZ CLI.

## Optional deterministic automation Python packages

These pinned packages are referenced by `requirements-automation.txt` and the `automation` optional extra. They are used only when the corresponding deterministic cross-Office workflow is invoked; their source code is not vendored into this ZIP:

- openpyxl 3.1.5
- python-docx 1.2.0
- python-pptx 1.0.2

## Development/test Python packages

These packages are referenced by `requirements-dev.txt` for synthetic acceptance fixtures and observation tooling; their source code is not vendored into this ZIP:

- reportlab 4.4.9
- psutil 7.2.2
- Pillow 12.3.0

Reference copies of the installed dependency license files available in this build environment are under `LICENSES/`. openpyxl is accompanied by a metadata notice because its installed wheel did not expose a standalone license file. Review the authoritative license distributed with each approved package before redistributing a bundled Python runtime.

## Generated synthetic Office files

The DOCX/XLSX/PPTX/PDF files under `acceptance/corpus/` are generated test fixtures and contain no patient or institutional production data.
