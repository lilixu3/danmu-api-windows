param(
  [Parameter(Mandatory=$true)][string]$RuntimeBundle,
  [Parameter(Mandatory=$true)][string]$NodeVersion,
  [Parameter(Mandatory=$true)][ValidateSet('x64','x86','arm64')][string]$Arch
)
# Stamps an official Node.js runtime (node.exe + NODE-LICENSE.txt) into an existing runtime
# bundle and rebuilds SHA256SUMS.txt + runtime-build.json. Replaces the old practice of
# extracting node.exe out of an archived legacy package.
#
# Verification chain (all hashes come from the official nodejs.org manifest, no trust in transit):
#   1. SHA256 of node-v<version>-win-<arch>.zip  == SHASUMS256.txt entry
#   2. SHA256 of the extracted node.exe          == SHASUMS256.txt 'win-<arch>/node.exe' entry
#   3. PE machine of the extracted node.exe      == <arch>
#   4. <node.exe> --version                      == v<version>
#
# ASCII-only on purpose: PowerShell 5.1 decodes BOM-less scripts with the system ANSI codepage.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$peMachine = @{ x86 = 0x014c; x64 = 0x8664; arm64 = 0xAA64 }[$Arch]
$base = 'https://nodejs.org/dist/v' + $NodeVersion
$zipName = 'node-v' + $NodeVersion + '-win-' + $Arch + '.zip'
$bundle = [IO.Path]::GetFullPath($RuntimeBundle)
if (-not (Test-Path (Join-Path $bundle 'SHA256SUMS.txt'))) { throw ('Not a runtime bundle: ' + $bundle) }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$stage = Join-Path ([IO.Path]::GetTempPath()) ('danmu-node-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
  $sumsPath = Join-Path $stage 'SHASUMS256.txt'
  curl.exe -sSL -o $sumsPath ($base + '/SHASUMS256.txt')
  $sums = Get-Content -LiteralPath $sumsPath
  # Official manifest format: "<64 hex>  <filename>"
  function OfficialHash([string]$name) {
    $hit = @($sums | Where-Object { $_ -match ('^([0-9a-fA-F]{64})  ' + [regex]::Escape($name) + '$') })
    if ($hit.Count -ne 1) { throw ('Official SHASUMS256.txt has no unique entry for ' + $name) }
    return ([regex]::Match($hit[0], '^([0-9a-fA-F]{64})').Groups[1].Value.ToUpperInvariant())
  }
  $expectZip = OfficialHash $zipName
  $expectNode = OfficialHash ('win-' + $Arch + '/node.exe')

  $zipPath = Join-Path $stage $zipName
  curl.exe -sSL -o $zipPath ($base + '/' + $zipName)
  $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
  if ($zipHash -ne $expectZip) { throw ('SHA256 mismatch for ' + $zipName + ': expected ' + $expectZip + ' actual ' + $zipHash) }

  $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
  try {
    $prefix = 'node-v' + $NodeVersion + '-win-' + $Arch + '/'
    $want = @{ ($prefix + 'node.exe') = 'node.exe'; ($prefix + 'LICENSE') = 'NODE-LICENSE.txt' }
    foreach ($name in $want.Keys) {
      if (-not ($archive.Entries | Where-Object { $_.FullName -eq $name })) { throw ('Missing ' + $name + ' in ' + $zipName) }
    }
    foreach ($name in $want.Keys) {
      $entry = $archive.Entries | Where-Object { $_.FullName -eq $name }
      [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $stage $want[$name]), $true)
    }
  }
  finally { $archive.Dispose() }

  $nodePath = Join-Path $stage 'node.exe'
  $licensePath = Join-Path $stage 'NODE-LICENSE.txt'
  $nodeHash = (Get-FileHash -LiteralPath $nodePath -Algorithm SHA256).Hash
  if ($nodeHash -ne $expectNode) { throw ('Extracted node.exe hash ' + $nodeHash + ' does not match manifest ' + $expectNode) }

  # A wrong-arch or wrong-version payload must fail here instead of at first launch on a user machine.
  $bytes = [IO.File]::ReadAllBytes($nodePath)
  $lfanew = [BitConverter]::ToInt32($bytes, 0x3C)
  if ([Text.Encoding]::ASCII.GetString($bytes, $lfanew, 4) -ne "PE`0`0") { throw 'Extracted node.exe has no PE signature' }
  $machine = [BitConverter]::ToUInt16($bytes, $lfanew + 4)
  if ($machine -ne $peMachine) { throw ('Extracted node.exe machine 0x' + $machine.ToString('X4') + ' is not ' + $Arch) }
  # A cross-architecture node.exe cannot execute on this machine, so the version probe only runs where
  # the payload can actually start. Verification for a foreign architecture rests on the two official
  # hashes above plus the PE machine below, and says so explicitly instead of skipping silently.
  $osArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
  $runnable = ($osArch -eq 'X64' -and $Arch -in @('x64', 'x86')) -or ($osArch -eq 'Arm64') -or ($osArch -eq 'X86' -and $Arch -eq 'x86')
  if ($runnable) {
    $version = (& $nodePath --version).Trim()
    if ($version -ne ('v' + $NodeVersion)) { throw ('Extracted node.exe reports ' + $version + ', expected v' + $NodeVersion) }
  }
  else {
    $version = 'v' + $NodeVersion
    Write-Output ('Cross-architecture stamp: ' + $Arch + ' runtime on ' + $osArch + ' host; --version is not executable here, verified by the official manifest hashes and the PE machine instead.')
  }

  Copy-Item -LiteralPath $nodePath -Destination (Join-Path $bundle 'node.exe') -Force
  Copy-Item -LiteralPath $licensePath -Destination (Join-Path $bundle 'NODE-LICENSE.txt') -Force

  $manifestPath = Join-Path $bundle 'SHA256SUMS.txt'
  $replaced = 0
  $lines = Get-Content -LiteralPath $manifestPath | ForEach-Object {
    if ($_ -notmatch '^([0-9a-fA-F]{64})  (.+)$') { throw ('Invalid runtime manifest line: ' + $_) }
    $path = $Matches[2]
    if ($path -eq 'node.exe' -or $path -eq 'NODE-LICENSE.txt') {
      $replaced++
      (Get-FileHash -LiteralPath (Join-Path $bundle $path) -Algorithm SHA256).Hash + '  ' + $path
    } else { $_ }
  }
  if ($replaced -ne 2) { throw ('Expected to replace node.exe and NODE-LICENSE.txt, replaced ' + $replaced) }
  Set-Content -LiteralPath $manifestPath -Value $lines -Encoding ASCII

  & (Join-Path $PSScriptRoot 'New-RuntimeBuildIdentity.ps1') -RuntimeBundle $bundle -NodeVersion $NodeVersion -Arch $Arch
  if ($LASTEXITCODE -ne 0) { throw 'Runtime build identity failed' }
  Write-Output ('Stamped Node ' + $version + ' (' + $Arch + ') into ' + $bundle)
}
finally {
  Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
