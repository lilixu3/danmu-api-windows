# Stops every instance of the application that was started from the exact installer target path.
# Called by DanmuApi.iss before the file copy. Matching by full image path keeps unrelated copies
# (for example a portable folder somewhere else) untouched; /T /F also takes down the managed
# Node core and FRP children that the host supervises.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Target)

$ErrorActionPreference = 'Stop'
$full = [IO.Path]::GetFullPath($Target)

function Get-TargetRows {
    @(Get-CimInstance Win32_Process -Filter "Name='DanmuApi.App.exe'" |
        Where-Object { $_.ExecutablePath -and ([IO.Path]::GetFullPath($_.ExecutablePath) -ieq $full) })
}

$rows = Get-TargetRows
foreach ($row in $rows) {
    # A process that exits between enumeration and the kill is already gone; the re-check below decides.
    $null = & "$env:SystemRoot\System32\taskkill.exe" /PID $row.ProcessId /T /F 2>&1
}
if ($rows.Count -gt 0) { Start-Sleep -Milliseconds 300 }

$remaining = Get-TargetRows
Write-Output ('target=' + $full + ' matched=' + $rows.Count + ' remaining=' + $remaining.Count)
if ($remaining.Count -ne 0) { exit 1 }
exit 0
