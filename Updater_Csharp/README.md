# Ra3.BattleNet.Updater —— 使用说明

> 面向**引用本库的开发者**：怎么接、怎么用、会拿到什么、该怎么处理。
> 设计规范（甲方要求）在仓库根目录的 `AGENT.md`（本地文档，不入库）。
> 子项目参数细节见同目录下各自的 README：
> `Ra3.BattleNet.Updater.XmlGenerator/README.md`（清单生成 + UUID 关联规则）、
> `Ra3.BattleNet.Updater.Server.PatchGenerator/README.md`（补丁参数与取舍）。

## 0. 它是什么

服务端只产出**静态文件树**（可整体交给 CDN），客户端按内容推导地址、只下载变化的部分；
拿不到增量时干净回落完整下载。库**不弹 UI、不退出进程、不抛异常**（唯一例外是取消/超时，见 §2.4）。

## 1. 目录结构（当前）

| 项目 | 角色 |
|---|---|
| `Ra3.BattleNet.Updater.Share` | 协议模型（清单 / 离线补丁包）、哈希、外部工具封装 |
| `Ra3.BattleNet.Updater.Core` | **更新引擎**（无 UI、无产品耦合）——**客户端只需引用它** |
| `Ra3.BattleNet.Updater.Server` | 清单生成 + 生成期自检 + 补丁生成（业务库） |
| `Ra3.BattleNet.Updater.XmlGenerator` | 壳：生成 `manifest.xml`（含自检报告） |
| `Ra3.BattleNet.Updater.Server.PatchGenerator` | 壳：生成 `patches/` 与 `files/` |
| `Ra3.BattleNet.Updater.Client.Update` | 壳：独立进程跑一次更新（宿主不是 C# 时用） |
| `Ra3.BattleNet.Updater.Client` / `.Server.CLI` / `.Client.CLI` | 离线补丁包链路（一次性 A→B）——**历史资产**，当前增量流程不走它，见 §8 |
| `Ra3.BattleNet.Updater.Tests` | 单元 + 端到端测试 |

约定：**核心逻辑 = 无后缀的库项目**，**可执行壳 = 带后缀、按角色命名**。

## 2. 客户端

### 2.1 怎么引用

- `ProjectReference` 或直接引用 DLL：**`Ra3.BattleNet.Updater.Core`**（TFM `net10.0`）。
- **外部工具必须随产物部署**：`hdiffpatch_bin/{win-x64,linux-x64}/{hdiffz,hpatchz}`。
  默认从**程序自身目录**找（`UpdateConfig.ToolsDir` 可改）；`Share` 项目会自动把它们复制到输出目录。
- 其它无需依赖：`Newtonsoft.Json` / `Serilog` 只被 §8 的历史链路用到。

### 2.2 最小用法

```csharp
using Ra3.BattleNet.Updater.Core;

var cfg = new UpdateConfig
{
    RootPath    = @"D:\Game",                                                  // 必填
    ManifestUrl = "https://objects.ra3battle.net/updater_test/manifest.xml",   // 必填
    // 可选：ExcludedDirs / FallbackBaseUrls / ToolsDir / LogPath / MaxConcurrency / ...
};

var progress = new Progress<UpdateProgress>(p =>
    Console.WriteLine($"[{p.Current}/{p.Total}] {p.FileName} {p.Stage}"));

UpdateResult r = new Updater(cfg).Run(progress);            // 同步入口
// 或：UpdateResult r = await new Updater(cfg).RunAsync(progress, ct);   // 可取消
```

### 2.3 返回值与怎么处理

宿主只需要一个 `switch`：

| `UpdateOutcome` | 含义 | 宿主该做什么 |
|---|---|---|
| `UpToDate` | 已是最新（1 次清单请求，命中 304 时 **0 字节**） | 直接启动 |
| `Updated` | 更新成功，本地清单已更新 | 直接启动 |
| `NeedsHostFallback` | **正常分支**：本地状态不可信 / 达到保护阈值（原因见 `Reason`+`Detail`） | 走你自己的整包逻辑（BT / 直链）；**这不是错误** |
| `Failed` | 意外失败：网络 / 磁盘 / 权限 / 被取消 | 提示重试；`Detail` 里有原因 |

`UpdateResult` 字段（`record`，只读）：

