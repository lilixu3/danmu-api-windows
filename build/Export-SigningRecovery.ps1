param([Parameter(Mandatory=$true)][string]$SecretDirectory)
$ErrorActionPreference='Stop'
$target=Join-Path $SecretDirectory 'OFFLINE-RECOVERY-PASSWORD.txt'
if(Test-Path $target){throw 'Recovery password file already exists; refusing overwrite'}
$secure=Get-Content -LiteralPath (Join-Path $SecretDirectory 'pfx-password.dpapi') | ConvertTo-SecureString
$pointer=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $value=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    [IO.File]::WriteAllText($target,$value,[Text.Encoding]::UTF8)
    $value=$null
} finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
Write-Output 'Offline recovery material created inside restricted signing directory. Store separately from the PFX; never include it in release files.'
