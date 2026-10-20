# 刷新 applier 的**签入产物**（AGENT.md §12.5）
#
# 为什么需要它：`applier_bin/{RID}/Client.Applier.exe` 是**签入仓库的构建产物**（和 hdiffpatch_bin 里的
# 二进制同类），不是编译 Client/Share 时产生的。改了 applier 或它依赖的源码之后必须跑一次这个脚本，
# 否则随包发出去的是旧 exe —— 而测试跑的是项目编译出来的那个，全绿也发现不了。
# 守卫测试 `ApplierPackagingTests` 会把"忘了刷新"变成红灯。
#
# **产物形态 = Native AOT**（默认）：宿主端要求"目标机上没有 .NET 也能跑"，而 applier 是**独立进程**
# （继承不到宿主的自包含运行时）。AOT 产物约 6 MB/RID；框架依赖单文件只有 ~0.3 MB 但**必须有 .NET 运行时**。
# 策略测试 `ApplierPackagingTests.CheckedInApplier_IsNativeAot` 钉住这件事。
#
# 为什么不能"构建时自动刷新"：唯一自动化手段是让 Client 引用 applier 项目，而 applier 引用 Client
# → 项目环，MSBuild 直接报错。所以只能是"脚本 + 守卫测试"。
#
# 工具链：Windows 上的 AOT 需要 MSVC（Visual Studio 的"使用 C++ 的桌面开发"）；
#         linux 的 AOT **不能跨 OS 编译**（`Cross-OS native compilation is not supported`），
#         所以在 WSL / Linux CI 里执行同一套参数（本脚本默认走 WSL）。
#
# 用法：
#   pwsh -File refresh-applier.ps1                    # 默认覆盖 HdiffTool.ShippedRids（随包平台全部，AOT）
#   pwsh -File refresh-applier.ps1 -Rids win-x64      # 只刷某一个/某几个（**策略测试会因此变红**，见下）
#   pwsh -File refresh-applier.ps1 -NoLinux           # 跳过 linux-*（没有 WSL/Linux 工具链的机器）
#   pwsh -File refresh-applier.ps1 -Fdd               # **调试用**：产出框架依赖单文件（策略测试会变红）
param(
    [string[]]$Rids = @(),
    [switch]$Fdd,
    [switch]$NoLinux,
    [string]$WslDistro = 'Debian'
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$proj = Join-Path $root 'Ra3.BattleNet.Updater.Client.Applier\Ra3.BattleNet.Updater.Client.Applier.csproj'
$shareDir = Join-Path $root 'Ra3.BattleNet.Updater.Share'
$binDir = Join-Path $shareDir 'applier_bin'
# 【必须】记录文件**不放 applier_bin/**：那个目录整目录随宿主产出走（Content），而记录是**构建期**的东西，
# 运行时没人读它 —— 放进去等于把一个带开发机绝对路径的文件塞进用户的安装树。
$recPath = Join-Path $shareDir 'applier_build.json'

if (-not (Test-Path $proj)) { throw "找不到 applier 项目：$proj" }

$mode = if ($Fdd) { 'fdd-singlefile' } else { 'aot' }
if ($Fdd) {
    Write-Warning '-Fdd 产出的是**框架依赖**产物：只适合本机调试。签入仓库的必须是 AOT —— 策略测试会变红。'
}

function Get-ShippedRids {
    # 单一来源：从 HdiffTool 里读（避免这里再维护一份会漂移的清单）
    $src = Get-Content (Join-Path $shareDir 'Utilities\HdiffTool.cs') -Raw
    $m = [regex]::Match($src, 'ShippedRids\s*=\s*\[(?<list>[^\]]*)\]')
    if (-not $m.Success) { return @() }
    return [regex]::Matches($m.Groups['list'].Value, '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
}

function ConvertTo-WslPath([string]$winPath) {
    $p = [IO.Path]::GetFullPath($winPath).Replace('\', '/')
    if ($p -match '^([A-Za-z]):(.*)$') { return '/mnt/' + $Matches[1].ToLowerInvariant() + $Matches[2] }
    throw "无法转换成 WSL 路径（需要盘符路径）：$winPath"
}

function Invoke-WslScript([string]$script) {
    $tmp = Join-Path $env:TEMP ("wsl-" + [guid]::NewGuid().ToString('N') + '.sh')
    [IO.File]::WriteAllText($tmp, ($script -replace "`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
    try { return (Get-Content $tmp -Raw | & wsl -d $WslDistro -- bash -s 2>&1 | Out-String) }
    finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
}

# ---- 1) 要覆盖哪些 RID：显式参数 > 记录里已有的 > HdiffTool.ShippedRids ----
if ($Rids.Count -eq 0 -and (Test-Path $recPath)) {
    $prev = (Get-Content $recPath -Raw | ConvertFrom-Json).Rids
    if ($prev) { $Rids = @($prev) }
}
if ($Rids.Count -eq 0) { $Rids = @(Get-ShippedRids) }
if ($Rids.Count -eq 0) { $Rids = @([System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier) }
if ($NoLinux) { $Rids = @($Rids | Where-Object { $_ -notlike 'linux-*' }) }
Write-Host ("[refresh-applier] mode = $mode ; RIDs = " + ($Rids -join ', '))

$exes = @{}
foreach ($rid in $Rids) {
    $exeName = if ($rid -like 'win-*') { 'Client.Applier.exe' } else { 'Client.Applier' }
    $dstDir = Join-Path $binDir $rid
    New-Item -ItemType Directory -Force -Path $dstDir | Out-Null
    Get-ChildItem $dstDir -File -ErrorAction SilentlyContinue | Remove-Item -Force

    if ($mode -eq 'aot' -and $rid -notlike 'win-*') {
        # ---- Linux AOT：不能跨 OS 编译 ⇒ 在 WSL 里用同一套参数出 ----
        $unixOut = "/tmp/applier-publish-$rid-" + [guid]::NewGuid().ToString('N')
        $bash = @(
            'set -e'
            'export PATH="$HOME/.dotnet:$PATH"'
            'command -v dotnet >/dev/null 2>&1 || { echo "[refresh-applier] 这台 Linux 上没有 .NET 10 SDK（AOT 必需）" >&2; exit 3; }'
            "cd '__REPO__'"
            # 【必须】-p:ObjCopyName=objcopy：AOT 收尾要 strip 一次符号表，默认调 llvm-objcopy。
            # Debian 上 /usr/bin/llvm-objcopy 常是"指向未安装的 lib/llvm-19"的坏符号链接 →
            # 报 `llvm-objcopy : error : No such file or directory`（编译其实已经成功，只差这一步）。
            # GNU objcopy 同样吃 --strip-debug --strip-unneeded，产物一致（已用 file 核对：ELF stripped）。
            "dotnet publish 'Ra3.BattleNet.Updater.Client.Applier/Ra3.BattleNet.Updater.Client.Applier.csproj' -c Release -r __RID__ --self-contained true -p:PublishAot=true -p:ObjCopyName=objcopy -o '__OUT__'"
            "mkdir -p '__DSTDIR__'"
            "cp '__OUT__/__EXE__' '__DST__'"
            "rm -rf '__OUT__'"
        ) -join "`n"
        $bash = $bash.
            Replace('__REPO__', (ConvertTo-WslPath $root)).
            Replace('__RID__', $rid).
            Replace('__OUT__', $unixOut).
            Replace('__DSTDIR__', (ConvertTo-WslPath $dstDir)).
            Replace('__DST__', (ConvertTo-WslPath (Join-Path $dstDir $exeName))).
            Replace('__EXE__', $exeName)
        $out = Invoke-WslScript $bash
        if ($LASTEXITCODE -ne 0) {
            throw "linux AOT 发布失败（rid=$rid, exit=$LASTEXITCODE）。WSL 里需要 .NET 10 SDK；也可以 -NoLinux 或改在 Linux CI 出。`n$out"
        }
    }
    else {
        $outDir = Join-Path $env:TEMP ("applier-publish-$rid-" + [guid]::NewGuid().ToString('N'))
        try {
            if ($mode -eq 'aot') {
                & dotnet publish $proj -c Release -r $rid --self-contained true -p:PublishAot=true -o $outDir | Out-Null
            }
            else {
                & dotnet publish $proj -c Release -r $rid --self-contained false -p:PublishSingleFile=true -o $outDir | Out-Null
            }
            if ($LASTEXITCODE -ne 0) { throw "publish 失败（rid=$rid, mode=$mode, exit=$LASTEXITCODE）" }

            $src = Join-Path $outDir $exeName
            if (-not (Test-Path $src)) { throw "发布产物里没有 $exeName（rid=$rid）：$outDir" }
            Copy-Item $src (Join-Path $dstDir $exeName) -Force
        }
        finally { Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    $final = Join-Path $dstDir $exeName
    if (-not (Test-Path $final)) { throw "产物没有落地：$final" }
    $exes[$rid] = [PSCustomObject]@{ Name = $exeName; Bytes = (Get-Item $final).Length }
    Write-Host ("[refresh-applier] {0,-10} → {1}  ({2:N0} B)" -f $rid, $final, (Get-Item $final).Length)
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
    Mode           = $mode
    Rids           = @($Rids)
    Exes           = $exes
    Sources        = $keys.Count
    # 用相对写法，别把开发机的绝对路径写进仓库
    PublishCommand = if ($mode -eq 'aot') {
        'dotnet publish Ra3.BattleNet.Updater.Client.Applier -c Release -r {rid} --self-contained true -p:PublishAot=true（linux-* 在 Linux/WSL 上执行同一命令：Windows 不能跨 OS AOT）'
    }
    else {
        'dotnet publish Ra3.BattleNet.Updater.Client.Applier -c Release -r {rid} --self-contained false -p:PublishSingleFile=true'
    }
    UtcTime        = (Get-Date).ToUniversalTime().ToString('o')
}
$json = ($rec | ConvertTo-Json -Depth 5).Replace("`r`n", "`n")
[IO.File]::WriteAllText($recPath, $json, [System.Text.UTF8Encoding]::new($false))
$total = ($exes.Values | Measure-Object -Property Bytes -Sum).Sum
Write-Host ("[refresh-applier] SourcesHash = $sourcesHash（$($keys.Count) 个源文件）；合计 {0:N0} B；记录写入 {1}" -f $total, $recPath)