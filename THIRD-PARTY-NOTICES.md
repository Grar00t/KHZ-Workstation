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

## ONLYOFFICE Desktop Editors - optional external fallback detector

- Project: ONLYOFFICE Desktop Editors
- License published by project: GNU AGPL v3
- Source: https://github.com/ONLYOFFICE/DesktopEditors
- Bundled in KHZ ZIP: NO
- Linked/embedded: NO

Commercial Developer licensing is a separate option for proprietary embedded integration and is not included here.

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
