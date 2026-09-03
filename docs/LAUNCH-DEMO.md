# KHZ Launch Demo

This is the short public demo path for KHZ Workstation.

Goal: show the product idea in under 30 seconds without claiming unverified capabilities.

## Story

KHZ is not another document editor and not an AI wrapper.

The demo should show that a real local workspace remains the center of the product while the Office editor is a replaceable component inside that workflow.

## 30-second capture

1. Start on the KHZ Workspace surface.
2. Open a real workspace folder.
3. Show ordinary files such as:
   - `Budget.xlsx`
   - `Contract.docx`
   - `Evidence.pdf`
4. Open `Budget.xlsx` through the local Office integration.
5. Make one harmless visible edit and save.
6. Return to KHZ.
7. Show Files and one KHZ-owned surface such as Structured Data or Activity.
8. Open Backup & Restore and show that the workspace can be backed up.
9. End on the KHZ Workspace surface.

## Required on-screen wording

Use only claims supported by the repository:

```text
KHZ Workstation
Your work, on your computer.

Local-first workspace
Real files
Replaceable Office engine
AI optional
Open source
```

## ONLYOFFICE wording

If the ONLYOFFICE spike is the editor shown in the recording, use:

```text
ONLYOFFICE is used as the document-editing layer in this prototype.
```

Do not say:

- "KHZ built its own Word/Excel/PowerPoint"
- "production-ready ONLYOFFICE integration"
- "zero egress" unless a fresh runtime proof exists for the exact build
- "secure sandbox" for the terminal
- "100% Microsoft Office compatibility"

## Suggested social caption

```text
Building KHZ Workstation in public.

Instead of rebuilding Word, Excel and PowerPoint, KHZ keeps the workspace, files, data, tasks, audit and backup local — and treats the Office editor as a replaceable layer.

No AI required. Real files stay real files.

Open source:
https://github.com/Grar00t/KHZ-Workstation
```

If the demo visibly uses ONLYOFFICE, add:

```text
This prototype uses ONLYOFFICE as the document-editing layer.
```

## Message for ONLYOFFICE

```text
Hi — we published an open-source prototype of KHZ Workstation using ONLYOFFICE as the document-editing layer inside a broader local-first workspace.

The interesting part for us is not another Office wrapper: KHZ owns the workspace, real files, structured data, tasks, audit and backup while the editor remains a replaceable component.

We would value an engineering look at the integration direction. If your developer/community team finds it useful, we would also be happy for you to share it.

Repo: https://github.com/Grar00t/KHZ-Workstation
```

## Launch checklist

Before posting publicly:

- Windows Release build passes for the exact launch head.
- README matches the actual Office architecture.
- The demo records a real running build, not a mockup.
- The ONLYOFFICE path is described as a spike/prototype even though JWT and the KHZ session boundary are enabled; runtime interoperability still needs evidence.
- No secrets, credentials, private paths or personal workspace content are visible.
- Repository link opens on a coherent default branch.
- Social preview image is set manually in GitHub repository settings if desired.
- Repository description and topics are set manually if the connected GitHub API does not expose those settings.