| 字段 | 说明 |
|---|---|
| `Outcome` / `Reason` / `Detail` | 结果类型 / 机器可读原因码 / 人看的细节 |
| `Total` `Skipped` `Moved` `Patched` `Full` `FailedCount` | 计划与执行计数 |
| `BytesDownloaded` | **落盘的内容**字节（≈ 这次真正新增了多少内容） |
| `PayloadBytes` | 从响应正文**读到的**字节（解压后；含清单与重试/续传各段） |
| `WireBytes` / `WireSentBytes` / `WireReceivedBytes` | **真正上网的字节**（连接层计数，含 TLS/HTTP 头）——**做带宽统计用这一组** |
| `Elapsed` / `HttpVersion` | 耗时 / 协商到的 HTTP 版本（如 `HTTP/2.0`） |
| `Applied` | 便捷属性：`UpToDate` 或 `Updated` 时为 `true` |

> 三个字节口径别混用：`BytesDownloaded` 是内容、`PayloadBytes` 是解压后正文、`WireBytes` 才是网线。
> 实测同一次真实更新（v4→v5，35 请求）：内容 1.85 MiB、payload 1.86 MiB、**wire 1.89 MiB**。

### 2.4 取消与超时

- `Run(progress, ct)`：`ct` 取消，或超过 `SessionTimeout`（默认 2 小时）→ **返回 `Failed`**
  （`Reason = io_error`，`Detail = "已取消"` 或 `"超出整体时限（…）"`），**不抛异常**。
- 单请求超时是分层的：连接 5 s、响应头 60 s、**正文停滞**超时 60 s
  （只要还在收到数据就不算超时 —— 大文件慢链路不会被掐死）。

### 2.5 配置项（`UpdateConfig`，全部 `init`）

| 项 | 默认 | 说明 |
|---|---|---|
| `RootPath` | **必填** | 安装根目录 |
| `ManifestUrl` | **必填** | 远端清单地址 |
| `LocalManifestPath` | `{RootPath}/manifest.xml` | 本地清单：**它的字节就是版本身份** |
| `CacheDir` | `{RootPath}/UpdaterCache` | `.part`、`files/` 缓存、ETag 等 |
| `ToolsDir` | 程序自身目录 | `hdiffpatch_bin` 所在目录 |
| `ExcludedDirs` | 空 | 不受管顶层目录名（大小写不敏感），其下文件永不参与更新 |
| `FallbackBaseUrls` | 空 | 备用资源根地址；主地址失败时按顺序回退（404 也会继续试下一个源） |
| `MaxConcurrency` | 4 | 并发**上限**；起始固定 2，全部成功才逐步加，出现失败就回退 |
| `MinFailuresForHostFallback` | 5 | 失败容忍度下限（`max(该值, ceil(比例 × 待处理文件数))`） |
| `FailRatioForHostFallback` | 0.10 | 失败容忍度比例 |
| `FullPackageThresholdFiles` / `...Ratio` | 0 / 0（关） | 可选保险丝：待下载文件数/占比超阈值就交回宿主 |
| `FullPackageRatioMinFiles` | 50 | 占比判据生效的最小文件数 |
| `LogPath` | `{CacheDir}/update.log` | 日志路径 |
| `MaxLogBytes` | 8 MiB | 超过即轮转为 `update.log.1`（只留一代）；0 = 不轮转 |
| `SessionTimeout` | 2 小时 | 整轮时限（最后一道保险） |
| `VerifyUnchangedFiles` | `false` | 对"判定无需更新"的文件也重算哈希（慢，能发现本地损坏） |

### 2.6 日志（机器可读，格式是契约）

`{CacheDir}/update.log`：UTF-8 无 BOM、**LF**、**TAB** 分隔、追加写、超限轮转。

| 行 | 列 |
|---|---|
| `S` 每轮开始 | `run_id`, `utc_iso` |
| `F` 每文件 | `run_id`,`uuid`,`old_md5`,`old_path`,`new_md5`,`new_path`,`action`,`status`,`reason`,`bytes`,`ms`,`payload` |
| `R` 每轮收尾 | `run_id`,`remote_manifest_hash`,`total`,`skip`,`move`,`patch`,`full`,`fail`,`bytes`,`ms`,`result`,`requests`,`payload`,`wire` |

- `action` = `skip` / `move` / `patch` / `full`；`status` = `0` 成功、`1` 未找到、`2` 重试超限、`3` 校验失败、`4` IO/权限/磁盘、`5` 判定需完整包、`9` 其他。
- `reason`（`action=full` 时最关键、**不允许为空**）= `no_local` / `no_patch` / `patch_failed` / `policy` / `local_corrupt` 等。
- **只允许往行尾追加列，不要改既有列**；分析脚本按 `run_id` 分组（一次进程可能跑多轮）。

### 2.7 独立进程壳（宿主不是 C# 时）

```
Client.Update --root <安装目录> --manifest-url <清单地址> [选项]
  --local-manifest <路径>   --cache-dir <目录>   --tools-dir <目录>   --log <路径>
  --exclude <列表>          --fallback <列表>    --concurrency <N>
  --threshold-files <N>     --threshold-ratio <R>  --verify-unchanged   --json
```

