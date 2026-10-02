[CmdletBinding()]
param(
    [ValidateSet('win-x64','win-x86','win-arm64')][string[]]$RuntimeIdentifier = @('win-x64','win-x86','win-arm64'),
    [string]$ToolsRoot = (Join-Path $env:SystemDrive 'Tools'),
    [string]$ScratchRoot,
    [string]$OutputRoot,
    [switch]$DownloadDependencies,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path $PSScriptRoot -Parent
if (!$OutputRoot) { $OutputRoot = Join-Path $projectRoot 'artifacts\outbound' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (@($RuntimeIdentifier | Select-Object -Unique).Count -ne $RuntimeIdentifier.Count) { throw 'Duplicate output runtime identifiers.' }
# Reject all prior RID outputs before toolchain preparation or writing any build outputs.
foreach ($rid in $RuntimeIdentifier) {
    if (Test-Path -LiteralPath (Join-Path $OutputRoot $rid)) { throw "Outbound output exists; use a fresh OutputRoot to preserve prior artifacts: $rid" }
}
if (!$ScratchRoot) { $ScratchRoot = Join-Path $projectRoot '_local-scratch\outbound-build' }
New-Item -ItemType Directory -Force -Path $ScratchRoot | Out-Null
$logPath = Join-Path $ScratchRoot 'build.log'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$helperVersion = '1.0.1+6004732'
$sourceCommit = '600473250a07d0f78502262d141e0f3faf4a9a36'
$sourceRoot = Join-Path $projectRoot 'runtime\outbound'
$moduleRoot = Join-Path $sourceRoot 'src'
$tool = & (Join-Path $PSScriptRoot 'Get-GoToolchain.ps1') -ToolsRoot $ToolsRoot
$go = $tool.GoExe
$modHash = (Get-FileHash (Join-Path $moduleRoot 'go.mod') -Algorithm SHA256).Hash
$sumHash = (Get-FileHash (Join-Path $moduleRoot 'go.sum') -Algorithm SHA256).Hash

function Invoke-Go([string[]]$GoArgs) {
    if ($env:GOROOT -cne $tool.Root -or $env:GOENV -cne 'off' -or $env:GOTOOLCHAIN -cne 'local') { throw 'Go environment no longer selects the verified pinned tree.' }
    $command = 'go ' + ($GoArgs -join ' ')
    Write-Host $command
    [IO.File]::AppendAllText($logPath, $command + "`n", $utf8)
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = @(& $go @GoArgs 2>&1); $code = $LASTEXITCODE } finally { $ErrorActionPreference = $oldPreference }
    foreach ($line in $output) { [IO.File]::AppendAllText($logPath, [string]$line + "`n", $utf8) }
    [IO.File]::AppendAllText($logPath, 'exitCode=' + $code + "`n", $utf8)
    if ($code -ne 0) { foreach ($line in $output) { Write-Host $line }; throw "$command failed (exit $code); see build.log." }
    return $output
}
function Hash-Text([string]$Text) {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hasher.ComputeHash($utf8.GetBytes($Text)))).Replace('-','').ToLowerInvariant() } finally { $hasher.Dispose() }
}
function PE-Machine([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) { throw 'Missing MZ header.' }
    $pe = [BitConverter]::ToInt32($bytes,60)
    if ($pe -lt 64 -or $pe + 24 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes,$pe) -ne 0x4550) { throw 'Invalid PE header.' }
    return [int][BitConverter]::ToUInt16($bytes,$pe+4)
}
$variables = @('GOROOT','GOENV','GOTOOLCHAIN','GOFLAGS','CGO_ENABLED','GOOS','GOARCH','GOMODCACHE','GOCACHE','GOPROXY','GOSUMDB','GOAMD64','GO386','GOARM64','GOWORK')
$prior = @{}
foreach ($name in $variables) { $prior[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }
Push-Location $moduleRoot
try {
    $env:GOROOT=$tool.Root; $env:GOENV='off'
    $env:GOTOOLCHAIN='local'; $env:GOFLAGS='-mod=readonly'; $env:CGO_ENABLED='0'; $env:GOOS='windows'; $env:GOARCH='amd64'
    $selected = (Invoke-Go @('env','-json','GOROOT','GOTOOLDIR','GOENV') | Out-String) | ConvertFrom-Json
    if (!([IO.Path]::GetFullPath($selected.GOROOT).TrimEnd('\').Equals($tool.Root,[StringComparison]::OrdinalIgnoreCase)) -or
        !([IO.Path]::GetFullPath($selected.GOTOOLDIR).Equals((Join-Path $tool.Root 'pkg\tool\windows_amd64'),[StringComparison]::OrdinalIgnoreCase)) -or $selected.GOENV -cne '' -or $env:GOENV -cne 'off') { throw 'Build selected an unverified Go root/tool directory/config.' }
    $env:GOWORK='off'; $env:GOAMD64='v1'; $env:GO386='sse2'; $env:GOARM64='v8.0'
    $env:GOMODCACHE = Join-Path $projectRoot '_local-scratch\outbound-go-mod'
    $env:GOCACHE = Join-Path $projectRoot '_local-scratch\outbound-go-cache'
    if ($DownloadDependencies) {
        # Explicit preparation phase. No alternative mirrors and no dependency updates.
        $env:GOPROXY='https://proxy.golang.org'; $env:GOSUMDB='sum.golang.org'
        Invoke-Go @('mod','download') | Out-Null
    }
    $env:GOPROXY='off'; $env:GOSUMDB='off'
    Invoke-Go @('mod','verify') | ForEach-Object { Write-Host $_ }
    # Listing "all" traverses dependency test graphs outside this production
    # lockfile. Enumerate the exact modules pinned in our go.mod instead.
    $moduleDescription = (Invoke-Go @('mod','edit','-json') | Out-String) | ConvertFrom-Json
    if ($moduleDescription.Replace -or $moduleDescription.Exclude) { throw 'Module replacements/exclusions need an explicit provenance policy.' }
    $modulePaths = @($moduleDescription.Require | ForEach-Object { $_.Path })
    $dependencyLines = Invoke-Go (@('list','-m','-f','{{.Path}}|{{.Version}}|{{.Sum}}|{{.GoModSum}}|{{.Dir}}') + $modulePaths)
    $dependencies = @(); $licenseFiles = @()
    foreach ($line in $dependencyLines) {
        $parts = ([string]$line).Split('|')
        if ($parts[0] -eq 'danmu-api/outbound') { continue }
        if ($parts.Length -ne 5 -or !$parts[1] -or !$parts[2] -or !$parts[3]) { throw 'Incomplete locked dependency metadata.' }
        $dependencies += [ordered]@{path=$parts[0]; version=$parts[1]; sum=$parts[2]; goModSum=$parts[3]}
        $licenses = @(Get-ChildItem -LiteralPath $parts[4] -File | Where-Object { $_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE|PATENTS)' })
        if ($licenses.Count -eq 0) { throw ('Missing license for ' + $parts[0]) }
        foreach ($license in $licenses) { $licenseFiles += [pscustomobject]@{file=$license.FullName; path=('licenses/' + $parts[0] + '/' + $parts[1] + '/' + $license.Name)} }
    }
    foreach ($name in @('LICENSE','PATENTS')) {
        $file = Join-Path $tool.Root $name
        if (Test-Path $file) { $licenseFiles += [pscustomobject]@{file=$file; path=('licenses/go/' + $tool.Version + '/' + $name)} }
    }
    if (!$SkipTests) {
        Invoke-Go @('test','-count=1','-v','./...') | ForEach-Object { Write-Host $_ }
        Invoke-Go @('vet','./...') | Out-Null
    }
    $inputPaths = New-Object 'System.Collections.Generic.List[string]'
    foreach ($file in Get-ChildItem -LiteralPath $moduleRoot -File -Recurse) {
        if ($file.Extension -eq '.go' -or $file.Name -eq 'go.mod' -or $file.Name -eq 'go.sum') { $inputPaths.Add($file.FullName.Substring($projectRoot.Length+1).Replace('\','/')) }
    }
    foreach ($path in @('runtime/outbound/LICENSE','runtime/outbound/UPSTREAM','build/Build-OutboundHelper.ps1','build/Get-GoToolchain.ps1')) { $inputPaths.Add($path) }
    $inputPaths.Sort([StringComparer]::Ordinal)
    $sourceInputs = @(); $canonical = ''
    foreach ($path in $inputPaths) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $projectRoot $path) -Algorithm SHA256).Hash.ToLowerInvariant()
        $sourceInputs += [ordered]@{path=$path; sha256=$hash}
        $canonical += $hash + '  ' + $path + "`n"
    }
    $sourceSha256 = Hash-Text $canonical
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($rid in $RuntimeIdentifier) {
        $architecture = @{ 'win-x64'='amd64'; 'win-x86'='386'; 'win-arm64'='arm64' }[$rid]
        $machine = @{ 'win-x64'=34404; 'win-x86'=332; 'win-arm64'=43620 }[$rid]
        $env:GOARCH = $architecture
        $package = (Invoke-Go @('list','-json','.') | Out-String) | ConvertFrom-Json
        $productionSourceInputs = @()
        foreach ($file in $package.GoFiles) {
            $path = 'runtime/outbound/src/' + $file
            $record = @($sourceInputs | Where-Object { $_.path -ceq $path })
            if ($record.Count -ne 1 -or $file.EndsWith('_test.go',[StringComparison]::Ordinal)) { throw "Compiled source is not recorded as a production input: $path" }
            $productionSourceInputs += $record[0]
        }
        if ($productionSourceInputs.Count -eq 0) { throw 'No verified production Go sources selected.' }
        $ridOutput = Join-Path $OutputRoot $rid
        # Atomic directory creation also refuses a directory appearing after the preflight.
        New-Item -ItemType Directory -Path $ridOutput -ErrorAction Stop | Out-Null
        $exe = Join-Path $ridOutput 'danmu-outbound.exe'
        Invoke-Go @('build','-trimpath','-buildvcs=false','-ldflags',('-s -w -X main.buildVersion=' + $helperVersion),'-o',$exe,'.') | Out-Null
        foreach ($input in $sourceInputs) {
            if ((Get-FileHash -LiteralPath (Join-Path $projectRoot $input.path) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $input.sha256) { throw "Recorded source changed during native build: $($input.path)" }
        }
        $actualMachine = PE-Machine $exe
        if ($actualMachine -ne $machine) { throw "PE machine mismatch for $rid ($actualMachine)." }
        $exeHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
        $buildInfo = Invoke-Go @('version','-m',$exe)
        [IO.File]::WriteAllText((Join-Path $ScratchRoot ($rid + '-go-buildinfo.log')),($buildInfo -join "`n") + "`n",$utf8)
        Copy-Item -LiteralPath (Join-Path $sourceRoot 'LICENSE') -Destination (Join-Path $ridOutput 'OUTBOUND-LICENSE.txt')
        Copy-Item -LiteralPath (Join-Path $sourceRoot 'UPSTREAM') -Destination (Join-Path $ridOutput 'OUTBOUND-UPSTREAM.txt')
        $zipPath = Join-Path $ridOutput 'outbound-source.zip'
        if (Test-Path -LiteralPath $zipPath) { throw 'Source archive already exists in the fresh output; refusing overwrite.' }
        $zip = [IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($path in $inputPaths) { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $projectRoot $path),$path,[IO.Compression.CompressionLevel]::Optimal) | Out-Null }
            foreach ($license in $licenseFiles) { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$license.file,$license.path,[IO.Compression.CompressionLevel]::Optimal) | Out-Null }
        } finally { $zip.Dispose() }
        $notice = "App-owned outbound helper $helperVersion`nAGPL-3.0: corresponding source in outbound-source.zip, original baseline in OUTBOUND-UPSTREAM.txt.`nGo $($tool.Version): BSD-style license in source archive.`nLocked dependency licenses are in source archive licenses/<module>/<version>/.`n"
        foreach ($dependency in $dependencies) { $notice += $dependency.path + ' ' + $dependency.version + "`n" }
        $notice += "Default helper ECH-rejection retry: refresh DNS once and retry only the handshake (target and encrypted DoH). Go 1.26 crypto/tls returns ECHRejectionError; this helper supplies the bounded refresh policy. No business request is replayed by the helper after RoundTrip begins. Underlying Go HTTP transports retain their standard internal retry behavior. Forced h3 and required ECH never downgrade.`n"
        [IO.File]::WriteAllText((Join-Path $ridOutput 'OUTBOUND-NOTICES.txt'),$notice,$utf8)
        $metadata = [ordered]@{ schemaVersion=1; version=$helperVersion; protocolVersion=1; runtimeIdentifier=$rid; goVersion=$tool.Version; sourceCommit=$sourceCommit; sourceSha256=$sourceSha256; executableSha256=$exeHash; machine=$machine; toolchainArchiveSha256=$tool.ArchiveSha256; toolchainArchiveUrl=$tool.ArchiveUrl; toolchainArchiveBytes=$tool.ArchiveBytes; toolchainInputCount=$tool.InputCount; toolchainInputSha256=$tool.InputSha256; toolchainDriverSha256=$tool.GoExeSha256; toolchainCompilerSha256=$tool.CompilerSha256; toolchainLinkerSha256=$tool.LinkerSha256; toolchainStandardLibrarySha256=$tool.StandardLibrarySha256; goEnvironment=@{GOROOT='verified-official-archive-root'; GOTOOLDIR='pkg/tool/windows_amd64'; GOENV='off'; GOTOOLCHAIN='local'}; sourceInputs=$sourceInputs; productionSourceInputs=$productionSourceInputs; dependencies=$dependencies; buildFlags=@('-trimpath','-buildvcs=false','-mod=readonly','CGO_ENABLED=0','GOOS=windows',('GOARCH='+$architecture),('-ldflags=-s -w -X main.buildVersion='+$helperVersion)); unitTestsRun=(!$SkipTests); liveVerified=$false }
        [IO.File]::WriteAllText((Join-Path $ridOutput 'outbound-build.json'),($metadata | ConvertTo-Json -Depth 8) + "`n",$utf8)
        $checksums = ''
        foreach ($name in @('danmu-outbound.exe','outbound-build.json','OUTBOUND-LICENSE.txt','OUTBOUND-UPSTREAM.txt','outbound-source.zip','OUTBOUND-NOTICES.txt')) { $checksums += (Get-FileHash -LiteralPath (Join-Path $ridOutput $name) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $name + "`n" }
        [IO.File]::WriteAllText((Join-Path $ridOutput 'OUTBOUND-SHA256SUMS.txt'),$checksums,$utf8)
        Write-Host "$rid machine=$actualMachine SHA256=$exeHash sourceSHA256=$sourceSha256"
    }
    if ((Get-FileHash (Join-Path $moduleRoot 'go.mod') -Algorithm SHA256).Hash -ne $modHash -or (Get-FileHash (Join-Path $moduleRoot 'go.sum') -Algorithm SHA256).Hash -ne $sumHash) { throw 'Locked dependency inputs changed during the build.' }
} finally {
    Pop-Location
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name,$prior[$name],'Process') }
}
