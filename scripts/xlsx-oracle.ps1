# KHZ-Workstation G1 OOXML oracle (zero-dependency, PowerShell 7+)
# Author: Suliman Nazal Alshammari · سليمان نزال الشمري · Grar00t
# SPDX-License-Identifier: LicenseRef-KHZ-Proprietary
# Oracle: the workbook caches every formula result in <v>. The file is ground truth.
#
# Usage:
#   pwsh scripts/xlsx-oracle.ps1 -Extract book.xlsx -OutCsv oracle.csv
#   pwsh scripts/xlsx-oracle.ps1 -Before before.xlsx -After after.xlsx
#
# PASS extract : ORACLE_PAIRS > 0 (caller asserts over the corpus aggregate).
# PASS round   : PRESERVE_UNKNOWN_XML=PASS (every ZIP part SHA-256 identical).
# Declarations are emitted as named counters; never silently passed.

[CmdletBinding()]
param(
    [Parameter(ParameterSetName = 'Extract')]
    [string]$Extract,
    [Parameter(ParameterSetName = 'Extract')]
    [string]$OutCsv,
    [Parameter(ParameterSetName = 'BeforeAfter', Mandatory = $true)]
    [string]$Before,
    [Parameter(ParameterSetName = 'BeforeAfter', Mandatory = $true)]
    [string]$After
)

$ErrorActionPreference = 'Stop'
$NS = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
$ErrorValues = @('#NAME?', '#CYCLE!', '#REF!', '#DIV/0!', '#VALUE!', '#N/A', '#NULL!', '#NUM!')
$WholeColRe  = [regex] '(?<![A-Za-z0-9])([A-Z]+):(\1)(?![A-Za-z0-9])'

function Get-SharedStrings([System.IO.Compression.ZipArchive]$zip) {
    $entry = $zip.GetEntry('xl/sharedStrings.xml')
    if (-not $entry) { return @{} }
    $sr = New-Object System.IO.StreamReader($entry.Open())
    try { $xml = [xml]$sr.ReadToEnd() } finally { $sr.Close() }
    $nsm = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
    $nsm.AddNamespace('m', $NS)
    $out = @{}
    $i = 0
    foreach ($si in $xml.SelectNodes('//m:si', $nsm)) {
        $t = ($si.SelectNodes('.//m:t', $nsm) | ForEach-Object { $_.InnerText } | Out-String)
        $out[$i] = ($t -join '')
        $i++
    }
    return $out
}

function Get-CellText($cell, $strings) {
    $t = $cell.GetAttribute('t')
    if ($t -eq 's') {
        $v = $cell['v']
        if ($v -and $v.InnerText) { return $strings[[int]$v.InnerText] }
        return ''
    }
    if ($t -eq 'inlineStr') {
        $is = $cell['is']
        if (-not $is) { return '' }
        return (($is.SelectNodes('.//*[local-name()="t"]') | ForEach-Object { $_.InnerText }) -join '')
    }
    return ''
}

function Get-FormulaPairs([string]$path) {
    $pairs = New-Object System.Collections.ArrayList
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $fs = [System.IO.File]::OpenRead($path)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Read)
        $strings = Get-SharedStrings $zip
        $sheetNames = $zip.Entries | Where-Object { $_.FullName -match '^xl/worksheets/sheet\d+\.xml$' } | Sort-Object FullName
        foreach ($entry in $sheetNames) {
            $sr = New-Object System.IO.StreamReader($entry.Open())
            try { $xml = [xml]$sr.ReadToEnd() } finally { $sr.Close() }
            $rows = $xml.SelectNodes("//*[local-name()='row']")
            foreach ($row in $rows) {
                foreach ($cell in $row.SelectNodes("*[local-name()='c']")) {
                    $fEl = $cell.SelectSingleNode("*[local-name()='f']")
                    if (-not $fEl) { continue }
                    $vEl = $cell.SelectSingleNode("*[local-name()='v']")
                    $ref = $cell.GetAttribute('r')
                    $formula = if ($fEl.InnerText) { $fEl.InnerText } else { '' }
                    $shared = if ($fEl.GetAttribute('t') -eq 'shared') { '1' } else { '0' }
                    $whole = if ($WholeColRe.IsMatch($formula)) { '1' } else { '0' }
                    $cached = if ($vEl -and $vEl.InnerText) { $vEl.InnerText } else { '' }
                    $label = Get-CellText $cell $strings
                    [void]$pairs.Add([pscustomobject]@{
                        sheet_part = $entry.FullName; ref = $ref; label = $label
                        formula = $formula; cached = $cached; shared = $shared; whole_column = $whole
                    })
                }
            }
        }
    } finally { $fs.Close() }
    return $pairs
}

