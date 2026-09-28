# Ra3.BattleNet.Updater —— 使用说明

> 面向**引用本库的开发者**：怎么接、怎么用、会拿到什么、该怎么处理。
> 子项目参数细节见同目录下各自的 README：
> `Ra3.BattleNet.Updater.XmlGenerator/README.md`（清单生成 + UUID 关联规则）、
> `Ra3.BattleNet.Updater.Server.PatchGenerator/README.md`（补丁参数与取舍）。

## 目录

- §0 它是什么 · §1 目录结构
- **接入方只看 §2**：§2.1 怎么引用 · §2.2 最小用法 · §2.3 返回值与怎么处理 · §2.4 取消与超时 ·
  §2.5 配置项 · §2.6 日志（格式是契约）· §2.7 独立进程壳（宿主不是 C# 时）·
  **§2.8 宿主集成：两条落地路线与「退出后转交 applier」**（含不支持的场景 / 宿主的义务 / 可选代重启）
- 发布侧：§3 服务端流水线 · §4 协议与清单格式
- 上手：§5 本地跑一遍 · §6 测试 · §7 部署与 CDN 要求

> **先看这五条，能省很多时间**（详情都在 §2.8）：
> ① 要替换**宿主自己的**文件 → `ApplyMode.Staged`，并在宿主退出**之前**把 applier 挂起来；
> ② **不要等 applier 结束** —— 它等的是宿主退出，互等就是死锁；
> ③ 退出动作放主线程的关闭序列里，别在更新线程里 `Environment.Exit`；
> ④ 测试与验收**必须 NTFS**（把 `UPDATER_TEST_TMP` 指到 NTFS 盘）；
> ⑤ **安装根必须对当前用户可写** —— 阶段一就要往 `<root>/UpdaterStage/` 写，不是只有落地才需要。

## 0. 它是什么

服务端只产出**静态文件树**（可整体交给 CDN），客户端按内容推导地址、只下载变化的部分；
拿不到增量时干净回落完整下载。库**不弹 UI、不退出进程、不抛异常**（唯一例外是取消/超时，见 §2.4）。

### 0.1 核心设计（每条一句话，细节见括号里的章节）

- **内容寻址、无索引**：地址由哈希直接推导 —— `files/{md5}.bin`、`patches/{old}_{new}.bin`（§4）。
- **清单即版本身份**：`manifest.xml` 的字节与本地保存的那份比对，一次请求就能判断"要不要更新"（命中 304 时 0 字节）（§4）。
- **文件身份 = UUID**：路径变了也认得出是同一个文件，于是改名也能打补丁、甚至 0 下载（§4 + XmlGenerator README）。
- **失败不致命**：补丁缺失/打不上 → 静默回落完整下载；失败到阈值 → 交回宿主走整包；库不抛异常、不弹 UI（§2.3）。
- **可观测**：每轮写 `update.log`（S/F/R 三种行），结果里给内容字节、payload 与**真正上网的字节**（§2.6）。
- **两种落地路线**：默认 `InPlace` 就地替换；**自更新**用 `ApplyMode.Staged` —— 阶段一一个字节都不动现有树，宿主退出后由独立 applier 落地（§2.8）。

### 0.2 现状与已知限制（**接入前必看**）

**已验证**：协议与清单格式、增量 / 补丁 / 纯改名 / 回落的正确性、中断续做、
暂存更新的两个阶段（含 Cloudflare R2 真机端到端）、宿主身份判定、路径信任边界。

