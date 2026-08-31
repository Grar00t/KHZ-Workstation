# Network Policy

Implemented modes:

- `DENY`
- `LOOPBACK_ONLY`
- `ALLOWLIST`
- `UNRESTRICTED`

The policy object authorizes KHZ-owned destinations. `LOOPBACK_ONLY` accepts localhost/loopback addresses and rejects non-loopback hosts.

## Healthcare default

Healthcare Hardened sets `LOOPBACK_ONLY`, disables Git network, updates, remote AI, telemetry, and plugins.

## Important limitation

An in-process Python policy cannot prevent a third-party process such as LibreOffice from opening its own socket. That requires supported engine configuration plus Windows Firewall or equivalent process/OS enforcement. KHZ does not claim centralized egress enforcement over processes it cannot mediate.

## Acceptance probe

The Linux acceptance script monitors the process tree during NO_AI and LibreOffice round-trip activity with `psutil` and records unexpected non-loopback remote connections. The observed test passed with none. Windows verification remains required.
