param([Parameter(Mandatory=$true)][string]$RuntimeBundle)
$ErrorActionPreference='Stop'
$entries=@(Get-Content -LiteralPath (Join-Path $RuntimeBundle 'SHA256SUMS.txt') | ForEach-Object {
  if($_ -notmatch '^([0-9a-fA-F]{64})  (.+)$'){throw 'Invalid runtime manifest'}
  @{Path=$Matches[2];Hash=$Matches[1].ToUpperInvariant()}
})
$sorted=[string[]]@($entries | ForEach-Object {$_.Path})
[Array]::Sort($sorted,[StringComparer]::Ordinal)
$map=@{}; foreach($entry in $entries){if($map.ContainsKey($entry.Path)){throw 'Duplicate runtime path'}; $map[$entry.Path]=$entry.Hash}
$canonical=($sorted | ForEach-Object {$map[$_]+'  '+$_}) -join "`n"
$sha=[Security.Cryptography.SHA256]::Create()
try {$version=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))).Replace('-','')} finally {$sha.Dispose()}
# Critical entries are the startup fast-check set: every host file, one package.json sentinel per
# public dependency package, and the opencc payload the core loads directly. Executable entry
# payloads stay "hash" (verified on every launch); node.exe, license text and the dependency
# manifests are "metadata" (size and timestamps, hashed only when those differ), because opening
# 92 MB plus 31 manifests on every launch dominates startup for no real gain. Deployment, explicit
# repair and the full dependency check still verify every payload's content.
$hashMode=@(
  'nodejs-project/main.js','nodejs-project/android-server.js','nodejs-project/favorite-scheduler-host.js',
  'nodejs-project/runtime-polyfills.js','nodejs-project/startup-failure.js','nodejs-project/worker-proxy.js',
  'nodejs-project/package.json','nodejs-project/package-lock.json','nodejs-project/runtime_asset_layout.txt',
  'nodejs-project/node_modules/opencc-js/dist/umd/full.js')
$sentinels=@($entries | Where-Object {$_.Path -match '^nodejs-project/node_modules/(@[^/]+/)?[^/]+/package\.json$'})
$critical=@($entries | Where-Object {$_.Path -notlike 'nodejs-project/node_modules/*' -or $_.Path -match '^nodejs-project/node_modules/(dotenv/lib/main.js|opencc-js/dist/umd/full.js)$'})
# Sort-Object -Unique cannot dedupe hashtables; reduce to paths first, then re-attach hashes.
$criticalPaths=@((@($critical | ForEach-Object {$_.Path}) + @($sentinels | ForEach-Object {$_.Path})) | Sort-Object -Unique)
if($criticalPaths.Count -gt 56){throw 'Too many critical runtime entries for the 16 KiB startup receipt'}
$files=@($criticalPaths | ForEach-Object {
  $path=$_
  $mode=if($hashMode -contains $path){'hash'}else{'metadata'}
  [ordered]@{Path=$path;Hash=$map[$path];Mode=$mode}
})
$value=@{Schema=1;Version=$version;Files=$files} | ConvertTo-Json -Depth 5 -Compress
$bytes=[Text.Encoding]::UTF8.GetBytes($value)
if($bytes.Length -gt 16384){throw 'Runtime build identity exceeds 16 KiB'}
[IO.File]::WriteAllBytes((Join-Path ([IO.Path]::GetFullPath($RuntimeBundle)) 'runtime-build.json'),$bytes)
Write-Output ('Runtime build identity: '+$version+'; bytes='+$bytes.Length+'; critical='+$files.Count+'; sentinels='+$sentinels.Count)
