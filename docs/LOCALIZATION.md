# Localization Architecture

Canonical locale: `en-US`.

`src/khz_workstation/i18n.py` defines locale metadata, canonical resource keys, fallback behavior, and RTL metadata. `ar-SA` is registered as an RTL secondary locale, but an Arabic resource catalog and full widget-direction integration are **UNVERIFIED / NOT IMPLEMENTED** in this build.

The localization boundary deliberately does not alter:

- code;
- terminal commands;
- filesystem paths;
- Git hashes;
- model identifiers;
- spreadsheet function names.

English remains the acceptance-test interface. Arabic parity is not treated as a v1 release blocker.
