param([Parameter(Mandatory=$true)][string]$RuntimeBundle,[Parameter(Mandatory=$true)][string]$OptionalRedisNodeModules)
$ErrorActionPreference='Stop'
# 注意：本文件必须是带 UTF-8 BOM 的 .ps1。PowerShell 5.1 对无 BOM 脚本按系统 ANSI 代码页解码，
# 中文注释会被解坏并吞掉换行，导致语句错位（曾表现为 Join-Path 的结果"变成"空值）。
# 把可选 Redis 载荷装进运行环境包。载荷的每个包都放在 node_modules 根下（redis 需要同级解析
# @redis/* 与 cluster-key-slot），并且**不进** SHA256SUMS.txt：它由 OptionalRedisStager 在
# 启动时按 config/.env 的 LOCAL_REDIS_URL 条件铺开并单独校验，不参与受管依赖的事务替换。
if(-not(Test-Path (Join-Path $RuntimeBundle 'SHA256SUMS.txt'))){throw 'Runtime bundle is missing SHA256SUMS.txt'}
if(-not(Test-Path $OptionalRedisNodeModules)){throw 'Optional redis node_modules not found'}
$nodeModules=Join-Path $RuntimeBundle 'nodejs-project\node_modules'
if(-not(Test-Path $nodeModules)){throw 'Runtime bundle is missing nodejs-project/node_modules'}

$payloadRoots=@()
foreach($packageRoot in (Get-ChildItem -LiteralPath $OptionalRedisNodeModules -Force -Directory)){
  if($packageRoot.Name.StartsWith('@')){
    foreach($scoped in (Get-ChildItem -LiteralPath $packageRoot.FullName -Force -Directory)){
      $payloadRoots+=,[pscustomobject]@{Name=($packageRoot.Name+'/'+$scoped.Name);Directory=$scoped.FullName}
    }
  } else {
    $payloadRoots+=,[pscustomobject]@{Name=$packageRoot.Name;Directory=$packageRoot.FullName}
  }
}
foreach($expected in @('redis','cluster-key-slot','@redis/client')){
  if(@($payloadRoots.Name) -notcontains $expected){throw ('Optional redis payload is missing '+$expected)}
}

$lines=New-Object System.Collections.Generic.List[string]
$installed=@()
foreach($payloadRoot in $payloadRoots){
  $base=$payloadRoot.Directory.Length+1
  foreach($file in (Get-ChildItem -LiteralPath $payloadRoot.Directory -File -Recurse)){
    $relative=$payloadRoot.Name+'/'+$file.FullName.Substring($base).Replace('\','/')
    if($relative.Split('/') -contains '..'){throw 'Unsafe redis payload path'}
    $target=Join-Path $nodeModules $relative.Replace('/','\')
    New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    $lines.Add((Get-FileHash $target -Algorithm SHA256).Hash+'  '+$relative)
    $installed+=,$relative.Replace('/','\')
  }
}
$payloadManifest=Join-Path $RuntimeBundle 'redis-payload.SHA256SUMS.txt'
$lines | Set-Content -LiteralPath $payloadManifest -Encoding ASCII

# 主受管清单必须排除载荷路径；否则它们会被当成受管依赖无条件铺开，并破坏"按配置可选"的语义。
$manifest=Join-Path $RuntimeBundle 'SHA256SUMS.txt'
$installedSet=@{}
foreach($relative in $installed){$installedSet[$relative]=$true}
$kept=New-Object System.Collections.Generic.List[string]
$dropped=0
foreach($line in (Get-Content -LiteralPath $manifest)){
  if($line.Length -ge 67 -and $line.Substring(64,2) -eq '  '){
    $relative=$line.Substring(66).Replace('/','\')
    if($installedSet.ContainsKey($relative)){$dropped++;continue}
  }
  $kept.Add($line)
}
$kept | Set-Content -LiteralPath $manifest -Encoding ASCII
if($dropped -ne 0){Write-Output ('Removed '+$dropped+' redis entries from the managed manifest')}

& (Join-Path $PSScriptRoot 'New-RuntimeBuildIdentity.ps1') -RuntimeBundle ([IO.Path]::GetFullPath($RuntimeBundle))
Write-Output ('Optional redis payload files: '+$lines.Count)
