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
| Prompt injection in documents | model gets only bounded workspace MCP tools; writes become pending proposals requiring explicit review | model output may still socially persuade a user to approve a harmful change |
| Malicious spreadsheet formula | no automatic macro/script execution by KHZ; external links are not a KHZ background task | formula/engine-specific external content behavior requires engine hardening |
| Macro execution | Healthcare policy marks macros disabled | adapter does not yet enforce every LibreOffice macro setting at process level |
| AI hallucination/confabulation | model output is not execution evidence; typed actions and approval | user may still trust bad prose |
| Excessive AI agency | no shell/network/self-approval; filesystem tools are workspace-bound read/search plus proposal creation | llama.cpp/MCP are external and experimental; AppContainer isolation is not implemented |
| Terminal command injection | terminal requires explicit user authorization; model MCP has no shell action; WPF process tree uses a Job Object | user can intentionally run dangerous commands; Job Objects are not filesystem sandboxes |
| Unauthenticated loopback caller | random Office/AI bearer tokens, enabled Document Server JWT, exact container-source/capability checks | another process under the same user can read user-owned runtime state unless stronger OS isolation is deployed |
| Plugin compromise | plugins disabled by default; no plugin marketplace implemented | future plugin model requires capability isolation |
| Dependency/model compromise | pinned dev dependencies; SBOM; Office image digest; model source/license manifest and SHA-256 verification | first download and upstream llama.cpp/model supply chains still require organizational intake controls |
| Secret leakage | no bundled credentials; context gate | future credentials need DPAPI/Credential Manager |
| Unexpected network egress | network policy; Git network explicit; Linux process observation test | external processes can bypass in-process broker without OS rules |
| Workspace boundary escape | canonical resolution plus symlink/reparse denial tests | filesystem race/TOCTOU remains a residual class |
| Database corruption | SQLite transactions, backup/restore, rollback tests | no full low-level corruption repair tool |
| Audit tampering | hash chain detects obvious modification | attacker with filesystem write access can replace whole history |
| Backup theft | no automatic upload; hashes ensure integrity | backup confidentiality depends on protected storage/volume |
| Malicious Office document | pre-edit snapshots and external mature engine | mature engine still has parser attack surface |

## Clinical scope

No autonomous diagnosis, treatment recommendation, medication decision, or triage feature is implemented. AI is productivity assistance only and is disabled by default.