退出码：`0` 已最新或已更新 ｜ `1` 需要完整包 / 失败 ｜ `2` 参数或配置错误。
加 `--json` 只输出一行结构化结果（字段同 §2.3，另含 `Ms`）。

## 3. 服务端（发布流水线）

三步，**必须按版本顺序链式生成**（否则 UUID 链断裂 → 补丁全部落空且不报错）：

```powershell
# 1) 清单（v5 以 v4 为基线）
XmlGenerator --old-xmlpath v4.xml --old-root <v4目录> `
             --target-dir  <v5目录> --new-xmloutputpath v5.xml `
             --report reports/v5.report.txt --auto-link-uuids

# 2) 服务端静态树：files/{md5}.bin + patches/{old}_{new}.bin（保留窗口 N=3）
Server.PatchGenerator --manifest v5.xml --manifest-root <v5目录> `
                      --baseline v4.xml --baseline-root <v4目录> `
                      --baseline v3.xml --baseline-root <v3目录> `
                      --output <服务端目录>

# 3) 发布：先传 files/ 与 patches/，**最后**传 manifest.xml（它就是发布开关）
rclone copy <服务端目录>/files   <远端>/files/   --transfers 16
rclone copy <服务端目录>/patches <远端>/patches/ --transfers 16
rclone copyto <服务端目录>/manifest.xml <远端>/manifest.xml
```

关键规则：

- **命名必须带 `.bin`**（`files/{md5}.bin`、`patches/{old}_{new}.bin`）——CDN 的默认缓存**只认扩展名白名单**，
  无扩展名与 `.hdiff` 都进不了（实测），`.bin` 才能命中；这些对象内容寻址、永不变更，缓存没有正确性风险。
- **`manifest.xml` 不要缓存**：它是唯一会变的对象，被缓存住 = 客户端永远以为自己最新。
- 只传 `files/` 与 `patches/` 的新增内容即可（内容寻址，天然去重、不可变）；**清单最后传**。
- 发布后抽查一次缓存：同一 URL 连打两次，第二次应 `cf-cache-status: HIT`。

服务端自检报告会说清"哪些新增/消失条目需要人工确认 UUID"（自动关联默认已打开，见 XmlGenerator README）。

## 4. 协议与清单格式

| 资源 | 地址 |
|---|---|
| 清单 | `{BaseUrl}/manifest.xml` |
| 完整文件 | `{BaseUrl}/files/{md5}.bin` |
| 补丁 | `{BaseUrl}/patches/{oldMd5}_{newMd5}.bin` |

**没有索引文件**：客户端用本地内容的哈希与远端清单里的目标哈希直接推出地址；三者都不可变。

`manifest.xml` 结构（当前）：

```xml
<Metadata Version="1.0.0">          <!-- 格式版本（不是产品版本） -->
  <Tags><UUID/><GenTime/><Commit/></Tags>
  <Includes />                       <!-- 预留，暂未使用 -->
  <Manifest>
    <File>
      <UUID>…32 位十六进制…</UUID>    <!-- 文件身份：路径变了也认它 -->
      <FileName>a.dll</FileName>
      <MD5>…</MD5>
      <Path>\bin\</Path>
      <Version>1.0.0</Version>       <!-- 历史字段，无判断价值 -->
      <Type>Bin</Type>               <!-- 历史字段（Bin/Text），无判断价值 -->
      <Mode>Auto</Mode>              <!-- Auto/Force/Skip：**Skip 生效**（不更新） -->
      <KindOf>NULL</KindOf>          <!-- 历史字段，无判断价值 -->
    </File>
  </Manifest>
</Metadata>
```

客户端逐文件决策（**身份只看 UUID**，不按路径猜）：

```
目标 Mode=Skip 或在排除目录里     → skip（永不参与）
按 UUID 找到前身、内容一致        → move（写到新路径，0 下载）
按 UUID 找到前身、内容不同        → GET patches/{old}_{new}.bin
                                    200 → 打补丁 → 校验 MD5；404 或失败 → 回落完整下载
本地没有同 UUID 的条目            → 本地有同内容且已不需要的文件 → move（0 下载）；否则 GET files/{md5}.bin
```

## 5. 本地跑一遍

```powershell
# 1) 两版清单（第二版以第一版为基线）
dotnet run --project Ra3.BattleNet.Updater.XmlGenerator -- `
  --target-dir <旧版目录> --new-xmloutputpath old.xml
dotnet run --project Ra3.BattleNet.Updater.XmlGenerator -- `
  --target-dir <新版目录> --old-xmlpath old.xml --old-root <旧版目录> --new-xmloutputpath new.xml