function Invoke-Extract([string]$path, [string]$csv) {
    $pairs = Get-FormulaPairs $path
    $oracle = @($pairs | Where-Object { $_.cached -ne '' -and ($ErrorValues -notcontains $_.cached.Trim()) })
    $shared  = @($pairs | Where-Object { $_.shared -eq '1' }).Count
    $whole   = @($pairs | Where-Object { $_.whole_column -eq '1' }).Count
    $emptyV  = @($pairs | Where-Object { $_.cached -eq '' }).Count
    $errV    = @($pairs | Where-Object { ($ErrorValues -contains $_.cached.Trim()) }).Count
    if ($csv) {
        $oracle | Select-Object sheet_part, ref, label, formula, cached, shared, whole_column |
            Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
    }
    Write-Output "ORACLE_PAIRS=$($oracle.Count)"
    Write-Output "SHARED_UNRESOLVED=$shared"
    Write-Output "WHOLE_COLUMN_UNRESOLVED=$whole"
    Write-Output "EMPTY_CACHED_V=$emptyV"
    Write-Output "ERROR_VALUES_CACHED=$errV"
    Write-Output "FORMULAS_TOTAL=$($pairs.Count)"
    if ($csv) { Write-Output "OUT_CSV=$csv" }
    if ($oracle.Count -gt 0) { exit 1 } else { exit 2 }
}

function Get-PartHashes([string]$path) {
    $h = @{}
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $fs = [System.IO.File]::OpenRead($path)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Read)
        foreach ($entry in $zip.Entries) {
            $sr = New-Object System.IO.StreamReader($entry.Open())
            try { $bytes = [System.IO.MemoryStream]::new()
                  $entry.Open().CopyTo($bytes)
                  $h[$entry.FullName] = ([System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes.ToArray()) | ForEach-Object { $_.ToString('x2') }) -join ''
            } finally { $sr.Close() }
        }
    } finally { $fs.Close() }
    return $h
}

function Invoke-BeforeAfter([string]$before, [string]$after) {
    $bh = Get-PartHashes $before
    $ah = Get-PartHashes $after
    $beforeOnly = @($bh.Keys | Where-Object { $ah.Keys -notcontains $_ })
    $afterOnly  = @($ah.Keys | Where-Object { $bh.Keys -notcontains $_ })
    $common = @($bh.Keys | Where-Object { $ah.Keys -contains $_ })
    $mismatched = @($common | Where-Object { $bh[$_] -ne $ah[$_] })
    $preserve = if ($beforeOnly.Count -eq 0 -and $afterOnly.Count -eq 0 -and $mismatched.Count -eq 0) { 'PASS' } else { 'FAIL' }
    Write-Output "BEFORE_PARTS=$($bh.Count)"
    Write-Output "AFTER_PARTS=$($ah.Count)"
    Write-Output "BEFORE_ONLY=$($beforeOnly.Count)"
    Write-Output "AFTER_ONLY=$($afterOnly.Count)"
    Write-Output "MISMATCHED=$($mismatched.Count)"
    Write-Output "PRESERVE_UNKNOWN_XML=$preserve"
    if ($mismatched.Count -gt 0) { $mismatched | Select-Object -First 10 | ForEach-Object { Write-Output "MISMATCH $_" } }
    if ($preserve -eq 'PASS') { exit 0 } else { exit 1 }
}

if ($PSCmdlet.ParameterSetName -eq 'Extract') {
    if (-not $Extract) { Write-Error 'Extract path required'; exit 2 }
    Invoke-Extract $Extract $OutCsv
} elseif ($PSCmdlet.ParameterSetName -eq 'BeforeAfter') {
    Invoke-BeforeAfter $Before $After
} else {
    Write-Error 'No mode. Use -Extract or -Before/-After.'
    exit 2
}