| 项 | 现状 |
|---|---|
| **真宿主集成** | **未验证**。库与壳各自都测过，但"宿主退出前挂 applier → 退出 → 落地 → 拉起宿主"整条链路只有文档 + 单元级测试；接入方必须自己在真宿主上跑一遍 |
| **删除集**（上游删掉的文件） | **不做**。上游删掉的文件会**留在盘上**；若残留的旧 dll/exe 影响加载，需要定期整包重装或另行实现 |
| 非 Windows | **未验证**。改名提交的语义只在 NTFS 上量过（`linux-x64` 工具随包，但整套没在 Linux/macOS 上跑过） |
| 提权 | 库**自己绝不提权、不弹 UAC**。宿主提权则整条链路都提权；宿主不提权则**安装根必须对当前用户可写**（阶段一就要写 `UpdaterStage/`） |
| CI | **不做**，回归靠人工跑全套（§6） |

## 1. 目录结构（当前）

| 项目 | 角色 |
|---|---|
| `Ra3.BattleNet.Updater.Share` | 共享层：协议模型（清单格式）、哈希、外部工具封装 |
| `Ra3.BattleNet.Updater.Client` | **客户端更新引擎**（无 UI、无产品耦合）——**客户端只需引用它** |
| `Ra3.BattleNet.Updater.Server` | **服务端发布逻辑**：清单生成 + 生成期自检 + 补丁生成 |
| `Ra3.BattleNet.Updater.XmlGenerator` | 壳：生成 `manifest.xml`（含自检报告） |
| `Ra3.BattleNet.Updater.Server.PatchGenerator` | 壳：生成 `patches/` 与 `files/` |
| `Ra3.BattleNet.Updater.Client.CLI` | 壳：独立进程跑一次更新（宿主不是 C# 时用） |
| `Ra3.BattleNet.Updater.Tests` | 单元 + 端到端测试 |

约定：**核心逻辑 = 无后缀的库项目**（`Share` / `Client` / `Server`），**可执行壳 = 按角色命名**。
`Share` 被两侧共用；客户端库（`Client`）与服务端库（`Server`）**互不依赖**，各自只依赖 `Share`。`Client` / `Share` / `Server` / `Tests` / `Client.CLI` 各自目录下都有一份**内部导览 README**（改代码前先看那份）。

## 2. 客户端

### 2.1 怎么引用

- `ProjectReference` 或直接引用 DLL：**`Ra3.BattleNet.Updater.Client`**（TFM `net10.0`）。
- **外部工具必须随产物部署**：`hdiffpatch_bin/{win-x64,win-x86,linux-x64}/{hdiffz,hpatchz}`（32 位宿主见 §7）。
  默认从**程序自身目录**找（`UpdateConfig.ToolsDir` 可改）；`Share` 项目会自动把它们复制到输出目录。
- 除框架外**没有第三方依赖**（客户端链路只用到 `Share` 的工具封装与模型）。

### 2.2 最小用法

