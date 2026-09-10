param([Parameter(Mandatory=$true)][string]$SecretDirectory)
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($SecretDirectory)
if (Test-Path $directory) { throw 'Signing directory already exists; never replace an established signing identity.' }
New-Item -ItemType Directory -Path $directory | Out-Null
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$acl = Get-Acl $directory
$acl.SetAccessRuleProtection($true, $false)
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule('SYSTEM','FullControl','ContainerInherit,ObjectInherit','None','Allow')))
Set-Acl -LiteralPath $directory -AclObject $acl
$certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Danmu API' -FriendlyName 'Danmu API Windows signing' -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5)
$random = New-Object byte[] 48
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($random) } finally { $rng.Dispose() }
$password = ConvertTo-SecureString ([Convert]::ToBase64String($random)) -AsPlainText -Force
[Array]::Clear($random,0,$random.Length)
Export-PfxCertificate -Cert $certificate -FilePath (Join-Path $directory 'danmu-api-windows.pfx') -Password $password | Out-Null
$password | ConvertFrom-SecureString | Set-Content -LiteralPath (Join-Path $directory 'pfx-password.dpapi') -Encoding ASCII
Export-Certificate -Cert $certificate -FilePath (Join-Path $directory 'danmu-api-windows.cer') | Out-Null
[ordered]@{Subject=$certificate.Subject;Thumbprint=$certificate.Thumbprint;SerialNumber=$certificate.SerialNumber;NotBefore=$certificate.NotBefore.ToString('o');NotAfter=$certificate.NotAfter.ToString('o');Store='CurrentUser/My';Algorithm='RSA3072/SHA256';Trust='Self-signed; not installed in Root or TrustedPublisher'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'identity.json') -Encoding UTF8
Write-Output ('Created Windows signing identity: ' + $certificate.Thumbprint)
