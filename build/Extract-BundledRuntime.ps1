param([Parameter(Mandatory=$true)][string]$LegacyZip,[Parameter(Mandatory=$true)][string]$Destination,[Parameter(Mandatory=$true)][string]$NpmCli,[Parameter(Mandatory=$true)][string]$NodeArchive,[string]$OptionalRedisNodeModules)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
if(Test-Path $Destination){throw 'Destination exists'}
New-Item -ItemType Directory $Destination | Out-Null
$names=@('main.js','android-server.js','favorite-scheduler-host.js','runtime-polyfills.js','startup-failure.js','worker-proxy.js','package.json','package-lock.json','runtime_asset_layout.txt')
$outer=[IO.Compression.ZipFile]::OpenRead($LegacyZip)
try {
  $jarEntry=@($outer.Entries | Where-Object {$_.FullName -match '/app/desktop-0\.1\.0-.*\.jar$'})
  if($jarEntry.Count -ne 1){throw 'Expected one runtime resource JAR'}
  $stream=New-Object IO.MemoryStream
  $source=$jarEntry[0].Open(); try {$source.CopyTo($stream)} finally {$source.Dispose()}
  $stream.Position=0
  $jar=New-Object IO.Compression.ZipArchive($stream,[IO.Compression.ZipArchiveMode]::Read)
  try {
    foreach($entry in $jar.Entries){
      $name=$entry.FullName
      $allowed=$name -eq 'runtime/node.exe' -or $name.StartsWith('runtime/nodejs-project/node_modules/') -or ($name.StartsWith('runtime/nodejs-project/') -and $names -contains $name.Substring('runtime/nodejs-project/'.Length))
      if(-not $allowed -or $name.EndsWith('/')){continue}
      $relative=$name.Substring('runtime/'.Length)
      if($relative.Split('/') -contains '..'){throw 'Unsafe archive path'}
      $target=Join-Path $Destination $relative
      New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
      [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$false)
    }
  } finally {$jar.Dispose();$stream.Dispose()}
} finally {$outer.Dispose()}
$node=Join-Path $Destination 'node.exe'
& $node $NpmCli ci --omit=dev --ignore-scripts --prefix (Join-Path $Destination 'nodejs-project')
if($LASTEXITCODE -ne 0){throw 'Locked production dependency installation failed'}

# 可选 Redis 载荷：核心只在配置 LOCAL_REDIS_URL 时才 import 它，因此不进主受管清单，
# 而是单独写 redis-payload.SHA256SUMS.txt，由 OptionalRedisStager 在启动时按配置铺开。
# npm ci 会重建整个 node_modules，所以这一步必须在它之后。
if($OptionalRedisNodeModules){
  & (Join-Path $PSScriptRoot 'Add-OptionalRedisPayload.ps1') -RuntimeBundle ([IO.Path]::GetFullPath($Destination)) -OptionalRedisNodeModules $OptionalRedisNodeModules
} else {
  Write-Output 'Optional redis payload skipped (-OptionalRedisNodeModules not provided)'
}
$payloadPaths=@()
$payloadManifest=Join-Path $Destination 'redis-payload.SHA256SUMS.txt'
if(Test-Path $payloadManifest){
  # 载荷文件不进受管清单；Add-OptionalRedisPayload 同时也已把它们从清单里剔掉。
  $payloadPaths=@(Get-Content -LiteralPath $payloadManifest |
    Where-Object { $_.Length -ge 67 } |
    ForEach-Object { 'nodejs-project/node_modules/' + $_.Substring(66) })
}

$nodeZip=[IO.Compression.ZipFile]::OpenRead($NodeArchive)
try {
  $licenses=@($nodeZip.Entries | Where-Object {$_.FullName -match '^node-[^/]+/LICENSE$'})
  if($licenses.Count -ne 1){throw 'Node license missing'}
  [IO.Compression.ZipFileExtensions]::ExtractToFile($licenses[0],(Join-Path $Destination 'NODE-LICENSE.txt'),$false)
} finally {$nodeZip.Dispose()}
$root=[IO.Path]::GetFullPath($Destination).TrimEnd('\')
$lines=Get-ChildItem $root -File -Recurse | ForEach-Object {
  $relative=$_.FullName.Substring($root.Length+1).Replace('\','/')
  # Redis 载荷不进受管清单：它由 OptionalRedisStager 按 LOCAL_REDIS_URL 条件铺开并单独校验。
  if($payloadPaths -contains $relative){return}
  (Get-FileHash $_.FullName -Algorithm SHA256).Hash+'  '+$relative
}
$lines | Set-Content (Join-Path $root 'SHA256SUMS.txt') -Encoding ASCII
& (Join-Path $PSScriptRoot 'New-RuntimeBuildIdentity.ps1') -RuntimeBundle $root
Write-Output ('Bundled dependency files: '+$lines.Count)