```csharp
using Ra3.BattleNet.Updater.Client;

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
| `Staged` | 暂存模式阶段一成功：内容已就绪，**但还没落地**（宿主退出后由 applier 生效） | 提示用户退出/重启；**不要**回退到你自己的整包流程 |
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
| `Applied` | 便捷属性：`UpToDate` / `Updated` / `Staged` 时为 `true`（库这边该做的都做完了） |
| `PendingRestart` | `Staged` 时为 `true`：已就绪，需宿主退出/重启后才生效 |

> 三个字节口径别混用：`BytesDownloaded` 是内容、`PayloadBytes` 是解压后正文、`WireBytes` 才是网线。
> 实测同一次真实更新（v4→v5，35 请求）：内容 1.85 MiB、payload 1.86 MiB、**wire 1.89 MiB**。

`Reason` 里几个值得宿主单独认的：`pending_staged_apply`（有已暂存未落地的更新，却走了就地模式）、
`staged_plan_stale`（暂存内容对应的远端清单已经变了，本轮不落地）、
`tree_busy`（等不到树静默：宿主 PID 未退出，或**清单里列出的 `.exe`** 仍在树内运行；**一个文件都没动**）、
`path_escape`（**清单或计划里有逃出安装根的路径 → 整个更新被拒**）。

### 2.4 取消与超时

- `Run(progress, ct)`：`ct` 取消，或超过 `SessionTimeout`（默认 2 小时）→ **返回 `Failed`**
  （`Reason = io_error`，`Detail = "已取消"` 或 `"超出整体时限（…）"`），**不抛异常**。
- 单请求超时是分层的：连接 5 s、响应头 60 s、**正文停滞**超时 60 s
  （只要还在收到数据就不算超时 —— 大文件慢链路不会被掐死）。

### 2.5 配置项（`UpdateConfig`，全部 `init`）

| 项 | 默认 | 说明 |
|---|---|---|
| `RootPath` | **必填** | 安装根目录 |
| `ApplyMode` | `InPlace` | 落地模式：`InPlace` 就地替换（会话内生效）；`Staged` 只暂存到 `<root>/UpdaterStage/`，由独立 applier 在宿主退出后落地（自更新必须走这条）。 |
| `ManifestUrl` | **必填** | 远端清单地址 |
| `LocalManifestPath` | `{RootPath}/manifest.xml` | 本地清单：**它的字节就是版本身份** |
| `CacheDir` | `<系统临时目录>/updater-cache/<安装根指纹>` | 下载产物：`.part`（续传）、内容 blob（文件名 = 目标 MD5，平铺在缓存根）、补丁缓存、`manifest.etag` / `manifest.remote.xml`；**可随时清空**（最坏重下） |
| `ToolsDir` | 程序自身目录 | `hdiffpatch_bin` 所在目录 |
| `ExcludedDirs` | 空 | 不受管顶层目录名（大小写不敏感），其下文件永不参与更新 |
| `FallbackBaseUrls` | 空 | 备用资源根地址；主地址失败时按顺序回退（404 也会继续试下一个源） |
| `MaxConcurrency` | 4 | 并发**上限**；起始固定 2，全部成功才逐步加，出现失败就回退 |
| `MinFailuresForHostFallback` | 5 | 失败容忍度下限（`max(该值, ceil(比例 × 待处理文件数))`） |
| `FailRatioForHostFallback` | 0.10 | 失败容忍度比例 |
| `FullPackageThresholdFiles` / `...Ratio` | 0 / 0（关） | 可选保险丝：待下载文件数/占比超阈值就交回宿主 |
| `FullPackageRatioMinFiles` | 50 | 占比判据生效的最小文件数 |
| `LogPath` | `<LocalApplicationData>/updater-logs/<安装根指纹>/update.log` | 日志路径；**故意不放临时目录**（日志是事后要看的东西） |
| `MaxLogBytes` | 8 MiB | 超过即轮转为 `update.log.1`（只留一代）；0 = 不轮转 |
| `SessionTimeout` | 2 小时 | 整轮时限（最后一道保险） |
| `VerifyUnchangedFiles` | `false` | 对"判定无需更新"的文件也重算哈希（慢，能发现本地损坏） |

> `CacheDir` / `LogPath` 的默认值都带一个**安装根指纹** = `<安装根目录名（≤24 字符）>-<根路径 SHA-256 前 16 位>`。
> 同一台机器上不同安装目录各有各的缓存与日志 —— **单实例锁与续传状态都按缓存目录定位**，
> 两个安装共用一份缓存会把对方判成「已有实例在运行」。Windows 路径大小写不敏感，指纹先归一化再取哈希。
> 排查时可直接打印这两个解析结果（`UpdateConfig.ResolveCacheDir()` / `ResolveLogPath()`）。`LogPath` 的优先级是：显式 `LogPath` → 显式 `CacheDir` 下的 `update.log` → 上面的默认值。

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
Client.CLI --root <安装目录> --manifest-url <清单地址> [选项]
  --local-manifest <路径>   --cache-dir <目录>   --tools-dir <目录>   --log <路径>
  --exclude <列表>          --fallback <列表>    --concurrency <N>
  --threshold-files <N>     --threshold-ratio <R>  --verify-unchanged   --json
  --apply-mode <模式>       inplace（默认，就地替换）/ staged（只暂存，宿主退出后由 applier 落地）
```

