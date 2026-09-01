# KHZ Workstation

**Your work, on your computer.**

KHZ Workstation is an open-source, local-first Windows workspace for real files, Office documents, structured data, tasks, repositories, terminal workflows, search, audit history, backup/restore, and optional local AI.

The workspace is the product. AI is optional and **OFF by default**.

## Current Windows surfaces

The .NET 9 WPF application currently includes:

- Workspace Composer / Files
- Documents / Sheets / Slides / PDF
- Structured Data
- Search
- Tasks
- Repositories
- Terminal
- Chat
- Activity
- Security
- Integrations
- Settings
- Backup & Restore

Files remain ordinary filesystem files. A KHZ workspace adds stable local identity and metadata under `.khz/`; it does not require importing the user's work into a cloud database.

## Local chat

KHZ can use a local GGUF model without making AI a dependency of the workstation.

The user configures:

- a local `llama-server.exe`;
- a local GGUF model;
- optional local LoRA adapter;
- optional chat-template file.

KHZ does **not** download or silently replace the model. The model runtime is launched as a child process on a dynamic `127.0.0.1` port with llama.cpp offline mode and stays resident between requests.

The UI owns the configured model label. A model claiming to be a different vendor/model is not runtime metadata. Hidden reasoning is not stored in chat history, and common visible reasoning tags are removed from the transcript compatibility path.

Bounded local tools provide directory listing, text read/search, read-only repository inspection, SHA-256-guarded exact text replacement, and PowerShell. File edits and PowerShell require explicit user confirmation; PowerShell cannot run merely because the model requested it.

See [`docs/AI-BOUNDARY.md`](docs/AI-BOUNDARY.md).

## Office engine status

KHZ keeps Office editing replaceable instead of rebuilding Word/Excel/PowerPoint from scratch.

The repository contains two separate evidence tracks:

1. a historical LibreOffice compatibility/round-trip acceptance baseline;
2. a WPF ONLYOFFICE embedded spike behind a local gateway.

The ONLYOFFICE spike is not a hardened production deployment claim. Review the Office ADRs and licensing notes before distribution.

## Verification

The GitHub workflow builds the Windows .NET 9 solution and runs the Python core/`NO_AI_BASELINE` matrix on Windows and Ubuntu. Runtime claims remain narrower than build claims: the user's actual model/GPU path must be exercised before local inference is called runtime-verified.

## Build the Windows application

Requirements:

- Windows 10/11 x64
- .NET 9 SDK
- Microsoft Edge WebView2 Runtime

```powershell
cd windows
dotnet restore .\KHZ.Workstation.sln
dotnet build .\KHZ.Workstation.sln -c Release
```

## Repository structure

```text
windows/KHZ.App/              primary WPF Windows application
src/khz_workstation/          deterministic Python services / baseline host
scripts/                      build and acceptance utilities
tests/                        core/security regression tests
tools/office-spike/           local ONLYOFFICE integration spike
acceptance/                   compatibility fixtures and evidence
docs/                         architecture and deployment boundaries
```

## Principles

1. Real files stay real files.
2. The workspace remains useful without AI.
3. Model output is data, not execution authority.
4. Sensitive mutations require explicit, observable execution paths.
5. Integrations remain replaceable.
6. Claims require evidence.

## License

KHZ Workstation source is licensed under Apache-2.0 unless a file states otherwise. Third-party components and Office engines retain their own licenses, notices, and trademarks.

**KHZ Workstation is independent software and is not affiliated with Microsoft, ONLYOFFICE, LibreOffice, Ai2/AllenAI, or other referenced vendors unless an explicit agreement says otherwise.**
