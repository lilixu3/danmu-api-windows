[CmdletBinding()]
param(
    [string]$ToolsRoot = (Join-Path $env:SystemDrive 'Tools'),
    [string]$DownloadRoot = (Join-Path $env:SystemDrive 'Tools\_downloads')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Full installed tree is proved against the pinned official archive, not a local receipt.
$version = 'go1.26.0'
$filename = 'go1.26.0.windows-amd64.zip'
$officialSha256 = '9bbe0fc64236b2b51f6255c05c4232532b8ecc0e6d2e00950bd3021d8a4d07d4'
$officialSize = 74815266
$url = 'https://go.dev/dl/' + $filename
$installRoot = [IO.Path]::GetFullPath((Join-Path $ToolsRoot $version)).TrimEnd('\')
$goExe = Join-Path $installRoot 'bin\go.exe'
$manifestPath = Join-Path $installRoot 'toolchain.json'
$archive = Join-Path $DownloadRoot $filename
$utf8 = New-Object Text.UTF8Encoding($false)
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-NoReparse([string]$path) {
    $current = [IO.Path]::GetFullPath($path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Go provenance forbids reparse points: $current" }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
function Hash-Stream($stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
}
function Hash-Text([string]$text) {
    $stream = New-Object IO.MemoryStream(,$utf8.GetBytes($text))
    try { return Hash-Stream $stream } finally { $stream.Dispose() }
}
function Assert-Archive {
    Assert-NoReparse $archive
    if ((Get-Item -LiteralPath $archive -Force).Length -ne $officialSize) { throw 'Go archive size does not match official metadata.' }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $officialSha256) { throw 'Go archive SHA256 does not match official metadata.' }
}
# Even an existing install requires the archive. A driver-only local hash cannot prove
# compiler, linker, runtime or standard library provenance.
New-Item -ItemType Directory -Force -Path $ToolsRoot,$DownloadRoot | Out-Null
if (!(Test-Path -LiteralPath $archive)) {
    $partial = $archive + '.partial'
    Assert-NoReparse $partial
    Write-Host "Downloading $filename from the official Go server"
    & curl.exe --fail --silent --show-error --location --connect-timeout 20 --max-time 600 $url --output $partial
    if ($LASTEXITCODE -ne 0) { throw "Official Go download failed (curl exit $LASTEXITCODE); partial file retained for diagnosis." }
    Move-Item -LiteralPath $partial -Destination $archive
}
Assert-Archive
Assert-NoReparse $installRoot
if (!(Test-Path -LiteralPath $installRoot)) {
    $stage = Join-Path $ToolsRoot ($version + '-extract-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    try {
        Expand-Archive -LiteralPath $archive -DestinationPath $stage
        $stageGo = Join-Path $stage 'go'
        $receipt = [ordered]@{ version=$version; url=$url; archive=$filename; archiveSha256=$officialSha256; archiveBytes=$officialSize; goExeSha256=(Get-FileHash (Join-Path $stageGo 'bin\go.exe') -Algorithm SHA256).Hash.ToLowerInvariant() }
        [IO.File]::WriteAllText((Join-Path $stageGo 'toolchain.json'), ($receipt | ConvertTo-Json), $utf8)
        Move-Item -LiteralPath $stageGo -Destination $installRoot
    } finally { Remove-Item -LiteralPath $stage -Recurse -Force }
}
if (!(Test-Path -LiteralPath $goExe) -or !(Test-Path -LiteralPath $manifestPath)) { throw 'Existing fixed Go toolchain is incomplete; inspect it before installing.' }
Assert-NoReparse $manifestPath
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.version -cne $version -or $manifest.archiveSha256 -cne $officialSha256 -or $manifest.url -cne $url -or $manifest.archiveBytes -ne $officialSize) { throw 'Existing Go toolchain provenance differs from the pinned official input.' }
$hashes = New-Object 'Collections.Generic.Dictionary[string,string]' ([StringComparer]::Ordinal)
$directories = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($entry in $zip.Entries) {
        if (!$entry.FullName.StartsWith('go/', [StringComparison]::Ordinal)) { throw 'Official Go archive root mismatch.' }
        $name = $entry.FullName.Substring(3)
        if (!$name) { continue }
        $name = $name.TrimEnd('/')
        if ($name.Contains('\') -or $name.Contains(':') -or $name -match '(^|/)\.\.?(/|$)') { throw 'Unsafe official Go archive path.' }
        $parts = $name.Split('/')
        for ($i=1; $i -lt $parts.Length; $i++) { $null = $directories.Add(($parts[0..($i-1)] -join '/')) }
        if ($entry.FullName.EndsWith('/')) { $null = $directories.Add($name); continue }
        $stream = $entry.Open()
        try { $hash = Hash-Stream $stream } finally { $stream.Dispose() }
        if ($hashes.ContainsKey($name)) { throw 'Duplicate official Go archive file.' }
        $hashes.Add($name,$hash)
        $path = Join-Path $installRoot $name
        Assert-NoReparse $path
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash) { throw "Installed Go archive input checksum mismatch: $name" }
    }
} finally { $zip.Dispose() }
# Force includes hidden files. Inspect every directory before descent (never follow a junction).
$pending = New-Object 'Collections.Generic.Queue[string]'
$pending.Enqueue($installRoot)
while ($pending.Count) {
    foreach ($item in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Go provenance forbids reparse points: $($item.FullName)" }
        $relative = $item.FullName.Substring($installRoot.Length+1).Replace('\','/')
        if ($item.PSIsContainer) {
            if (!$directories.Contains($relative)) { throw "Unlisted installed Go directory: $relative" }
            $pending.Enqueue($item.FullName)
        } elseif (!$hashes.ContainsKey($relative) -and $relative -cne 'toolchain.json') { throw "Unlisted installed Go input: $relative" }
    }
}
if ($hashes['bin/go.exe'] -cne $manifest.goExeSha256) { throw 'Installed Go receipt driver checksum mismatch.' }
$names = [string[]]@($hashes.Keys); [Array]::Sort($names,[StringComparer]::Ordinal)
$canonical = [string]::Concat(@($names | ForEach-Object { $hashes[$_] + '  ' + $_ + "`n" }))
$standardNames = @($names | Where-Object { $_ -cmatch '^(src|pkg/include|lib)/' })
$standardCanonical = [string]::Concat(@($standardNames | ForEach-Object { $hashes[$_] + '  ' + $_ + "`n" }))
$variables = @('GOROOT','GOENV','GOTOOLCHAIN','GOFLAGS','GOWORK','GOOS','GOARCH')
$previous = @{}; foreach ($name in $variables) { $previous[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }
try {
    $env:GOROOT=$installRoot; $env:GOENV='off'; $env:GOTOOLCHAIN='local'; $env:GOFLAGS=''; $env:GOWORK='off'; $env:GOOS='windows'; $env:GOARCH='amd64'
    $actual = & $goExe version
    if ($LASTEXITCODE -ne 0 -or $actual -cne 'go version go1.26.0 windows/amd64') { throw "Unexpected fixed toolchain version: $actual" }
    $selected = (& $goExe env -json GOROOT GOTOOLDIR GOENV | Out-String) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or !([IO.Path]::GetFullPath($selected.GOROOT).TrimEnd('\').Equals($installRoot,[StringComparison]::OrdinalIgnoreCase)) -or
        !([IO.Path]::GetFullPath($selected.GOTOOLDIR).Equals((Join-Path $installRoot 'pkg\tool\windows_amd64'),[StringComparison]::OrdinalIgnoreCase)) -or $selected.GOENV -cne '' -or $env:GOENV -cne 'off') { throw 'Go selected root/tool directory/config differs from the pinned official tree.' }
} finally { foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name,$previous[$name],'Process') } }
Write-Host "$actual; official archive SHA256=$officialSha256; full tree verified ($($hashes.Count) inputs)"
[pscustomobject]@{ GoExe=$goExe; Root=$installRoot; Version=$version; ArchiveSha256=$officialSha256; ArchiveUrl=$url; ArchiveBytes=$officialSize; ManifestPath=$manifestPath;
    InputCount=$hashes.Count; InputSha256=(Hash-Text $canonical); GoExeSha256=$hashes['bin/go.exe']; CompilerSha256=$hashes['pkg/tool/windows_amd64/compile.exe']; LinkerSha256=$hashes['pkg/tool/windows_amd64/link.exe']; StandardLibrarySha256=(Hash-Text $standardCanonical) }