退出码：`0` 已最新或已更新 ｜ `1` 需要完整包 / 失败 ｜ `2` 参数或配置错误 ｜ `3` 已暂存待落地（暂存模式，宿主要退出）。
加 `--json` 只输出一行结构化结果。**字段集由库定义**（`UpdateResult.ToJson()`，只增不改）：§2.3 的全部字段 + `Applied` / `PendingRestart` + `Ms`。
壳只要读这行就够了 —— `PendingRestart` 就是"该重启"这个信号，不必靠猜退出码。

暂存更新（`--apply-mode staged`）模式下，**落地由另一次 `--apply` 调用完成**（宿主退出后跑；
库不自己 spawn 进程）：

```
Client.CLI --apply --root <安装目录> --manifest-url <清单地址> [--wait-for-pid <宿主PID>] [--quiescence-timeout 600] [--poll-seconds 2] [--json]
```
`--wait-for-pid` 认宿主时连**进程名与启动时刻**一起比（`--wait-for-name` / `--wait-for-start`）——
PID 会被系统复用，只认 PID 可能把"抢到同一个 PID 的无关进程"当成宿主还在，白等到超时。
后两个参数由 `BuildApplyCommand` **自动填**，宿主不用管。

退出码 `0` = 已落地。宿主可以直接用 `StagedApplier.BuildApplyCommand(exe, cfg)` 生成这条命令。

### 2.8 宿主集成：两条落地路线与「退出后转交 applier」

`UpdateConfig.ApplyMode` 选落地方式：

| 模式 | 谁把文件换上去 | 何时生效 | 适合 |
|---|---|---|---|
| `InPlace`（默认） | 库自己，会话内同步完成 | 立即 | 不需要替换宿主自身的文件 |
| `Staged` | 独立 applier（`Client.CLI --apply`） | **宿主退出之后** | 自更新；要求「要么整版落地、要么不动」 |

**直接用（`InPlace`）**

```csharp
var cfg = new UpdateConfig { RootPath = installDir, ManifestUrl = manifestUrl };
var r = new Updater(cfg).Run(progress);
switch (r.Outcome)
{
    case UpdateOutcome.UpToDate:
    case UpdateOutcome.Updated:           break;                    // 直接启动
    case UpdateOutcome.NeedsHostFallback: /* 交回你的整包逻辑（BT / 直链） */ break;
    case UpdateOutcome.Failed:            /* 提示重试；r.Detail 里有原因 */   break;
}
```

**暂存式自更新（`Staged`）三步**

```
① 库把内容暂存好（宿主还在跑）   → 结果 Staged、PendingRestart == true、退出码 3
② 宿主先挂起 applier，再正常退出 → applier 等宿主 PID + 等树静默
③ applier 落地 → 写本地清单/ETag → 清理 new/（old/ 留到下一轮启动时清）
```

```csharp
var cfg = new UpdateConfig
{
    RootPath = installDir,
    ManifestUrl = manifestUrl,
    ApplyMode = ApplyMode.Staged,
    ApplierQuiescenceTimeout = TimeSpan.FromMinutes(10),
};

var r = new Updater(cfg).Run(progress);
if (r.PendingRestart)
{
    // 【要点 1】先挂 applier，**再**开始退出：它会等我们的 PID，所以现在挂不会打架。
    Process.Start(StagedApplier.BuildApplyCommand(applierExePath, cfg));

    // 【要点 2】把「该重启了」交给你的主线程去处理；不要在更新线程里 Environment.Exit。
    RequestShutdown("update staged");    // 你自己的机制：消息 / Dispatcher / 原子标志
}
```

`applierExePath` 是宿主自己发布的 `Client.CLI` 可执行文件（或用 `dotnet Client.CLI.dll`）。

