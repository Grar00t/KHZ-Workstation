param(
    [switch]$SkipDependencyRestore
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $SkipDependencyRestore) {
    python -m pip install --disable-pip-version-check -r requirements-automation.txt
    python -m pip install --disable-pip-version-check --no-build-isolation -e .
}

$env:PYTHONPATH = Join-Path $root 'src'
python -m compileall -q src tests scripts
python -W error::ResourceWarning -m unittest discover -s tests -v
python scripts\no_ai_baseline.py
Write-Host 'KHZ host source build, regression tests, and NO_AI_BASELINE completed.'
