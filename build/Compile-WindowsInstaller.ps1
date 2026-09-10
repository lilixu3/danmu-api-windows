param([Parameter(Mandatory=$true)][string]$InnoCompiler,[Parameter(Mandatory=$true)][string]$SourceDirectory,[Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$Version='0.1.1')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$script=Join-Path $PSScriptRoot 'Sign-WindowsFile.ps1'
$sign='powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $q'+$script+'$q -FilePath $f'
$args=@('/DSourceDir="'+$SourceDirectory+'"','/DOutputRoot="'+$OutputDirectory+'"','/DAppVersion='+$Version,'/SDanmuSign="'+$sign+'"','"'+(Join-Path $root 'installer\DanmuApi.iss')+'"')
$p=Start-Process -FilePath $InnoCompiler -ArgumentList $args -Wait -PassThru -NoNewWindow
if($p.ExitCode -ne 0){throw ('Inno failed with exit '+$p.ExitCode)}
if(-not(Test-Path (Join-Path $OutputDirectory ('DanmuApi-'+$Version+'-win-x64-setup.exe')))){throw 'No completed setup EXE'}
