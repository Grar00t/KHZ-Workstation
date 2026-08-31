# Threat Model

## Assets

Workspace files, structured Data, audit metadata, backup archives, repository content, terminal output, future AI context, and local credentials.

## Trust boundaries

- User / Windows identity
- KHZ host process
- Workspace filesystem
- SQLite metadata store
- External Office engine
- Git executable
- Local shell
- Optional model provider
- OS/network controls

## Threats and mitigations

| Threat | Current mitigation | Residual risk |
|---|---|---|
| Malicious document | Office engine isolated as separate process; no document text becomes KHZ instruction automatically | Office-engine vulnerabilities remain possible |
| Prompt injection in documents | document content is data; AI context release is explicit/typed | future provider/tool integration must preserve this boundary |
| Malicious spreadsheet formula | no automatic macro/script execution by KHZ; external links are not a KHZ background task | formula/engine-specific external content behavior requires engine hardening |
| Macro execution | Healthcare policy marks macros disabled | adapter does not yet enforce every LibreOffice macro setting at process level |
| AI hallucination/confabulation | model output is not execution evidence; typed actions and approval | user may still trust bad prose |
| Excessive AI agency | no direct filesystem/shell/network; action allowlist | future executors must remain bounded |
| Terminal command injection | terminal requires explicit user authorization; model action schema has no shell action | user can intentionally run dangerous commands |
| Plugin compromise | plugins disabled by default; no plugin marketplace implemented | future plugin model requires capability isolation |
| Dependency compromise | pinned dev dependencies; SBOM; Office binary not downloaded at runtime | package-manager and Office supply chain still exist |
| Secret leakage | no bundled credentials; context gate | future credentials need DPAPI/Credential Manager |
| Unexpected network egress | network policy; Git network explicit; Linux process observation test | external processes can bypass in-process broker without OS rules |
| Workspace boundary escape | canonical resolution plus symlink/reparse denial tests | filesystem race/TOCTOU remains a residual class |
| Database corruption | SQLite transactions, backup/restore, rollback tests | no full low-level corruption repair tool |
| Audit tampering | hash chain detects obvious modification | attacker with filesystem write access can replace whole history |
| Backup theft | no automatic upload; hashes ensure integrity | backup confidentiality depends on protected storage/volume |
| Malicious Office document | pre-edit snapshots and external mature engine | mature engine still has parser attack surface |

## Clinical scope

No autonomous diagnosis, treatment recommendation, medication decision, or triage feature is implemented. AI is productivity assistance only and is disabled by default.
