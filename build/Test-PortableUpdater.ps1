param([Parameter(Mandatory=$true)][string]$Dotnet,[Parameter(Mandatory=$true)][string]$PublishedExecutable,[Parameter(Mandatory=$true)][string]$SigningIdentity,[Parameter(Mandatory=$true)][string]$SignTool,[Parameter(Mandatory=$true)][string]$WorkDirectory)
$ErrorActionPreference='Stop'
if(Test-Path $WorkDirectory){throw 'Probe directory already exists'}
New-Item -ItemType Directory $WorkDirectory | Out-Null
$code=@'
using System;
using System.IO;
using System.Threading;
if(args.Length==2 && args[0]=="--app-update-receipt") { File.WriteAllText(Path.Combine(args[1],"startup.ok"),"0.1.4"); return; }
if(args.Length==2 && args[0]=="--wait") { File.WriteAllText(args[1]+".ready","ready"); while(!File.Exists(args[1])) Thread.Sleep(50); return; }
throw new Exception("Unknown probe mode");
'@
$project=@'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0-windows</TargetFramework><OutputType>Exe</OutputType><AssemblyName>DanmuApi.App</AssemblyName><PublishSingleFile>true</PublishSingleFile><SelfContained>true</SelfContained><RuntimeIdentifier>win-x64</RuntimeIdentifier></PropertyGroup></Project>
'@
[IO.File]::WriteAllText((Join-Path $WorkDirectory 'Program.cs'),$code)
[IO.File]::WriteAllText((Join-Path $WorkDirectory 'Probe.csproj'),$project)
$env:DANMU_SIGNTOOL=$SignTool; $env:DANMU_SIGNING_IDENTITY=$SigningIdentity
foreach($version in '0.1.2','0.1.4'){
  $publish=Join-Path $WorkDirectory $version
  & $Dotnet publish (Join-Path $WorkDirectory 'Probe.csproj') -c Release "-p:Version=$version" "-p:PublishDir=$publish\" | Out-Null
  if($LASTEXITCODE -ne 0){throw 'Probe publish failed'}
  & (Join-Path $PSScriptRoot 'Sign-WindowsFile.ps1') -FilePath (Join-Path $publish 'DanmuApi.App.exe')
}
$job=Join-Path $env:LOCALAPPDATA ('DanmuApi\app-updates\'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $job | Out-Null
$parent=$null;$helper=$null
try {
  $stage=Join-Path $WorkDirectory '0.1.4'
  New-Item -ItemType Directory (Join-Path $stage 'runtime-bundle') | Out-Null
  [IO.File]::WriteAllText((Join-Path $stage 'runtime-bundle\SHA256SUMS.txt'),'probe')
  Compress-Archive -LiteralPath @((Join-Path $stage 'DanmuApi.App.exe'),(Join-Path $stage 'runtime-bundle')) -DestinationPath (Join-Path $job 'probe-portable.zip')
  $asset=Get-Item (Join-Path $job 'probe-portable.zip')
  $manifest=[ordered]@{schemaVersion=1;product='DanmuApi.Windows';version='0.1.4';channel='preview';architecture='win-x64';assets=@([ordered]@{name=$asset.Name;size=$asset.Length;sha256=(Get-FileHash $asset.FullName).Hash;kind='portable'})}
  $bytes=[Text.Encoding]::UTF8.GetBytes(($manifest|ConvertTo-Json -Depth 8 -Compress))
  $identity=Get-Content -Raw $SigningIdentity|ConvertFrom-Json
  $cert=Get-Item ('Cert:\CurrentUser\My\'+$identity.Thumbprint)
  $rsa=[Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
  try{$signature=$rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)}finally{$rsa.Dispose()}
  [IO.File]::WriteAllBytes((Join-Path $job 'update-manifest.json'),$bytes)
  [IO.File]::WriteAllBytes((Join-Path $job 'update-manifest.json.sig'),$signature)
  $target=Join-Path $WorkDirectory '0.1.2'
  [IO.File]::WriteAllText((Join-Path $target 'user-note.txt'),'preserve')
  $exitFile=Join-Path $WorkDirectory 'exit-parent'
  $parent=Start-Process (Join-Path $target 'DanmuApi.App.exe') -ArgumentList '--wait',('"'+$exitFile+'"') -PassThru
  $watch=[Diagnostics.Stopwatch]::StartNew()
  while(-not(Test-Path ($exitFile+'.ready'))){if($watch.Elapsed.TotalSeconds -gt 10){throw 'Probe parent not ready'};Start-Sleep -Milliseconds 100}
  $task=[ordered]@{ParentPid=$parent.Id;ParentStartTicks=$parent.StartTime.ToUniversalTime().Ticks;TargetDirectory=$target;AssetName=$asset.Name;Kind='portable';ExpectedVersion='0.1.4'}
  [IO.File]::WriteAllText((Join-Path $job 'job.json'),($task|ConvertTo-Json))
  Copy-Item $PublishedExecutable (Join-Path $job 'DanmuApi.Updater.exe')
  $helper=Start-Process (Join-Path $job 'DanmuApi.Updater.exe') -ArgumentList '--apply-app-update',('"'+$job+'"') -PassThru
  $watch.Restart()
  while(-not(Test-Path (Join-Path $job 'helper.ready'))){if($helper.HasExited -or $watch.Elapsed.TotalSeconds -gt 30){throw ('Updater not ready: '+(Get-Content (Join-Path $job 'error.txt') -ErrorAction SilentlyContinue))};Start-Sleep -Milliseconds 100}
  if((Get-Item (Join-Path $target 'DanmuApi.App.exe')).VersionInfo.ProductVersion -ne '0.1.2'){throw 'Replaced before parent exited'}
  [IO.File]::WriteAllText($exitFile,'exit')
  if(-not $helper.WaitForExit(45000)){throw 'Updater timed out'}
  if($helper.ExitCode -ne 0){throw ('Updater failed: '+(Get-Content (Join-Path $job 'error.txt')))}
  if((Get-Item (Join-Path $target 'DanmuApi.App.exe')).VersionInfo.ProductVersion -ne '0.1.4'){throw 'Version not replaced'}
  if((Get-Content (Join-Path $target 'user-note.txt')) -ne 'preserve'){throw 'User file changed'}
  if(-not(Test-Path (Join-Path $job 'startup.ok')) -or (Test-Path (Join-Path $job 'backup'))){throw 'Startup receipt/backup cleanup failed'}
  [IO.File]::WriteAllText((Join-Path $WorkDirectory 'result.txt'),'PASS: final signed helper verified manifest/package, waited for old process, replaced files, restarted and received startup receipt; user file preserved.')
  Write-Output 'Portable update end-to-end PASS'
}finally{
  if($parent -and -not $parent.HasExited){[IO.File]::WriteAllText((Join-Path $WorkDirectory 'exit-parent'),'exit');$parent.WaitForExit(5000)|Out-Null}
  if($helper -and -not $helper.HasExited){[IO.File]::WriteAllText((Join-Path $job 'cancel'),'cancel')}
}