**要点**

1. **先挂 applier、再退出**：applier 用 `--wait-for-pid <宿主PID>` + 树内进程扫描等静默，可以在宿主还活着时启动；这样即使宿主退出过程中崩了，落地照样完成。反过来（先退再挂）就没人挂了。
2. **不要在更新线程里直接退出进程**：更新很可能不在主线程。库不碰进程生命周期，「退出」是宿主的事 —— 请把 `PendingRestart` 变成信号交给主循环，由主线程按自己的顺序保存状态、关窗、退出。
3. **不要重定向 applier 的 stdio**（`BuildApplyCommand` 已不带重定向）：管道会随宿主退出而失效，applier 写日志就会出错。
4. **别把 applier 放进带 `KILL_ON_JOB_CLOSE` 的 Job Object**，否则宿主一退它被一起杀（**子进程默认继承作业成员身份**）。先确认宿主到底有没有这种作业 —— 自写启动器通常没有（代码里不出现 `CreateJobObject` 就没有）。真有的话，可靠解是**换掉"创建者"**：计划任务、WMI `Win32_Process.Create`（由服务创建，天然在作业外）、或 `explorer.exe <路径>`。
   **别指望 `UseShellExecute = true`**：它**不保证**脱离作业，代价却是失去 .NET 的正确参数转义（`UseShellExecute=true` 时 `ArgumentList` 不能用，得自己拼命令行 —— 我们的路径又长、又带空格和中文），还可能出现控制台窗口。不值当。
5. **落地要树静默**：宿主必须**真的退出**（applier 等的是宿主 PID），并且**清单里列出的 `.exe`** 不能在树内还在跑。扫描范围就是这两条 —— 树内**不被 manifest 管理**的 exe（自备工具、临时进程、WebView2 子进程等）**不会**被判为繁忙，它们的影响是"占着文件导致改名失败"，那时返回 `io_error` 而非 `tree_busy`。等不到静默时 applier 返回 `Failed` + `reason=tree_busy`，**一个文件都不动**，下次再试。（判据原文见 `AGENT.md` §12.5）
6. **落地失败是安全的**：任何一步出问题都不会推进本地清单，下次运行会自动续做；最坏是「这次没生效」，不会「半个版本」。
7. **离线也能落地**：阶段一把远端清单原文留在缓存里，applier 用它校验暂存内容，**落地阶段零网络**；缓存不在才联网重取，那条路上顺手拿到 ETag 并写下来。用缓存离线落地时拿不到 ETag 就不写 —— 下一轮做一次完整 GET，无害。
8. **两种模式互斥**：存在待提交计划时，`InPlace` 会被拒（`reason=pending_staged_apply`）。
9. **回退窗口**：落地后 `old/` 留着上一版备份，到**下一轮暂存开始时**（且上次已落地）自动清掉。
10. **applier 不负责重启宿主**：落地完成后没有任何人会把程序拉起来 —— 那是宿主的责任（外层启动器 / 计划任务 / 让用户再点一次）。要 applier 代劳得另加开关。
11. **启动 applier 之后就不要再开更新会话**：applier 一启动就持有更新锁，再跑一次会得到 `already_running`。
12. **`ApplierQuiescenceTimeout` 从"applier 进入等待"开始算**（不是从宿主死亡、也不是从被创建的那一毫秒），所以这 10 分钟要同时覆盖「宿主从挂上它到真正消失」+「树里其它进程退干净」。**最佳时机是关闭序列的最后一刻**（用户交互都做完、状态存完、真正退出之前）—— 挂太早，用户在"确定要退出吗"上犹豫久了，applier 会等到超时判 `tree_busy`（树没动，下次再来，不是损坏）。
13. **这个值有两个方向都真实的代价**：太低 → 合法的慢关闭会白跑一趟；太高 → 万一宿主因为别的原因一直不消失（自己崩在半途、或有个判活意外），applier 会**一直占着更新锁**，用户重启后再跑 updater 会拿到 `already_running`，持续到这个超时为止。所以别设成小时级，10 分钟这个量级是对的。
14. **用户取消重启就把它杀掉**：`Process.Start` 的返回值留着，取消时 `p.Kill()`。此刻 applier 绝不可能已经动树（它在等宿主死），所以**杀它一定安全** —— 这样"宿主死不掉了"根本不进超时逻辑。

