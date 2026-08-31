# Office Licensing Boundary

This is technical documentation for legal review, not legal advice.

## Selected integration

KHZ Workstation detects and launches an **administrator/user-installed, unmodified LibreOffice executable**. LibreOffice binaries are not included in this ZIP.

LibreOffice states that the project is made available under MPLv2 and contains additional components under other open-source licenses. The deployed LibreOffice installation must retain its own notices/licenses.

KHZ source itself is Apache-2.0.

## Integration forms are not equivalent

The deployment review must distinguish:

| Integration form | Current KHZ behavior |
|---|---|
| Use unmodified external executable | **YES** - LibreOffice |
| Link to LibreOffice libraries | NO |
| Embed LibreOfficeKit | NO |
| Fork/modify LibreOffice source | NO |
| Redistribute LibreOffice binaries inside KHZ ZIP | NO |
| Network-integrate a document server | NO |
| White-label third-party Office UI | NO |

## ONLYOFFICE

ONLYOFFICE Desktop Editors and Community Docs are published under AGPLv3. ONLYOFFICE also offers commercial Enterprise/Developer licenses. Their license FAQ states that using their source in another application under AGPL entails AGPL licensing obligations for the application; the commercial Developer route is the appropriate item to evaluate for proprietary embedded/white-label distribution.

KHZ's `OnlyOfficeDesktopEngine` only detects an already-installed desktop executable as a fallback. It does not bundle or modify ONLYOFFICE.

## Collabora

Collabora publishes source under MPLv2/other component licenses, while executable/distribution and subscription terms can differ. The exact chosen Collabora product and distribution channel must be reviewed before bundling or branded redistribution.

## Univer

Univer core is Apache-2.0. Univer Pro documentation describes a production license for advanced capabilities. A legal and technical feature map is required before relying on Pro functionality for import/export or institutional spreadsheet features.

## ClosedXML and Open XML SDK

Both are MIT-licensed libraries. They are useful for deterministic file manipulation/inspection but are not Office-class interactive editors and therefore do not satisfy the Office engine requirement by themselves.

## Commercial license required for intended deployment

For the **current selected LibreOffice external-process architecture**, no commercial LibreOffice license requirement was identified from the reviewed project licensing page. This is not a legal conclusion.

For a future **proprietary embedded/white-labeled ONLYOFFICE Developer** integration, a commercial license should be expected and reviewed.
