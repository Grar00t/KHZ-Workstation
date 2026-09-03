[CmdletBinding()]
param(
    [string]$SessionFile
)

$ErrorActionPreference = "Stop"

$scriptDirectory =
    Split-Path -Parent $MyInvocation.MyCommand.Path

$repositoryRoot =
    (Resolve-Path (Join-Path $scriptDirectory "..\..")).Path

if ([string]::IsNullOrWhiteSpace($SessionFile)) {
    $SessionFile =
        Join-Path $scriptDirectory ".runtime\session.env"
}

$resolvedSessionFile =
    (Resolve-Path $SessionFile -ErrorAction Stop).Path

$token = $null

foreach ($line in [IO.File]::ReadAllLines($resolvedSessionFile)) {
    if ($line -match '^KHZ_OFFICE_GATEWAY_TOKEN=([A-Za-z0-9_-]{32,})$') {
        $token = $Matches[1]
    }
}

if ([string]::IsNullOrWhiteSpace($token)) {
    throw "The Office session file is missing a valid gateway token. Run start-spike.sh first."
}

$env:KHZ_OFFICE_GATEWAY_TOKEN = $token

try {
    & dotnet run `
        --project (Join-Path $repositoryRoot "windows\KHZ.App\KHZ.App.csproj") `
        --configuration Release

    if ($LASTEXITCODE -ne 0) {
        throw "KHZ Workstation exited with code $LASTEXITCODE."
    }
}
finally {
    Remove-Item Env:KHZ_OFFICE_GATEWAY_TOKEN -ErrorAction SilentlyContinue
}
