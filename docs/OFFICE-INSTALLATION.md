# Offline Office Engine Installation

This is an administrator procedure, not a redistribution grant or legal conclusion.

## Boundary

KHZ Workstation does **not** bundle LibreOffice. The application detects an unmodified local installation and launches it as a separate process. KHZ does not download, update, replace, or remove the Office engine at startup.

Observed compatibility spike engine:

```text
LibreOffice 25.2.3.2 520(Build:2)
Platform actually tested: Linux
Windows 11 behavior for this exact package: UNVERIFIED
```

Do not treat `25.2.3.2` as a permanent security pin. Before institutional deployment, select a currently maintained LibreOffice build, approve it, and rerun the KHZ Office corpus on that exact build.

## Administrator intake procedure on Windows

1. Obtain the x86-64 Windows installer only from The Document Foundation / LibreOffice's approved official distribution or an organizational software repository.
2. Keep the installer outside KHZ source control.
3. Record the installer filename, version, source URL/repository, acquisition date, and SHA-256 in an administrator-controlled deployment manifest.
4. Verify the Authenticode signature before installation:

```powershell
$sig = Get-AuthenticodeSignature .\LibreOffice-Approved.msi
$sig | Format-List Status,StatusMessage,SignerCertificate
if ($sig.Status -ne 'Valid') { throw 'Office installer signature is not valid.' }
```

5. Compute the SHA-256 independently:

```powershell
Get-FileHash .\LibreOffice-Approved.msi -Algorithm SHA256
```

6. Compare that value with the organization-approved intake manifest. This repository deliberately does **not** invent a vendor hash for a Windows installer that was not retrieved and verified in this build environment.
7. Install from the approved offline package. Example administrative command:

```powershell
msiexec.exe /i .\LibreOffice-Approved.msi /qn /norestart
```

8. Launch KHZ and inspect the Office engine status. The adapter searches `PATH` plus conventional 64-bit/32-bit Program Files locations.
9. Run the synthetic Office acceptance corpus and retain the machine-readable reports.
10. In Healthcare Hardened deployments, manage Office updates centrally and block unapproved external destinations at OS/network controls. KHZ does not claim that its in-process broker controls third-party Office egress.

## Acceptance gate for a new Office version

A newly approved engine version remains **UNVERIFIED** until it passes, at minimum:

- DOCX open/edit/save/reopen;
- XLSX open/calculate/edit/save/reopen plus structural inspection;
- pivot/chart/validation/conditional-format checks;
- PPTX open/edit/save/reopen/present checks;
- PDF export where relied upon;
- zero-egress observation under the deployment policy;
- administrator review of licensing/notices and deployed version.

If a version fails the corpus, retain the previous approved package and record the failure. Do not silently upgrade.

## Removal / rollback

KHZ does not uninstall Office. Rollback is an administrator software-deployment action: uninstall the rejected engine with the organization's normal software-management tooling, reinstall the last approved package, then rerun the corpus. KHZ workspace originals remain ordinary files and are not migrated into a proprietary KHZ document format.
