param([Parameter(Mandatory=$true)][string]$Dotnet,[Parameter(Mandatory=$true)][string]$InnoCompiler,[Parameter(Mandatory=$true)][string]$SignTool,[Parameter(Mandatory=$true)][string]$SigningIdentity,[string]$OutputDirectory,[Parameter(Mandatory=$true)][string]$RuntimeBundle,[Parameter(Mandatory=$true)][string]$GitBundle,[string]$NodeVersion,[ValidateSet('x64','x86','arm64')][string]$NodeArch,[ValidateSet('win-x64','win-x86','win-arm64')][string]$Arch='win-x64')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
[xml]$props=Get-Content (Join-Path $root 'Directory.Build.props')
$version=[string]$props.Project.PropertyGroup.Version
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root ('artifacts\signed-'+$version)}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path $output){throw 'Release output exists; use a fresh directory to preserve earlier artifacts.'}
# The PR merge service launches <app>\git\cmd\git.exe; a system Git or a lone git.exe is not a release dependency.
$gitRoot=[IO.Path]::GetFullPath($GitBundle)
$gitBin=@{'win-x86'='mingw32';'win-x64'='mingw64';'win-arm64'='clangarm64'}[$Arch]
$gitExe=Join-Path $gitRoot 'cmd\git.exe'
# Only the executables that actually run as Git itself must match the target architecture.
# usr\bin\sh.exe is deliberately NOT architecture-checked: Git for Windows ships the x86-64 sh.exe
# inside the arm64 MinGit as well (verified: identical SHA256 to the x64 bundle), relying on
# Windows 11 ARM64's x64 emulation. Demanding an arm64 sh.exe here would reject the official bundle.
# Its presence is still required below.
$gitArchFiles=@('cmd\git.exe',"$gitBin\bin\git.exe","$gitBin\bin\git-remote-https.exe")
$gitFiles=@($gitArchFiles + 'usr\bin\sh.exe')
foreach($relative in @($gitFiles + 'etc\gitconfig')){
  if(-not (Test-Path -LiteralPath (Join-Path $gitRoot $relative) -PathType Leaf)){throw "Portable Git bundle is incomplete: $relative"}
}
$machine=@{'win-x86'=0x014c;'win-x64'=0x8664;'win-arm64'=0xaa64}[$Arch]
foreach($relative in $gitArchFiles){
  $path=Join-Path $gitRoot $relative
  $gitVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion
  if($gitVersion -ne '2.55.0.windows.5'){throw "Portable Git version mismatch: $relative is $gitVersion (expected 2.55.0.windows.5)"}
  $stream=[IO.File]::OpenRead($path)
  try {
    $reader=New-Object IO.BinaryReader($stream)
    $stream.Position=0x3c
    $stream.Position=$reader.ReadInt32()+4
    $actualMachine=$reader.ReadUInt16()
  } finally {$stream.Dispose()}
  if($actualMachine -ne $machine){throw ('Portable Git architecture mismatch: {0} is 0x{1:X4} for {2}' -f $relative,$actualMachine,$Arch)}
}
# This release requires the complete app-owned outbound runtime. Validate before publish/signing;
# old bundle fixtures still work with Prepare but cannot silently produce a release without the feature.
$archName=$Arch.Replace('win-','')
if($NodeArch -and $NodeArch -cne $archName){throw 'Node architecture must equal the release process RID'}
if($NodeVersion -and $NodeVersion -cne [string]$props.Project.PropertyGroup.DanmuBundledNodeVersion){throw 'Node version must equal the compiled host contract'}
& (Join-Path $PSScriptRoot 'New-OutboundRuntimeBundle.ps1') -BaseBundle $RuntimeBundle -Arch $archName -ValidateOnly
New-Item -ItemType Directory $output | Out-Null
$publish=Join-Path $output 'publish'
$env:DANMU_SIGNTOOL=[IO.Path]::GetFullPath($SignTool)
$env:DANMU_SIGNING_IDENTITY=[IO.Path]::GetFullPath($SigningIdentity)
$restoreArgs=@(); if($env:DANMU_SKIP_RESTORE -eq '1'){$restoreArgs=@('--no-restore')}
# Do NOT add -p:PublishSingleFile=true here. Command-line -p: properties are GLOBAL, so it would
# apply to DanmuApi.Core/.Runtime/.Platform/.Tests too, and the SDK then injects the implicit
# Microsoft.NET.ILLink.Tasks package reference into every project's restore graph. That rewrites
# the per-RID packages.lock.*.json files (RestorePackagesWithLockFile is on) with a dependency the
# plain `dotnet restore -p:CI=true` gate does not produce, so locked-mode RID restores fail with
# NU1004 right after a release build. The App project declares PublishSingleFile itself.
& $Dotnet publish (Join-Path $root 'src\DanmuApi.App\DanmuApi.App.csproj') -c Release --runtime $Arch --self-contained true -p:PublishTrimmed=false -p:BuiltInComInteropSupport=true -p:PublishReadyToRun=false "-p:PublishDir=$publish\" @restoreArgs
if($LASTEXITCODE -ne 0){throw 'Publish failed'}
if(-not(Test-Path (Join-Path $RuntimeBundle 'node.exe')) -or -not(Test-Path (Join-Path $RuntimeBundle 'SHA256SUMS.txt'))){throw 'Required runtime bundle is incomplete'}
# Directory.Build.props declares the Node version this host was built against; the same value is
# compiled into DanmuApi.Core and asserted against node.exe at start-up.
$nodeVersion=if($NodeVersion){$NodeVersion}else{[string]$props.Project.PropertyGroup.DanmuBundledNodeVersion}
if(-not $nodeVersion){throw 'DanmuBundledNodeVersion is not declared in Directory.Build.props'}
# The runtime identifier decides the architecture of both the host and its bundled Node runtime.
$archName=$Arch.Replace('win-','')
$nodeArch=if($NodeArch){$NodeArch}else{$archName}
# Project only the previously validated manifests; recursive copying would reopen the
# unlisted-file channel between validation and packaging. Validate the final tree below.
$publishedRuntime=Join-Path $publish 'runtime-bundle'
if(Test-Path -LiteralPath $publishedRuntime){throw 'Published app already contains a runtime bundle; refusing to merge trees'}
$runtimeFiles=New-Object 'System.Collections.Generic.List[string]'
foreach($line in [IO.File]::ReadAllLines((Join-Path $RuntimeBundle 'SHA256SUMS.txt'))){
  if($line -cnotmatch '^[0-9a-fA-F]{64}  (.+)$'){throw 'Invalid verified runtime manifest'}
  $runtimeFiles.Add($Matches[1])
}
$runtimeFiles.Add('SHA256SUMS.txt');$runtimeFiles.Add('runtime-build.json')
$redisManifest=Join-Path $RuntimeBundle 'redis-payload.SHA256SUMS.txt'
if(Test-Path -LiteralPath $redisManifest){
  $runtimeFiles.Add('redis-payload.SHA256SUMS.txt')
  foreach($line in [IO.File]::ReadAllLines($redisManifest)){
    if($line -cnotmatch '^[0-9a-fA-F]{64}  (.+)$'){throw 'Invalid verified optional Redis manifest'}
    $runtimeFiles.Add('nodejs-project/node_modules/'+$Matches[1])
  }
}
foreach($relative in $runtimeFiles){
  $target=Join-Path $publishedRuntime $relative
  New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
  [IO.File]::Copy((Join-Path $RuntimeBundle $relative),$target,$false)
}
& (Join-Path $PSScriptRoot 'New-OutboundRuntimeBundle.ps1') -BaseBundle $publishedRuntime -Arch $archName -ValidateOnly
if(Test-Path (Join-Path $publish 'git')){throw 'Published app already contains a git directory; refusing to merge bundles'}
Copy-Item -LiteralPath $gitRoot -Destination (Join-Path $publish 'git') -Recurse
# Stamp the published copy from nodejs.org so the runtime's origin is reproducible instead of an
# archived legacy package. The source bundle is left untouched.
& (Join-Path $PSScriptRoot 'Get-OfficialNodeRuntime.ps1') -RuntimeBundle (Join-Path $publish 'runtime-bundle') -NodeVersion $nodeVersion -Arch $nodeArch
if($LASTEXITCODE -ne 0){throw 'Stamping the official Node runtime failed'}
& (Join-Path $PSScriptRoot 'New-RuntimeBuildIdentity.ps1') -RuntimeBundle (Join-Path $publish 'runtime-bundle') -NodeVersion $nodeVersion -Arch $nodeArch
if($LASTEXITCODE -ne 0){throw 'Runtime build identity failed'}
& (Join-Path $PSScriptRoot 'New-OutboundRuntimeBundle.ps1') -BaseBundle (Join-Path $publish 'runtime-bundle') -Arch $archName -ValidateOnly
$signScript=Join-Path $PSScriptRoot 'Sign-WindowsFile.ps1'
& $signScript -FilePath (Join-Path $publish 'DanmuApi.App.exe')
& (Join-Path $PSScriptRoot 'Compile-WindowsInstaller.ps1') -InnoCompiler $InnoCompiler -SourceDirectory $publish -OutputDirectory $output -Version $version -ArchName $archName
if(-not (Test-Path (Join-Path $output ('DanmuApi-'+$version+'-'+$Arch+'-setup.exe')))){throw 'Compiler returned without a completed installer'}
# The exact same closed final tree is the input to the portable archive and installer.
& (Join-Path $PSScriptRoot 'New-OutboundRuntimeBundle.ps1') -BaseBundle $publishedRuntime -Arch $archName -ValidateOnly
$files=Get-ChildItem $publish -File | Where-Object {$_.Extension -in '.exe','.dll'}
Compress-Archive -LiteralPath @($files.FullName + (Join-Path $publish 'runtime-bundle') + (Join-Path $publish 'git')) -DestinationPath (Join-Path $output ('DanmuApi-'+$version+'-'+$Arch+'-portable.zip'))
& (Join-Path $PSScriptRoot 'New-UpdateManifest.ps1') -ReleaseDirectory $output -SigningIdentity $SigningIdentity -Version $version -Architecture $Arch
Copy-Item (Join-Path (Split-Path $SigningIdentity -Parent) 'danmu-api-windows.cer') $output
Get-ChildItem $output -File | Where-Object {$_.Extension -in '.exe','.zip','.cer','.json','.sig'} | Get-FileHash -Algorithm SHA256 | ForEach-Object {$_.Hash+'  '+[IO.Path]::GetFileName($_.Path)} | Set-Content (Join-Path $output 'SHA256SUMS.txt') -Encoding ASCII
Write-Output ('Release files: '+$output)
