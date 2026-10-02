param(
  [Parameter(Mandatory=$true)][string]$RuntimeBundle,
  [Parameter(Mandatory=$true)][string]$NodeVersion,
  [Parameter(Mandatory=$true)][ValidateSet('x64','x86','arm64')][string]$Arch
)
$ErrorActionPreference='Stop'
$hosts=@('node.exe','NODE-LICENSE.txt','nodejs-project/main.js','nodejs-project/android-server.js','nodejs-project/favorite-scheduler-host.js','nodejs-project/package-lock.json','nodejs-project/package.json','nodejs-project/runtime-polyfills.js','nodejs-project/runtime_asset_layout.txt','nodejs-project/startup-failure.js','nodejs-project/worker-proxy.js','nodejs-project/app-outbound-bridge.js','nodejs-project/app-outbound-runtime.js','nodejs-project/app-outbound-diagnostics.js','nodejs-project/app-outbound-errors.js','nodejs-project/outbound/danmu-outbound.exe','nodejs-project/outbound/outbound-build.json','nodejs-project/outbound/OUTBOUND-LICENSE.txt','nodejs-project/outbound/OUTBOUND-UPSTREAM.txt','nodejs-project/outbound/outbound-source.zip','nodejs-project/outbound/OUTBOUND-NOTICES.txt','nodejs-project/outbound/OUTBOUND-SHA256SUMS.txt')
$entries=@(Get-Content -LiteralPath (Join-Path $RuntimeBundle 'SHA256SUMS.txt') | ForEach-Object {
  if($_ -cnotmatch '^([0-9a-fA-F]{64})  (.+)$'){throw 'Invalid runtime manifest'}
  $path=$Matches[2];$hash=$Matches[1].ToUpperInvariant()
  if($path.Contains('\') -or $path.Contains(':') -or $path.StartsWith('/')){throw "Unsafe runtime path: $path"}
  foreach($part in $path.Split('/')){if(-not $part -or $part -in '.','..' -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '[\x00-\x1f<>"|?*]' -or $part -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)'){throw "Unsafe runtime path: $path"}}
  if($hosts -cnotcontains $path -and -not $path.StartsWith('nodejs-project/node_modules/',[StringComparison]::Ordinal)){throw "Runtime manifest exceeds app-owned boundary: $path"}
  @{Path=$path;Hash=$hash}
})
$sorted=[string[]]@($entries | ForEach-Object {$_.Path})
[Array]::Sort($sorted,[StringComparer]::Ordinal)
$map=@{};foreach($entry in $entries){if($map.ContainsKey($entry.Path)){throw 'Duplicate runtime path'};$map[$entry.Path]=$entry.Hash}
if(-not $map.ContainsKey('node.exe') -or -not $map.ContainsKey('nodejs-project/main.js')){throw 'Runtime manifest lacks Node or entry point'}
foreach($path in $sorted){$parts=$path.Split('/');for($i=1;$i -lt $parts.Length;$i++){if($map.ContainsKey(($parts[0..($i-1)] -join '/'))){throw 'Runtime file/directory conflict'}}}
if(@($sorted | Where-Object {$_ -like 'nodejs-project/app-outbound-*' -or $_ -like 'nodejs-project/outbound/*'}).Count -gt 0){foreach($path in $hosts){if(-not $map.ContainsKey($path)){throw "Outbound runtime manifest incomplete: $path"}}}
$canonical=($sorted | ForEach-Object {$map[$_]+'  '+$_}) -join "`n"
$sha=[Security.Cryptography.SHA256]::Create()
try{$version=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))).Replace('-','')}finally{$sha.Dispose()}
# Native helper and the bridge's scripts/manifests are always content checked. License/source payloads
# retain metadata mode; explicit preparation and repair still hash the full managed manifest once.
$hashMode=@('nodejs-project/main.js','nodejs-project/android-server.js','nodejs-project/favorite-scheduler-host.js','nodejs-project/runtime-polyfills.js','nodejs-project/startup-failure.js','nodejs-project/worker-proxy.js','nodejs-project/package.json','nodejs-project/package-lock.json','nodejs-project/runtime_asset_layout.txt','nodejs-project/node_modules/opencc-js/dist/umd/full.js','nodejs-project/app-outbound-bridge.js','nodejs-project/app-outbound-runtime.js','nodejs-project/app-outbound-diagnostics.js','nodejs-project/app-outbound-errors.js','nodejs-project/outbound/danmu-outbound.exe','nodejs-project/outbound/outbound-build.json','nodejs-project/outbound/OUTBOUND-SHA256SUMS.txt')
$sentinels=@($entries | Where-Object {$_.Path -match '^nodejs-project/node_modules/(@[^/]+/)?[^/]+/package\.json$'})
$critical=@($entries | Where-Object {$_.Path -notlike 'nodejs-project/node_modules/*' -or $_.Path -match '^nodejs-project/node_modules/(dotenv/lib/main.js|opencc-js/dist/umd/full.js)$'})
$criticalPaths=@((@($critical | ForEach-Object {$_.Path})+@($sentinels | ForEach-Object {$_.Path})) | Sort-Object -Unique)
if($criticalPaths.Count -gt 56){throw 'Too many critical runtime entries for the 16 KiB startup receipt'}
$files=@($criticalPaths | ForEach-Object {$path=$_;$mode=if($hashMode -contains $path){'hash'}else{'metadata'};[ordered]@{Path=$path;Hash=$map[$path];Mode=$mode}})
$value=@{Schema=1;Version=$version;NodeVersion=$NodeVersion;Arch=$Arch;Files=$files} | ConvertTo-Json -Depth 5 -Compress
$bytes=[Text.Encoding]::UTF8.GetBytes($value)
if($bytes.Length -gt 16384){throw 'Runtime build identity exceeds 16 KiB'}
[IO.File]::WriteAllBytes((Join-Path ([IO.Path]::GetFullPath($RuntimeBundle)) 'runtime-build.json'),$bytes)
Write-Output ('Runtime build identity: '+$version+'; Node='+$NodeVersion+'; Arch='+$Arch+'; bytes='+$bytes.Length+'; critical='+$files.Count+'; sentinels='+$sentinels.Count)
