param([Parameter(Mandatory=$true)][string]$LegacyZip,[Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference='Stop'
# Published GitHub asset digest, lilixu3/danmu-api-android / windows-desktop-v0.1.0-preview.1.
$expected='F9B5C73194067F30B59839D4C85F7A51740CB049D592D58A9E6DA70F06CBF20D'
if((Get-FileHash -LiteralPath $LegacyZip -Algorithm SHA256).Hash -ne $expected){throw 'Published Kotlin archive SHA256 mismatch'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$outer=[IO.Compression.ZipFile]::OpenRead($LegacyZip)
try {
  $jars=@($outer.Entries | Where-Object FullName -match '/app/desktop-0\.1\.0-.*\.jar$')
  if($jars.Count -ne 1){throw 'Expected exactly one Kotlin resource JAR'}
  $memory=New-Object IO.MemoryStream
  $input=$jars[0].Open(); try {$input.CopyTo($memory)} finally {$input.Dispose()}
  $memory.Position=0
  $jar=New-Object IO.Compression.ZipArchive($memory,[IO.Compression.ZipArchiveMode]::Read)
  try {
    $reader=New-Object IO.StreamReader($jar.GetEntry('runtime-manifest.txt').Open())
    try {$names=$reader.ReadToEnd().Split([char]10) | ForEach-Object {$_.Trim()} | Where-Object {$_}} finally {$reader.Dispose()}
    $lines=foreach($name in $names){
      if($name -in @('runtime/LICENSE','runtime/nodejs-project/config/.env')){continue}
      if(-not $name.StartsWith('runtime/') -or $name.Contains('..')){throw 'Invalid Kotlin manifest path'}
      $entry=$jar.GetEntry($name); if(-not $entry){throw "Missing Kotlin payload: $name"}
      $stream=$entry.Open(); $sha=[Security.Cryptography.SHA256]::Create()
      try {$hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')} finally {$stream.Dispose();$sha.Dispose()}
      $hash+'  '+$name.Substring(8)
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Destination))) | Out-Null
    [IO.File]::WriteAllLines([IO.Path]::GetFullPath($Destination),[string[]]$lines,[Text.Encoding]::ASCII)
    Write-Output ('Verified published Kotlin catalog files: '+$lines.Count+'; artifact SHA256='+$expected)
  } finally {$jar.Dispose();$memory.Dispose()}
} finally {$outer.Dispose()}
