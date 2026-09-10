param([Parameter(Mandatory=$true)][string]$Dotnet,[Parameter(Mandatory=$true)][string]$InnoCompiler,[Parameter(Mandatory=$true)][string]$SignTool,[Parameter(Mandatory=$true)][string]$SigningIdentity,[string]$OutputDirectory,[Parameter(Mandatory=$true)][string]$RuntimeBundle)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
[xml]$props=Get-Content (Join-Path $root 'Directory.Build.props')
$version=[string]$props.Project.PropertyGroup.Version
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root ('artifacts\signed-'+$version)}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path $output){throw 'Release output exists; use a fresh directory to preserve earlier artifacts.'}
New-Item -ItemType Directory $output | Out-Null
$publish=Join-Path $output 'publish'
$env:DANMU_SIGNTOOL=[IO.Path]::GetFullPath($SignTool)
$env:DANMU_SIGNING_IDENTITY=[IO.Path]::GetFullPath($SigningIdentity)
$restoreArgs=@(); if($env:DANMU_SKIP_RESTORE -eq '1'){$restoreArgs=@('--no-restore')}
& $Dotnet publish (Join-Path $root 'src\DanmuApi.App\DanmuApi.App.csproj') -c Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:BuiltInComInteropSupport=true -p:PublishReadyToRun=false "-p:PublishDir=$publish\" @restoreArgs
if($LASTEXITCODE -ne 0){throw 'Publish failed'}
if(-not(Test-Path (Join-Path $RuntimeBundle 'node.exe')) -or -not(Test-Path (Join-Path $RuntimeBundle 'SHA256SUMS.txt'))){throw 'Required runtime bundle is incomplete'}
Copy-Item -LiteralPath $RuntimeBundle -Destination (Join-Path $publish 'runtime-bundle') -Recurse
& (Join-Path $PSScriptRoot 'New-RuntimeBuildIdentity.ps1') -RuntimeBundle (Join-Path $publish 'runtime-bundle')
$signScript=Join-Path $PSScriptRoot 'Sign-WindowsFile.ps1'
& $signScript -FilePath (Join-Path $publish 'DanmuApi.App.exe')
& (Join-Path $PSScriptRoot 'Compile-WindowsInstaller.ps1') -InnoCompiler $InnoCompiler -SourceDirectory $publish -OutputDirectory $output -Version $version
if(-not (Test-Path (Join-Path $output ('DanmuApi-'+$version+'-win-x64-setup.exe')))){throw 'Compiler returned without a completed installer'}
$files=Get-ChildItem $publish -File | Where-Object {$_.Extension -in '.exe','.dll'}
Compress-Archive -LiteralPath @($files.FullName + (Join-Path $publish 'runtime-bundle')) -DestinationPath (Join-Path $output ('DanmuApi-'+$version+'-win-x64-portable.zip'))
& (Join-Path $PSScriptRoot 'New-UpdateManifest.ps1') -ReleaseDirectory $output -SigningIdentity $SigningIdentity -Version $version
Copy-Item (Join-Path (Split-Path $SigningIdentity -Parent) 'danmu-api-windows.cer') $output
Get-ChildItem $output -File | Where-Object {$_.Extension -in '.exe','.zip','.cer','.json','.sig'} | Get-FileHash -Algorithm SHA256 | ForEach-Object {$_.Hash+'  '+[IO.Path]::GetFileName($_.Path)} | Set-Content (Join-Path $output 'SHA256SUMS.txt') -Encoding ASCII
Write-Output ('Release files: '+$output)