#### 不支持的场景（得宿主自己解决，本库不管）

| 场景 | 为什么 / 宿主该怎么做 |
|---|---|
| 宿主在带 `KILL_ON_JOB_CLOSE` 的 **Job Object** 里 | applier 会被宿主退出**连带杀掉**，本库不处理。自写启动器通常没有这种作业（代码里不出现 `CreateJobObject` 就没有）。真有的话，换掉"创建者"：计划任务 / WMI `Win32_Process.Create` / `explorer.exe <路径>`。**别指望 `UseShellExecute = true`**（不保证脱离，还会丢掉参数转义） |
| 宿主**托盘常驻**、"关窗口不退出进程" | applier 等不到树静默 → `tree_busy`。宿主必须保证"用户点了关闭，进程真的退" |
| 安装根对当前用户**不可写** | **阶段一就会失败**（`UpdaterStage/` 写在安装根下）。要么宿主提权运行，要么安装期放宽 ACL / 装到可写位置 |
| ~~想让 applier 代替宿主重启~~ | **现在有了**（可选，默认关）—— 见下面的「落地后自动拉起宿主」 |

#### 宿主的义务（后面用这个库的人必须照做）

1. **必须在主宿主退出之前**就把 applier 创建出来。它是**独立进程**（`Process.Start` 的产物），不是线程 —— 宿主正常退出、崩溃、被结束都带不走它；但反过来说，**宿主一旦退出就再没有人能把它挂起来了**，这一步只能宿主自己在退出前完成。
2. **退出动作放在主线程的关闭序列里**（更新线程只发信号），不要用 `Environment.Exit` 代替宿主的清理。
3. **推荐直接强制重启**：暂存好后告诉用户"需要重启完成更新"，不给"稍后"选项 —— 用户确认 → 挂 applier → 退出，`tree_busy` / 白等超时这些问题都不会出现。
4. **如果一定要给"稍后"**：用户选稍后时立刻 `Kill()` 掉已挂起的 applier（见要点 14），并且**把"挂 applier"推迟到关闭序列的最后一刻**（别在用户确认之前挂）。

#### 落地后自动拉起宿主（可选，默认关）

```csharp
var cfg = new UpdateConfig
{
    RootPath = installDir,
    ManifestUrl = manifestUrl,
    ApplyMode = ApplyMode.Staged,
    RestartAfterApply = true,                        // 只开这一个开关就够了
};
Process.Start(StagedApplier.BuildApplyCommand(applierExePath, cfg));
```

`BuildApplyCommand` 是在**宿主进程里**执行的，所以它自动填好三样：要拉起的 exe（宿主自己）、
**原始参数原文**（`Environment.CommandLine` 去掉 exe 那一段，引号原封不动、我们不做任何重新解释）、
以及工作目录。要换成别的命令就显式给 `RestartExecutable` / `RestartArguments` / `RestartWorkingDirectory`。

顺序与语义（都有测试守着）：

- **只在落地真正成功（`Updated`）之后**才拉起；没落地成功一次都不会拉。
- **在释放更新锁之后**才拉起 —— 否则刚起来的宿主第一件事跑更新就会拿到 `already_running`。
- 拉起前默认等 1 秒（`RestartDelay`），给系统收尾留一点余量。
- 用 `UseShellExecute = true`（脱离宿主可能存在的 Job Object、落在交互式桌面）。
  启动失败**不影响落地结果**，只把原因并进 `UpdateResult.Detail`（不吞错、也不谎报成功）。
