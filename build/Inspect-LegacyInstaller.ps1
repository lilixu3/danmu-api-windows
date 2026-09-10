param([Parameter(Mandatory=$true)][string]$Installer,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if((Get-FileHash $Installer -Algorithm SHA256).Hash -ne 'CF1F4966C5A7997BAAC5ACE10B62BA1E0980A5056C1BF7D7D742ED79F1A7CB70'){throw 'Legacy installer hash mismatch'}
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$bytes=[IO.File]::ReadAllBytes($Installer)
$offset=566804
if([BitConverter]::ToString($bytes,$offset,8) -ne 'D0-CF-11-E0-A1-B1-1A-E1'){throw 'MSI payload signature mismatch'}
$msi=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'legacy-preview.msi'
$payload=New-Object byte[] ($bytes.Length-$offset)
[Array]::Copy($bytes,$offset,$payload,0,$payload.Length)
[IO.File]::WriteAllBytes($msi,$payload)
$engine=New-Object -ComObject WindowsInstaller.Installer
$db=$engine.OpenDatabase($msi,0)
$view=$db.OpenView('SELECT `Property`, `Value` FROM `Property`')
$view.Execute()
while($row=$view.Fetch()) { if($row.StringData(1) -in 'ProductCode','UpgradeCode','ProductVersion','ProductName','Manufacturer','ALLUSERS'){ Write-Output ($row.StringData(1)+'='+$row.StringData(2)) } }
$view.Close()
