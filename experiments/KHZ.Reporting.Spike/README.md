# KHZ Reporting Spike

Purpose: test one narrow question before changing KHZ production UI: can a local WPF app render and export a branded asset-register template without Microsoft Office, LibreOffice, ONLYOFFICE, or a network service at runtime?

## Scope

- WPF host on `net9.0-windows`
- local SQLite database under `%LocalAppData%\KHZ\ReportingSpike`
- RDL template stored as a normal file in the project
- editable report title and company name
- preview through `Majorsilence.Reporting.LibRdlWpfViewer`
- PDF export
- no Office application automation
- no cloud API

The NuGet restore step requires package access during development/build. After dependencies are present, the spike itself is local-only.

## Run

```powershell
dotnet run --project experiments\KHZ.Reporting.Spike\KHZ.Reporting.Spike.csproj
```

## Acceptance gate

Keep this dependency only if all of these are proven on Windows:

1. solution builds in CI;
2. preview opens the bundled `AssetRegister.rdl`;
3. SQLite rows render correctly;
4. changing title/company refreshes the preview;
5. PDF export succeeds;
6. no Office process and no external network service is required at runtime.

This is deliberately an experiment. It is not wired into `KHZ.App` navigation or production data paths.