# 2) 服务端静态树
dotnet run --project Ra3.BattleNet.Updater.Server.PatchGenerator -- `
  --manifest new.xml --manifest-root <新版目录> `
  --baseline old.xml --baseline-root <旧版目录> --output <服务端目录>
copy new.xml <服务端目录>\manifest.xml

# 3) 起本地静态服务器
python simple_test_web_server/file_server.py --port 23456 --dir <服务端目录>

# 4) 跑一次更新（把 old 的内容拷进 client 目录，并放入 old.xml 作为本地清单）
dotnet run --project Ra3.BattleNet.Updater.Client.Update -- `
  --root <client目录> --manifest-url http://127.0.0.1:23456/manifest.xml
```

## 6. 测试

```powershell
# 单元 + 端到端（自带反射跑器，原因见下）
dotnet run --project Ra3.BattleNet.Updater.Tests -c Release

# 真实历史版本端到端（重活，默认跳过；需要两个环境变量）
$env:UPDATER_E2E_TREES = "<三个历史版本解包目录的父目录>"
$env:UPDATER_TEST_TMP   = "<空间充足的临时盘>"
dotnet run --project Ra3.BattleNet.Updater.Tests -c Release RealVersions
```

可选环境变量：`UPDATER_SIM_OUT`（场景产物落盘目录）、`UPDATER_ISO_TREES`（GB 级大文件用例，门控）。

> 该测试项目自带一个反射跑器（`Tests/Program.cs`）：受限环境里 VSTest 的 testhost 会因
> `Process.GetProcessHandle` 被拒而崩溃。测试本身是标准 xUnit `[Fact]`，
> 正常机器上 `dotnet test Ra3.BattleNet.sln` 照常可用。

## 7. 部署与 CDN 要求（当前结论）

| 项 | 要求 |
|---|---|
| `files/`、`patches/` | **不可变 + 长缓存**；靠 `.bin` 后缀即可进 CF 默认缓存（实测 `MISS → HIT`，命中时 `Range` 仍 206，续传不受影响） |
| `manifest.xml` | **不要缓存**（唯一会变的对象）；条件请求可用，命中 304 = 0 字节 |
| 压缩 | CF 只按 `Content-Type` 白名单压：`manifest.xml`（`text/xml`）19,507 → **3,264 B**；`files/`、`patches/`（`application/octet-stream`）**不压**——已按"不压"设计，不再为它做特殊处理 |
| 单文件缓存上限 | CF 的 Free/Pro/Business 为 **512 MB**（Enterprise 5 GB）；超出的对象永远回源 → GB 级文件按"不可缓存"设计 |
| 外部工具 | `hdiffpatch_bin` 必须随客户端产物部署 |

## 8. 历史资产（参考用，不是当前流程）

### 8.1 离线补丁包（一次性 A→B 对比）

`Server.CLI`（`--old-manifest/--new-manifest/--old-base/--new-base/--output`）生成一个自包含目录
（`patch-manifest.json` + `files/` + `patches/`），`Client.CLI`（`--patch/--target`）在**离线**环境把它打上。
它**不参与**当前的服务端发布与在线增量流程（在线流程见 §3/§4）。

> **`BaseVersion` / `TargetVersion` 是这套链路里的历史字段**：写入时两者都填"清单根版本"
> （我们的生成器恒为 `1.0.0`），而**应用端只读 `Operations`，从不读这两个字段** —— 属于"写而不读"的遗留。
> 真要保留语义，应当填**清单哈希**（那才是版本身份）。当前不影响任何行为。

### 8.2 已移除的老链路

`Server.PatchIndexGenerator`（SQLite + `patches.json` 索引）与 `Client.PatchIndexApplyer` 已被
`Server.PatchGenerator` 与 `Client.Update` 取代：现在**没有索引文件**，补丁按内容对直接寻址。

### 8.3 HDiffPatch 选型对比（当年数据，供参考）

| 内容对 | 大小 | bsdiff | deltaq | xdelta3 | hdiffpatch |
|---|---|---|---|---|---|
| `EnhancerCorona.dll` 新旧 | 3.3M → 3.3M | **483K** | 483K | 576–632K | 921K |
| ubuntu live-server ISO 相邻版本 | 1.2G → 1.2G | 耗时过长 | 耗时过长 | 1.1G | **695M** |
| `amdvlk32.dll` → `amdvlk64.dll` | 102M → 115M | **23M** | 23M | 24M | 63M |

结论：小文件 bsdiff 更小，大文件 hdiffpatch 明显更优；本项目统一用 **hdiffz/hpatchz**，
参数为 `-m`（内存够时）否则 `-s`（流式），统一 `-c-lzma`。
