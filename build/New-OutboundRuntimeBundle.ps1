[CmdletBinding(DefaultParameterSetName='Create')]
param(
  [Parameter(Mandatory=$true)][string]$BaseBundle,
  [Parameter(Mandatory=$true,ParameterSetName='Create')][string]$Destination,
  [Parameter(Mandatory=$true)][ValidateSet('x64','x86','arm64')][string]$Arch,
  [Parameter(Mandatory=$true,ParameterSetName='Create')][string]$OutboundArtifact,
  [Parameter(ParameterSetName='Create')][string]$HostSource='',
  [Parameter(Mandatory=$true,ParameterSetName='Validate')][switch]$ValidateOnly
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$hostFiles=@('main.js','android-server.js','favorite-scheduler-host.js','runtime-polyfills.js','startup-failure.js','worker-proxy.js','package.json','package-lock.json','runtime_asset_layout.txt','app-outbound-bridge.js','app-outbound-runtime.js','app-outbound-diagnostics.js','app-outbound-errors.js')
$helperFiles=@('danmu-outbound.exe','outbound-build.json','OUTBOUND-LICENSE.txt','OUTBOUND-UPSTREAM.txt','outbound-source.zip','OUTBOUND-NOTICES.txt','OUTBOUND-SHA256SUMS.txt')
$owned=@('node.exe','NODE-LICENSE.txt')+@($hostFiles | ForEach-Object {'nodejs-project/'+$_})+@($helperFiles | ForEach-Object {'nodejs-project/outbound/'+$_})
$overlaid=@($hostFiles | Where-Object {$_ -notin 'package.json','package-lock.json'} | ForEach-Object {'nodejs-project/'+$_})+@($helperFiles | ForEach-Object {'nodejs-project/outbound/'+$_})
$expectedMachine=@{x64=0x8664;x86=0x014c;arm64=0xaa64}[$Arch]
$repo=Split-Path $PSScriptRoot -Parent
# PowerShell 5.1 -File evaluates parameter defaults before PSScriptRoot is available.
# Resolve the documented default in the script body; an explicit empty value remains invalid.
if($PSCmdlet.ParameterSetName -eq 'Create' -and -not $PSBoundParameters.ContainsKey('HostSource')){
  $HostSource=Join-Path $repo 'runtime\node-host'
}
if($PSCmdlet.ParameterSetName -eq 'Create' -and [string]::IsNullOrWhiteSpace($HostSource)){throw 'HostSource must be a nonempty path when explicitly supplied'}
[xml]$props=Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props')
$expectedNode=[string]$props.Project.PropertyGroup.DanmuBundledNodeVersion
if(-not $expectedNode){throw 'Bundled Node version is not declared'}
function Assert-SafePath([string]$path){
  if(-not $path -or $path.Contains('\') -or $path.Contains(':') -or $path.StartsWith('/')){throw "Unsafe manifest path: $path"}
  foreach($part in $path.Split('/')){
    if(-not $part -or $part -in '.','..' -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '[\x00-\x1f<>"|?*]' -or $part -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)'){throw "Unsafe manifest path: $path"}
  }
}
function Assert-Owned([string]$path){
  Assert-SafePath $path
  if($owned -cnotcontains $path -and -not $path.StartsWith('nodejs-project/node_modules/',[StringComparison]::Ordinal)){throw "Runtime manifest exceeds app-owned boundary: $path"}
}
function Assert-NoReparse([string]$path){
  $current=[IO.Path]::GetFullPath($path)
  while($current){
    if(Test-Path -LiteralPath $current){if((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Reparse point forbidden: $current"}}
    $current=[IO.Path]::GetDirectoryName($current)
  }
}
function Read-Hashes([string]$path,[switch]$Runtime){
  Assert-NoReparse $path
  $map=New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
  foreach($line in [IO.File]::ReadAllLines($path)){
    if($line -cnotmatch '^([0-9a-fA-F]{64})  (.+)$'){throw "Invalid SHA256 manifest: $path"}
    $digest=$Matches[1].ToUpperInvariant();$name=$Matches[2]
    Assert-SafePath $name
    if($Runtime){Assert-Owned $name}
    if($map.ContainsKey($name)){throw "Duplicate SHA256 path: $name"}
    $map.Add($name,$digest)
  }
  if($map.Count -eq 0){throw "Empty SHA256 manifest: $path"}
  foreach($name in $map.Keys){$parts=$name.Split('/'); for($i=1;$i -lt $parts.Length;$i++){if($map.ContainsKey(($parts[0..($i-1)] -join '/'))){throw "File/directory manifest conflict: $name"}}}
  return ,$map
}
function Get-Version($map){
  $names=[string[]]@($map.Keys);[Array]::Sort($names,[StringComparer]::Ordinal)
  $text=($names | ForEach-Object {$map[$_]+'  '+$_}) -join "`n"
  $sha=[Security.Cryptography.SHA256]::Create()
  try{return [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).Replace('-','')}finally{$sha.Dispose()}
}
function Assert-Pe([string]$path){
  Assert-NoReparse $path
  $stream=[IO.File]::OpenRead($path);$reader=New-Object IO.BinaryReader($stream)
  try{
    if($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5a4d){throw "Missing MZ header: $path"}
    $stream.Position=0x3c;$offset=$reader.ReadInt32()
    if($offset -lt 64 -or $offset -gt $stream.Length-24){throw "Invalid PE offset: $path"}
    $stream.Position=$offset
    if($reader.ReadUInt32() -ne 0x4550){throw "Missing PE signature: $path"}
    $machine=$reader.ReadUInt16()
    if($machine -ne $expectedMachine){throw ('PE architecture mismatch: {0}; machine=0x{1:X4}; expected {2}' -f $path,$machine,$Arch)}
  }finally{$reader.Dispose();$stream.Dispose()}
}
function Assert-Hashes([string]$directory,$map){
  foreach($name in $map.Keys){$path=Join-Path $directory $name;Assert-NoReparse $path
    if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "Manifest payload missing: $name"}
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $map[$name]){throw "SHA256 mismatch: $name"}
  }
}
function Read-Redis([string]$directory,$runtimeMap){
  $path=Join-Path $directory 'redis-payload.SHA256SUMS.txt'
  if(-not(Test-Path -LiteralPath $path)){return $null}
  $map=Read-Hashes $path
  foreach($name in $map.Keys){
    if($name -cnotmatch '^(redis|@redis/[^/]+|cluster-key-slot)/'){throw "Foreign optional Redis payload: $name"}
    if($runtimeMap.ContainsKey('nodejs-project/node_modules/'+$name)){throw 'Optional Redis overlaps managed manifest'}
  }
  Assert-Hashes (Join-Path $directory 'nodejs-project/node_modules') $map
  return ,$map
}
function Assert-ClosedTree([string]$directory,$runtimeMap,$redisMap){
  Assert-NoReparse $directory
  $files=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
  $dirs=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
  foreach($name in @($runtimeMap.Keys)+@('SHA256SUMS.txt','runtime-build.json')){$null=$files.Add($name)}
  if($redisMap){$null=$files.Add('redis-payload.SHA256SUMS.txt');foreach($name in $redisMap.Keys){$null=$files.Add('nodejs-project/node_modules/'+$name)}}
  foreach($name in $files){$parts=$name.Split('/');for($i=1;$i -lt $parts.Length;$i++){$null=$dirs.Add(($parts[0..($i-1)] -join '/'))}}
  $pending=New-Object 'System.Collections.Generic.Queue[string]';$pending.Enqueue($directory)
  while($pending.Count){foreach($item in Get-ChildItem -LiteralPath $pending.Dequeue() -Force){
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Reparse point forbidden: $($item.FullName)"}
    $relative=$item.FullName.Substring($directory.Length+1).Replace('\','/')
    Assert-SafePath $relative
    if($item.PSIsContainer){
      if(-not $dirs.Contains($relative)){throw "Unlisted runtime directory: $relative"}
      $pending.Enqueue($item.FullName)
    }elseif(-not $files.Contains($relative)){throw "Unlisted runtime file: $relative"}
  }}
}
function Assert-Identity([string]$directory,$map,[switch]$Complete){
  $path=Join-Path $directory 'runtime-build.json';Assert-NoReparse $path
  if((Get-Item -LiteralPath $path).Length -gt 16384){throw 'Runtime identity exceeds 16 KiB'}
  $identity=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
  if($identity.Schema -ne 1 -or $identity.Version -cne (Get-Version $map) -or $identity.NodeVersion -cne $expectedNode -or $identity.Arch -cne $Arch){throw 'Runtime identity version/Node/architecture mismatch'}
  $seen=@{}
  foreach($entry in $identity.Files){
    Assert-Owned $entry.Path
    if($seen.ContainsKey($entry.Path) -or -not $map.ContainsKey($entry.Path) -or $entry.Hash -cne $map[$entry.Path]){throw 'Runtime identity entries disagree with SHA256 manifest'}
    if($entry.Mode -notin 'hash','metadata'){throw 'Invalid startup hash mode'}
    $seen[$entry.Path]=$true
  }
  if($identity.Files.Count -gt 56){throw 'Critical runtime count exceeds 56'}
  if($Complete){foreach($name in $owned){if(-not $seen.ContainsKey($name)){throw "Startup identity omits owned payload: $name"}}
    foreach($entry in $identity.Files){if(($entry.Path -like 'nodejs-project/app-outbound-*.js' -or $entry.Path -in 'nodejs-project/outbound/danmu-outbound.exe','nodejs-project/outbound/outbound-build.json','nodejs-project/outbound/OUTBOUND-SHA256SUMS.txt') -and $entry.Mode -cne 'hash'){throw "Outbound critical payload is not hash checked: $($entry.Path)"}}
  }
}
function Assert-Source([string]$directory,$metadata){
  if($metadata.sourceInputs -isnot [Array] -or $metadata.sourceInputs.Count -eq 0){throw 'Invalid outbound sourceInputs'}
  $inputs=New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::Ordinal)
  foreach($input in $metadata.sourceInputs){
    Assert-SafePath $input.path
    if($input.path -cnotlike 'runtime/outbound/*' -and $input.path -cnotin 'build/Build-OutboundHelper.ps1','build/Get-GoToolchain.ps1'){throw 'Invalid outbound source input path'}
    if($input.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $inputs.ContainsKey($input.path)){throw 'Invalid outbound source input SHA256/duplicate'}
    $inputs.Add($input.path,$input.sha256)
  }
  $names=[string[]]@($inputs.Keys);[Array]::Sort($names,[StringComparer]::Ordinal)
  $canonical=[string]::Concat(@($names | ForEach-Object {$inputs[$_]+'  '+$_+"`n"}))
  $sha=[Security.Cryptography.SHA256]::Create()
  try{$aggregate=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
  if($aggregate -cne $metadata.sourceSha256){throw 'Outbound source aggregate SHA256 mismatch'}
  $zipPath=Join-Path $directory 'outbound-source.zip';Assert-NoReparse $zipPath
  $zip=[IO.Compression.ZipFile]::OpenRead($zipPath);$seen=@{}
  try{foreach($entry in $zip.Entries){
    Assert-SafePath $entry.FullName
    if($seen.ContainsKey($entry.FullName)){throw 'Duplicate outbound archive path'};$seen[$entry.FullName]=$true
    if(-not $inputs.ContainsKey($entry.FullName)){if(-not $entry.FullName.StartsWith('licenses/',[StringComparison]::Ordinal)){throw "Undeclared outbound archive input: $($entry.FullName)"};continue}
    $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
    try{$hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose();$stream.Dispose()}
    if($hash -cne $inputs[$entry.FullName]){throw "Outbound archive source SHA256 mismatch: $($entry.FullName)"}
  };foreach($name in $names){if(-not $seen.ContainsKey($name)){throw "Outbound archive source missing: $name"}}}finally{$zip.Dispose()}
}
function Assert-Helper([string]$directory,$runtimeMap){
  $hashes=Read-Hashes (Join-Path $directory 'OUTBOUND-SHA256SUMS.txt')
  if($hashes.Count -ne $helperFiles.Count-1){throw 'Outbound SHA256 manifest is incomplete'}
  foreach($name in $hashes.Keys){if($helperFiles -cnotcontains $name -or $name -ceq 'OUTBOUND-SHA256SUMS.txt'){throw "Foreign outbound payload: $name"}
    if($runtimeMap -and $runtimeMap['nodejs-project/outbound/'+$name] -cne $hashes[$name]){throw "Outbound/runtime manifest mismatch: $name"}
  }
  $metadataPath=Join-Path $directory 'outbound-build.json';Assert-NoReparse $metadataPath
  if((Get-Item -LiteralPath $metadataPath).Length -gt 16384){throw 'Outbound metadata exceeds 16 KiB'}
  $metadata=Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
  if($metadata.schemaVersion -ne 1 -or $metadata.protocolVersion -ne 1 -or $metadata.version -cne '1.0.1+6004732' -or $metadata.runtimeIdentifier -cne ('win-'+$Arch) -or $metadata.goVersion -cne 'go1.26.0' -or $metadata.sourceCommit -cne '600473250a07d0f78502262d141e0f3faf4a9a36' -or $metadata.machine -ne $expectedMachine){throw 'Outbound metadata version/protocol/RID/PE identity mismatch'}
  if($metadata.executableSha256 -cnotmatch '^[0-9a-f]{64}$' -or $metadata.executableSha256.ToUpperInvariant() -cne $hashes['danmu-outbound.exe'] -or
    $metadata.toolchainArchiveSha256 -cne '9bbe0fc64236b2b51f6255c05c4232532b8ecc0e6d2e00950bd3021d8a4d07d4' -or
    $metadata.toolchainArchiveUrl -cne 'https://go.dev/dl/go1.26.0.windows-amd64.zip' -or $metadata.toolchainArchiveBytes -isnot [int] -or $metadata.toolchainArchiveBytes -ne 74815266){throw 'Outbound executable/official toolchain metadata mismatch'}
  if($metadata.toolchainInputCount -isnot [int] -or $metadata.toolchainInputCount -le 0){throw 'Missing complete official Go input provenance'}
  foreach($key in 'toolchainInputSha256','toolchainDriverSha256','toolchainCompilerSha256','toolchainLinkerSha256','toolchainStandardLibrarySha256'){
    if($metadata.$key -isnot [string] -or $metadata.$key -cnotmatch '^[0-9a-f]{64}$'){throw "Missing Go toolchain provenance: $key"}
  }
  if($metadata.goEnvironment -isnot [pscustomobject] -or @($metadata.goEnvironment.PSObject.Properties).Count -ne 4){throw 'Outbound Go environment must contain exactly four pinned keys'}
  if($metadata.goEnvironment.GOROOT -cne 'verified-official-archive-root' -or $metadata.goEnvironment.GOTOOLDIR -cne 'pkg/tool/windows_amd64' -or
    $metadata.goEnvironment.GOENV -cne 'off' -or $metadata.goEnvironment.GOTOOLCHAIN -cne 'local'){throw 'Outbound Go environment is not isolated to the verified tree'}
  if($metadata.dependencies -isnot [Array] -or $metadata.buildFlags -isnot [Array]){throw 'Invalid outbound dependencies/buildFlags'}
  foreach($dependency in $metadata.dependencies){foreach($key in 'path','version','sum','goModSum'){if($dependency.$key -isnot [string] -or -not $dependency.$key){throw "Invalid outbound dependency $key"}}}
  foreach($flag in $metadata.buildFlags){if($flag -isnot [string]){throw 'Invalid outbound build flag'}}
  Assert-Hashes $directory $hashes
  Assert-Pe (Join-Path $directory 'danmu-outbound.exe')
  Assert-Source $directory $metadata
  return ,$hashes
}
$base=[IO.Path]::GetFullPath($BaseBundle).TrimEnd('\');Assert-NoReparse $base
$baseMap=Read-Hashes (Join-Path $base 'SHA256SUMS.txt') -Runtime
if($ValidateOnly){
  foreach($name in $owned){if(-not $baseMap.ContainsKey($name)){throw "Outbound release runtime is incomplete: $name"}}
  Assert-Identity $base $baseMap -Complete
  Assert-Pe (Join-Path $base 'node.exe')
  $null=Assert-Helper (Join-Path $base 'nodejs-project\outbound') $baseMap
  Assert-Hashes $base $baseMap
  $redisMap=Read-Redis $base $baseMap
  Assert-ClosedTree $base $baseMap $redisMap
  Write-Output ('Outbound runtime verified offline: Arch='+$Arch+'; managed='+$baseMap.Count+'; native execution=not performed')
  return
}
$dest=[IO.Path]::GetFullPath($Destination).TrimEnd('\');$hostRoot=[IO.Path]::GetFullPath($HostSource).TrimEnd('\');$artifact=[IO.Path]::GetFullPath($OutboundArtifact).TrimEnd('\')
foreach($path in @($dest,$hostRoot,$artifact)){Assert-NoReparse $path}
foreach($source in @($base,$hostRoot,$artifact)){
  if($dest.Equals($source,[StringComparison]::OrdinalIgnoreCase) -or $dest.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase) -or $source.StartsWith($dest+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Bundle destination must not overlap any input'}
}
if(Test-Path -LiteralPath $dest){throw 'Destination exists; old artifacts and user data must not be overwritten'}
Assert-Identity $base $baseMap
Assert-Pe (Join-Path $base 'node.exe')
$artifactMap=Assert-Helper $artifact $null
foreach($file in @('package.json','package-lock.json')){
  $source=Join-Path $hostRoot $file;Assert-NoReparse $source
  if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne $baseMap['nodejs-project/'+$file]){throw "Host $file differs from baseline production dependencies; dependency overlay refused"}
}
# Create is intentionally a manifest-driven projection of a possibly dirty baseline:
# unlisted cores/config/logs/cache are not inspected or copied. ValidateOnly instead
# applies the closed-tree gate to the finished release tree, including optional Redis.
$redisManifest=Join-Path $base 'redis-payload.SHA256SUMS.txt'
$redisMap=Read-Redis $base $baseMap
$created=$false
try{
  New-Item -ItemType Directory -Path $dest -ErrorAction Stop | Out-Null;$created=$true
  foreach($name in $baseMap.Keys){$source=Join-Path $base $name;Assert-NoReparse $source;$target=Join-Path $dest $name
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null;[IO.File]::Copy($source,$target,$false)
  }
  if($redisMap){Assert-Hashes (Join-Path $base 'nodejs-project\node_modules') $redisMap
    foreach($name in $redisMap.Keys){$target=Join-Path $dest ('nodejs-project/node_modules/'+$name);New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null;[IO.File]::Copy((Join-Path $base ('nodejs-project/node_modules/'+$name)),$target,$false)}
    [IO.File]::Copy($redisManifest,(Join-Path $dest 'redis-payload.SHA256SUMS.txt'),$false)
  }
  foreach($file in $hostFiles){if($file -in 'package.json','package-lock.json'){continue};$source=Join-Path $hostRoot $file;Assert-NoReparse $source;[IO.File]::Copy($source,(Join-Path $dest ('nodejs-project/'+$file)),$true)}
  $helperDest=Join-Path $dest 'nodejs-project\outbound';New-Item -ItemType Directory -Path $helperDest -Force | Out-Null
  foreach($file in $helperFiles){$source=Join-Path $artifact $file;Assert-NoReparse $source;[IO.File]::Copy($source,(Join-Path $helperDest $file),$true)}
  $map=New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
  foreach($name in @($baseMap.Keys)+@($owned)){
    if($map.ContainsKey($name)){continue};Assert-Owned $name
    $path=Join-Path $dest $name;$digest=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    # New host files and helper payloads are intentional; every preserved dependency must match BASE.
    if($baseMap.ContainsKey($name) -and $overlaid -cnotcontains $name -and $digest -cne $baseMap[$name]){throw "Baseline payload SHA256 mismatch: $name"}
    if($name.StartsWith('nodejs-project/outbound/') -and $name -cne 'nodejs-project/outbound/OUTBOUND-SHA256SUMS.txt' -and $digest -cne $artifactMap[$name.Substring('nodejs-project/outbound/'.Length)]){throw "Copied helper SHA256 mismatch: $name"}
    $map.Add($name,$digest)
  }
  $names=[string[]]@($map.Keys);[Array]::Sort($names,[StringComparer]::Ordinal)
  [IO.File]::WriteAllLines((Join-Path $dest 'SHA256SUMS.txt'),[string[]]@($names | ForEach-Object {$map[$_]+'  '+$_}),[Text.Encoding]::ASCII)
  & (Join-Path $PSScriptRoot 'New-RuntimeBuildIdentity.ps1') -RuntimeBundle $dest -NodeVersion $expectedNode -Arch $Arch
  Assert-Identity $dest $map -Complete
  Assert-Pe (Join-Path $dest 'node.exe')
  $null=Assert-Helper $helperDest $map
  Assert-Hashes $dest $map
  $copiedRedis=Read-Redis $dest $map
  Assert-ClosedTree $dest $map $copiedRedis
  Write-Output ('Created outbound runtime: '+$dest+'; Arch='+$Arch+'; managed='+$map.Count+'; native execution=not performed')
}catch{
  $failure=$_
  if($created){try{Assert-NoReparse $dest;Remove-Item -LiteralPath $dest -Recurse -Force -ErrorAction Stop}catch{throw ('Bundle creation failed: '+$failure.Exception.Message+'; cleanup failed: '+$_.Exception.Message)}}
  throw $failure
}
