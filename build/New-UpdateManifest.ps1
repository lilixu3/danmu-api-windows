param([Parameter(Mandatory=$true)][string]$ReleaseDirectory,[Parameter(Mandatory=$true)][string]$SigningIdentity,[Parameter(Mandatory=$true)][string]$Version)
$ErrorActionPreference='Stop'
$assets=@()
foreach($kind in 'installer','portable'){
  $name=if($kind -eq 'installer'){"DanmuApi-$Version-win-x64-setup.exe"}else{"DanmuApi-$Version-win-x64-portable.zip"}
  $file=Get-Item -LiteralPath (Join-Path $ReleaseDirectory $name)
  $assets += [ordered]@{name=$name;size=$file.Length;sha256=(Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant();kind=$kind}
}
$manifest=[ordered]@{schemaVersion=1;product='DanmuApi.Windows';version=$Version;channel='preview';architecture='win-x64';assets=$assets}
$bytes=[Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 8 -Compress))
$identity=Get-Content -Raw -LiteralPath $SigningIdentity | ConvertFrom-Json
$certificate=Get-Item ('Cert:\CurrentUser\My\'+$identity.Thumbprint)
$key=[Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
try{$signature=$key.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)}finally{$key.Dispose()}
[IO.File]::WriteAllBytes((Join-Path $ReleaseDirectory 'update-manifest.json'),$bytes)
[IO.File]::WriteAllBytes((Join-Path $ReleaseDirectory 'update-manifest.json.sig'),$signature)
Write-Output 'Signed update manifest created.'
