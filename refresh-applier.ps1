# 刷新 applier 的**签入产物**（AGENT.md §12.5）
#
# 为什么需要它：`applier_bin/{RID}/Client.Applier.exe` 是**签入仓库的构建产物**（和 hdiffpatch_bin 里的
# 二进制同类），不是编译 Client/Share 时产生的。改了 applier 或它依赖的源码之后，必须跑一次这个脚本，
# 否则随包发出去的是旧 exe —— 而测试跑的是项目编译出来的那个，全绿也发现不了。
# 守卫测试 `ApplierPackagingTests.CheckedInApplier_MatchesCurrentSources` 会把"忘了刷新"变成红灯。
#
# 为什么不能"构建时自动刷新"：唯一自动化手段是让 Client 引用 applier 项目，而 applier 引用 Client
# → 项目环，MSBuild 直接报错。所以只能是"脚本 + 守卫测试"。
#
# 用法：pwsh -File refresh-applier.ps1
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$proj = Join-Path $root 'Ra3.BattleNet.Updater.Client.Applier\Ra3.BattleNet.Updater.Client.Applier.csproj'
$binDir = Join-Path $root 'Ra3.BattleNet.Updater.Share\applier_bin'
$recPath = Join-Path $binDir 'applier.src.json'
$exeName = 'Client.Applier.exe'

if (-not (Test-Path $proj)) { throw "找不到 applier 项目：$proj" }

# ---- 1) RID：沿用记录里那个（保证在任何平台刷新出的都是同一份产物），没有就用本机 ----
$rid = $null
if (Test-Path $recPath) { $rid = (Get-Content $recPath -Raw | ConvertFrom-Json).Rid }
if ([string]::IsNullOrWhiteSpace($rid)) {
    $rid = (& dotnet --info 2>&1 | Select-String -Pattern '^\s*RID:\s*(\S+)' | Select-Object -First 1)
    if ($rid) { $rid = $rid.Matches[0].Groups[1].Value }
    else { $rid = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier }
}
Write-Host "[refresh-applier] RID = $rid"

# ---- 2) 发布（框架依赖 + 单文件：applier_bin/{RID}/ 下**只有一个文件**）----
$out = Join-Path $env:TEMP ('applier-publish-' + [guid]::NewGuid().ToString('N'))
$publishCommand = "dotnet publish `"$proj`" -c Release -r $rid --self-contained false -p:PublishSingleFile=true -o <tmp>"
try {
    & dotnet publish $proj -c Release -r $rid --self-contained false -p:PublishSingleFile=true -o $out | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "publish 失败（exit=$LASTEXITCODE）" }

    $src = Join-Path $out $exeName
    if (-not (Test-Path $src)) { throw "发布产物里没有 $exeName：$out" }

    $dstDir = Join-Path $binDir $rid
    New-Item -ItemType Directory -Force -Path $dstDir | Out-Null
    Get-ChildItem $dstDir -File -ErrorAction SilentlyContinue | Remove-Item -Force   # 这个目录里只该有那一个 exe
    Copy-Item $src (Join-Path $dstDir $exeName) -Force
    Write-Host ("[refresh-applier] 已拷入 " + (Join-Path $dstDir $exeName) + "  (" + (Get-Item $src).Length + " B)")
}
finally {
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
}

# ---- 3) 源码指纹（守卫测试按同一算法核对；两端必须逐字一致，见 ApplierPackagingTests）----
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

$rec = [PSCustomObject]@{
    SourcesHash    = $sourcesHash
    Rid            = $rid
    PublishCommand = $publishCommand
    Sources        = $keys.Count
    UtcTime        = (Get-Date).ToUniversalTime().ToString('o')
}
New-Item -ItemType Directory -Force -Path $binDir | Out-Null
$json = $rec | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText($recPath, $json, [System.Text.UTF8Encoding]::new($false))
Write-Host ("[refresh-applier] SourcesHash = $sourcesHash  （$($keys.Count) 个源文件）")
Write-Host ("[refresh-applier] 指纹写入 " + $recPath)