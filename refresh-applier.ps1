# 刷新 applier 的**签入产物**（AGENT.md §12.5）
#
# 为什么需要它：`applier_bin/{RID}/Client.Applier.exe` 是**签入仓库的构建产物**（和 hdiffpatch_bin 里的
# 二进制同类），不是编译 Client/Share 时产生的。改了 applier 或它依赖的源码之后必须跑一次这个脚本，
# 否则随包发出去的是旧 exe —— 而测试跑的是项目编译出来的那个，全绿也发现不了。
# 守卫测试 `ApplierPackagingTests` 会把"忘了刷新"变成红灯。
#
# 为什么不能"构建时自动刷新"：唯一自动化手段是让 Client 引用 applier 项目，而 applier 引用 Client
# → 项目环，MSBuild 直接报错。所以只能是"脚本 + 守卫测试"。
#
# 用法：
#   pwsh -File refresh-applier.ps1                    # 默认覆盖 HdiffTool.ShippedRids（随包平台全部）
#   pwsh -File refresh-applier.ps1 -Rids win-x64      # 只刷某一个/某几个（**策略测试会因此变红**，见下）
param([string[]]$Rids = @())

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$proj = Join-Path $root 'Ra3.BattleNet.Updater.Client.Applier\Ra3.BattleNet.Updater.Client.Applier.csproj'
$shareDir = Join-Path $root 'Ra3.BattleNet.Updater.Share'
$binDir = Join-Path $shareDir 'applier_bin'
# 【必须】记录文件**不放 applier_bin/**：那个目录整目录随宿主产出走（Content），而记录是**构建期**的东西，
# 运行时没人读它 —— 放进去等于把一个带开发机绝对路径的文件塞进用户的安装树。
$recPath = Join-Path $shareDir 'applier_build.json'

if (-not (Test-Path $proj)) { throw "找不到 applier 项目：$proj" }

function Get-ShippedRids {
    # 单一来源：从 HdiffTool 里读（避免这里再维护一份会漂移的清单）
    $src = Get-Content (Join-Path $shareDir 'Utilities\HdiffTool.cs') -Raw
    $m = [regex]::Match($src, 'ShippedRids\s*=\s*\[(?<list>[^\]]*)\]')
    if (-not $m.Success) { return @() }
    return [regex]::Matches($m.Groups['list'].Value, '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
}

# ---- 1) 要覆盖哪些 RID：显式参数 > 记录里已有的 > HdiffTool.ShippedRids ----
if ($Rids.Count -eq 0 -and (Test-Path $recPath)) {
    $prev = (Get-Content $recPath -Raw | ConvertFrom-Json).Rids
    if ($prev) { $Rids = @($prev) }
}
if ($Rids.Count -eq 0) { $Rids = @(Get-ShippedRids) }
if ($Rids.Count -eq 0) { $Rids = @([System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier) }
Write-Host ("[refresh-applier] RIDs = " + ($Rids -join ', '))

$exes = @{}
foreach ($rid in $Rids) {
    $exeName = if ($rid -like 'win-*') { 'Client.Applier.exe' } else { 'Client.Applier' }
    $out = Join-Path $env:TEMP ("applier-publish-$rid-" + [guid]::NewGuid().ToString('N'))
    try {
        & dotnet publish $proj -c Release -r $rid --self-contained false -p:PublishSingleFile=true -o $out | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "publish 失败（rid=$rid, exit=$LASTEXITCODE）" }

        $src = Join-Path $out $exeName
        if (-not (Test-Path $src)) { throw "发布产物里没有 $exeName（rid=$rid）：$out" }

        $dstDir = Join-Path $binDir $rid
        New-Item -ItemType Directory -Force -Path $dstDir | Out-Null
        Get-ChildItem $dstDir -File -ErrorAction SilentlyContinue | Remove-Item -Force
        Copy-Item $src (Join-Path $dstDir $exeName) -Force
        $exes[$rid] = [PSCustomObject]@{ Name = $exeName; Bytes = (Get-Item $src).Length }
        Write-Host ("[refresh-applier] {0,-10} → {1}  ({2} B)" -f $rid, (Join-Path $dstDir $exeName), (Get-Item $src).Length)
    }
    finally { Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue }
}

# 清掉不再覆盖的 RID 目录（避免"以为不带、其实还躺在产出里"）
Get-ChildItem $binDir -Directory -ErrorAction SilentlyContinue |
    Where-Object { $Rids -notcontains $_.Name } |
    ForEach-Object { Write-Host ("[refresh-applier] 移除不再覆盖的 " + $_.Name); Remove-Item $_.FullName -Recurse -Force }

# 【必须】承载目录里**只允许** {RID}/ 子目录：它整目录随宿主产出走（Content），
# 所以任何游离文件（记录、说明、临时产物）都会进用户的安装树。历史上这里躺过一份 applier.src.json。
Get-ChildItem $binDir -File -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host ("[refresh-applier] 移除承载目录里的游离文件 " + $_.Name); Remove-Item $_.FullName -Force }

# ---- 2) 源码指纹（守卫测试按同一算法核对；两端必须逐字一致）----
$dirs = @('Ra3.BattleNet.Updater.Client.Applier', 'Ra3.BattleNet.Updater.Client', 'Ra3.BattleNet.Updater.Share')
$map = @{}
foreach ($d in $dirs) {
    $base = Join-Path $root $d
    foreach ($f in (Get-ChildItem $base -Recurse -File -Include *.cs, *.csproj)) {
        $rel = [IO.Path]::GetRelativePath($root, $f.FullName).Replace('\', '/')
        if ($rel -match '/(bin|obj)/') { continue }
        $map[$rel] = (Get-FileHash $f.FullName -Algorithm MD5).Hash.ToLower()
    }
}
$keys = [string[]]$map.Keys
[Array]::Sort($keys, [System.StringComparer]::Ordinal)
$text = (($keys | ForEach-Object { "$($_):$($map[$_])" }) -join "`n")
$bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($text)
$sourcesHash = (([System.Security.Cryptography.MD5]::Create().ComputeHash($bytes)) | ForEach-Object { $_.ToString('x2') }) -join ''

# ---- 3) 记录（放 Share 根下，**不随宿主产出走**）----
$rec = [PSCustomObject]@{
    SourcesHash    = $sourcesHash
    Rids           = @($Rids)
    Exes           = $exes
    Sources        = $keys.Count
    # 用相对写法，别把开发机的绝对路径写进仓库
    PublishCommand = 'dotnet publish Ra3.BattleNet.Updater.Client.Applier -c Release -r {rid} --self-contained false -p:PublishSingleFile=true'
    UtcTime        = (Get-Date).ToUniversalTime().ToString('o')
}
$json = ($rec | ConvertTo-Json -Depth 5).Replace("`r`n", "`n")
[IO.File]::WriteAllText($recPath, $json, [System.Text.UTF8Encoding]::new($false))
Write-Host ("[refresh-applier] SourcesHash = $sourcesHash（$($keys.Count) 个源文件）；记录写入 " + $recPath)