- **权限**：applier 若是提权跑的，它拉起的宿主**也是提权的**。要回到普通用户桌面得走
  `explorer.exe <命令>` 或计划任务（limited token）。

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
      <Mode>Auto</Mode>              <!-- Auto/Force/Skip：**Skip 生效**（该文件永不参与更新） -->
    </File>
  </Manifest>
</Metadata>
```

> **文件级 `Version` / `Type` / `KindOf` 已剔除**：三者都没有判断价值
> （客户端从不读、生成器只是搬运；官方清单实测 `Type` 恒为 `Bin`、`KindOf` 恒为 `NULL`、
> 文件级 `Version` 只有 3 个"哪次生成器跑出来的"残留值）。清单**根节点**的 `Version`
> （格式版本，属性形式）保留，`Mode` 保留。
> **读端仍能读老清单**：官方与历史清单里带着这三个元素时**一律忽略**，不会因此失败。

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

# 3) 起本地静态服务器（Python 标准库自带，不用装依赖）
#    注意：内置服务器不做 Range/ETag，客户端会退化成完整下载 —— 顺带也验证了回落路径；
#    304 / 续传那两块由测试套件的 TestHttpServer（自带 ETag/Range）覆盖。
python -m http.server 23456 --directory <服务端目录>

# 4) 跑一次更新（把 old 的内容拷进 client 目录，并放入 old.xml 作为本地清单）
dotnet run --project Ra3.BattleNet.Updater.Client.CLI -- `
  --root <client目录> --manifest-url http://127.0.0.1:23456/manifest.xml
```

## 6. 测试

（测试怎么组织、夹具、以及自带反射跑器的两条限制，见 [`Ra3.BattleNet.Updater.Tests/README.md`](Ra3.BattleNet.Updater.Tests/README.md)。）

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

### 6.1 交付前必做：用**干净 clone** 构建一次

只看工作区构建**不够** —— `.gitignore` 可能把源码文件一起忽略掉（本仓库就中过：
`.gitignore` 的 `[Ll]og/` 规则吞掉了 `Share/Log/Logger.cs`），此时本机全绿、干净 clone 编译不过。

```powershell
Remove-Item H:\TEST\freshclone -Recurse -Force -ErrorAction SilentlyContinue
git clone --no-hardlinks . H:\TEST\freshclone
dotnet build H:\TEST\freshclone\Ra3.BattleNet.sln -c Release
```

## 7. 部署与 CDN 要求（当前结论）

| 项 | 要求 |
|---|---|
| `files/`、`patches/` | **不可变 + 长缓存**；靠 `.bin` 后缀即可进 CF 默认缓存（实测 `MISS → HIT`，命中时 `Range` 仍 206，续传不受影响） |
| `manifest.xml` | **不要缓存**（唯一会变的对象）；条件请求可用，命中 304 = 0 字节 |
| 压缩 | CF 只按 `Content-Type` 白名单压：`manifest.xml`（`text/xml`）19,507 → **3,264 B**；`files/`、`patches/`（`application/octet-stream`）**不压**——已按"不压"设计，不再为它做特殊处理 |
| 单文件缓存上限 | CF 的 Free/Pro/Business 为 **512 MB**（Enterprise 5 GB）；超出的对象永远回源 → GB 级文件按"不可缓存"设计 |
| 外部工具 | `hdiffpatch_bin` **必须带与宿主 RID 匹配的那一份**：`HdiffTool.Rid` = 当前**进程**架构（32 位宿主 → `win-x86`）。随包覆盖 win-x64 / win-x86 / linux-x64 |
| 缺工具时 | **计划阶段就降级**：`hpatchz` 找不到 → 所有补丁直接计划成完整下载，`reason=patch_tool_missing`，`UpdateResult.Detail` 说明原因。绝不会"先下一份补丁再回落"（那比纯完整下载还费流量） |
