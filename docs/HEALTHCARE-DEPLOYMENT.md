# Healthcare Hardened Deployment

Healthcare Hardened is a KHZ security profile, **not a certification** and not a statement of NCA, PDPL, HIPAA, or healthcare regulatory compliance.

## Implemented application policy

When enabled, KHZ forces:

- AI OFF
- remote AI OFF
- embeddings OFF
- telemetry OFF
- automatic updates OFF
- Git network OFF
- plugin support OFF
- macro policy OFF
- in-process network mode `LOOPBACK_ONLY`
- Terminal disabled in the UI
- configurable idle timeout that requests the native Windows session lock (`LockWorkStation`)

Audit remains enabled. The Windows lock path is **IMPLEMENTED BUT NOT RUNTIME VERIFIED** in the Linux build environment; no KHZ-local password fallback is used.

## Recommended Windows configuration

These controls are external to the current source package and must be deployed/administered by the organization:

- Windows 11 Enterprise/approved managed edition.
- BitLocker on data and backup volumes.
- NTFS ACLs based on Windows identity and least privilege.
- Windows Defender/EDR as organizational policy requires.
- Windows Firewall rules that enforce expected KHZ/Office/AI egress rather than trusting only in-process checks.
- Disable or centrally manage Office macros, remote templates, external links, extension/plugin marketplaces, crash reporting, and update services as supported by the selected Office engine.
- Application allowlisting where appropriate.
- Domain/MDM session lock policy and screen-lock timeout; use these as the authoritative deployment controls even though KHZ can also request `LockWorkStation`.
- Offline administrator-controlled package installation and hash/signature verification.

## Organizational policy requirements

An organization must define, among other items:

- data classification rules;
- which workspaces may contain health data;
- who may enable local AI;
- whether PHI may ever be released to an approved model;
- macro and external-link policy;
- backup locations and retention;
- audit retention/review;
- incident response;
- software update approval;
- endpoint hardening.

## External compliance requirements

Engineering controls do not by themselves establish compliance. A deployment requires independent legal, privacy, security, regulatory, governance, and operational review applicable to the organization and jurisdiction.

## Zero-egress evidence

`acceptance/reports/healthcare-zero-egress.json` records a Linux process-observation test with Healthcare target settings, AI OFF, Git network OFF, updates OFF, the NO_AI scenario, and LibreOffice local Office round-trip. No non-loopback process connections were observed.

This does **not** establish Windows 11 zero-egress. Windows must be tested with the deployed Office version and OS firewall policy.
