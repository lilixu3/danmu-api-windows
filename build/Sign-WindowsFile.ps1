param([Parameter(Mandatory=$true)][string]$FilePath)
$ErrorActionPreference='Stop'
foreach($name in 'DANMU_SIGNTOOL','DANMU_SIGNING_IDENTITY') { if(-not [Environment]::GetEnvironmentVariable($name)){throw "Missing $name"} }
$tool=$env:DANMU_SIGNTOOL
$identity=Get-Content -Raw -LiteralPath $env:DANMU_SIGNING_IDENTITY | ConvertFrom-Json
$cert=Get-Item ('Cert:\CurrentUser\My\'+$identity.Thumbprint)
if(-not $cert.HasPrivateKey -or $cert.NotAfter -lt (Get-Date)){throw 'Signing key unavailable or expired'}
& $tool sign /sha1 $identity.Thumbprint /s My /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $FilePath
if($LASTEXITCODE -ne 0){throw "Signing/timestamp failed: $LASTEXITCODE"}
$signature=Get-AuthenticodeSignature -LiteralPath $FilePath
if($signature.SignerCertificate.Thumbprint -ne $identity.Thumbprint -or -not $signature.TimeStamperCertificate){throw 'Signer or timestamp verification failed'}
if($signature.Status -ne 'Valid') {
    $chain = New-Object Security.Cryptography.X509Certificates.X509Chain
    try {
        $chain.ChainPolicy.RevocationMode = 'NoCheck'
        $null = $chain.Build($signature.SignerCertificate)
        $other = @($chain.ChainStatus | Where-Object {$_.Status -ne 'UntrustedRoot'})
        if($signature.Status -notin 'UnknownError','NotTrusted' -or $other.Count -ne 0 -or $chain.ChainStatus.Count -eq 0){throw ('Signature verification failed: '+$signature.StatusMessage)}
        if($signature.StatusMessage -notmatch 'root.*not trusted|不受信任提供程序信任的根证书'){throw ('Unexpected verification failure: '+$signature.StatusMessage)}
    } finally { $chain.Dispose() }
}
Write-Output ('Signed and timestamped: '+[IO.Path]::GetFileName($FilePath)+'; trust='+$signature.Status)
