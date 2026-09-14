param([Parameter(Mandatory=$true)][string]$ReleaseDirectory,[Parameter(Mandatory=$true)][string]$SigningIdentity,[Parameter(Mandatory=$true)][string]$Version,[ValidateSet('win-x64','win-x86','win-arm64')][string]$Architecture='win-x64')
$ErrorActionPreference='Stop'
$assets=@()
foreach($kind in 'installer','portable'){
  $name=if($kind -eq 'installer'){"DanmuApi-$Version-$Architecture-setup.exe"}else{"DanmuApi-$Version-$Architecture-portable.zip"}
  $file=Get-Item -LiteralPath (Join-Path $ReleaseDirectory $name)
  $assets += [ordered]@{name=$name;size=$file.Length;sha256=(Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant();kind=$kind}
}
$manifest=[ordered]@{schemaVersion=1;product='DanmuApi.Windows';version=$Version;channel='preview';architecture=$Architecture;assets=$assets}
$bytes=[Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 8 -Compress))
$identity=Get-Content -Raw -LiteralPath $SigningIdentity | ConvertFrom-Json
$certificate=Get-Item ('Cert:\CurrentUser\My\'+$identity.Thumbprint)
$key=[Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
try{$signature=$key.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)}finally{$key.Dispose()}
# x64 keeps the original unsuffixed names so clients released before multi-architecture support keep
# finding them; the other architectures get a suffixed name in the same release.
$stem=if($Architecture -eq 'win-x64'){'update-manifest.json'}else{('update-manifest-'+$Architecture+'.json')}
[IO.File]::WriteAllBytes((Join-Path $ReleaseDirectory $stem),$bytes)
[IO.File]::WriteAllBytes((Join-Path $ReleaseDirectory ($stem+'.sig')),$signature)
Write-Output ('Signed update manifest created: '+$stem)
