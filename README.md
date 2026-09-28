# RA3BattleNet Updater

文件级**增量更新**系统（C# / .NET 10）。服务端只产出**静态文件树**（可整体交给 CDN），
客户端按内容寻址、只下载变化的部分；拿不到增量时干净回落完整下载。

> **三句话说明白它是什么**
> 1. 它是**一个客户端库 + 一条发布流水线**，不是开箱即用的启动器 —— UI、时机、退出都由宿主（游戏客户端 / 启动器）决定。
> 2. 宿主侧接入通常只有**一个 switch**：`new Updater(cfg).Run()` → 按 `UpdateResult.Outcome` 分支。
> 3. 要替换**宿主自己的** exe/dll（自更新）时必须用**暂存模式**：库只把内容暂存好，宿主退出后由独立进程 **applier** 落地。

## 我该按什么顺序看

| 顺序 | 看什么 | 为什么 |
|---|---|---|
| 1 | 本文的「关键设计」+「现状与必读」 | 先建立心智模型，别急着抄代码 |
| 2 | `USAGE.md` §2 | **接入方只需要这一章**：怎么引用、最小用法、返回值、配置、宿主集成 |
| 3 | 同上 §2.8 | 自更新 / 转交 applier 的**全部规矩与坑**（不支持的场景、宿主义务、可选代重启） |
| 4 | `Ra3.BattleNet.Updater.Client/Updater.cs`、`StagedApplier.cs` | 想知道"它到底怎么做的"再进代码 |
| 5 | `USAGE.md` §3 / §5 | 发布侧怎么生成产物、怎么本地跑通一遍 |
| 6 | `XmlGenerator/README.md`、`Server.PatchGenerator/README.md` | 发布参数与取舍细节（UUID 关联规则、hdiffz 参数） |

## 关键设计

- **内容寻址、无索引**：地址由哈希直接推导 —— `files/{md5}.bin`、`patches/{old}_{new}.bin`。
- **清单即版本身份**：`manifest.xml` 的字节与本地保存的那份比对，一次请求就能判断"要不要更新"（命中 304 时 0 字节）。
- **文件身份 = UUID**：路径变了也认得出是同一个文件，于是改名也能打补丁、甚至 0 下载。
- **失败不致命**：补丁缺失/打不上 → 静默回落完整下载；失败到阈值 → 交回宿主走整包；库不抛异常、不弹 UI。
- **可观测**：每轮写 `update.log`（S/F/R 三种行），结构化结果里给内容字节、payload 与**真正上网的字节（wire）**。
- **自更新可行（两种落地路线）**：默认 `InPlace` 就地替换；`ApplyMode.Staged` 把"换文件"推到**宿主退出之后**，由独立 applier **只做改名**提交 —— 阶段一一个字节都不动现有树，落地阶段 0 下载、失败则整树不动。

## 现状与必读（**接入前务必看**）

**已经验证过的**：内容寻址协议与清单格式、增量 / 补丁 / 纯改名 / 回落的正确性、
中断续做、暂存更新的两个阶段（含 Cloudflare R2 真机端到端）、宿主身份判定、路径信任边界。
测试怎么跑、验收覆盖到哪些条目，见 `USAGE.md` §6。

**还没验证 / 明确不做的**：

| 事项 | 现状 |
|---|---|
| **真宿主集成** | **未验证**。库与壳各自都测过，但"宿主退出前挂 applier → 退出 → 落地 → 拉起宿主"整条链路只在文档与单元级测试里覆盖过 —— 接入方必须自己在真宿主上跑一遍 |
| **删除集**（上游删掉的文件） | **不做**。上游删掉的文件会**留在盘上**；若残留的旧 dll/exe 会影响加载，需要定期整包重装或另行实现 |
| 非 Windows | **未验证**。改名提交的语义只在 NTFS 上量过（`linux-x64` 工具随包，但整套没在 Linux/macOS 上跑过） |
| 提权 / Program Files | 库**自己绝不提权、不弹 UAC**。宿主提权则整条链路都提权；宿主不提权则**安装根必须对当前用户可写**（阶段一就要往 `<root>/UpdaterStage/` 写） |
| CI | **不做**。回归靠人工跑全套（见 §6），没有 CI 配置也不需要 |

**接入前最容易踩的七条**（详情在 `USAGE.md` §2.8）：

1. **测试与验收必须用 NTFS**（落地语义依赖 NTFS 行为）；测试临时目录用环境变量 `UPDATER_TEST_TMP` 指到 NTFS 盘。
2. **暂存模式是 opt-in**（`ApplyMode.Staged`），默认是就地更新。
3. 宿主**必须在自己的进程退出之前**把 applier 创建出来 —— 它是独立进程，宿主一退出就再没有人能挂它。
4. **不要在更新线程里 `Environment.Exit`**；退出动作放在主线程的关闭序列里（否则跳过宿主自己的清理）。
5. 宿主若处在带 `KILL_ON_JOB_CLOSE` 的 Job Object 里（浏览器内核那类壳），applier 会被**连带杀掉** —— **本库不处理**，得换"创建者"（计划任务 / WMI / `explorer.exe`）。
6. **不要等 applier 结束**（它等的是宿主退出，互等就是死锁）；想知道结果就重启后再跑一次 updater，`UpToDate` 即落地成功。
7. `manifest.xml` 是**远端数据**：里面的路径一律过信任边界（越界则整个更新被拒），而宿主可能提权运行 —— 别把清单当可信输入。

## 仓库结构

| 路径 | 角色 |
|---|---|
| `Ra3.BattleNet.Updater.Share` | 共享层：协议模型（清单格式）、哈希、外部工具封装 |
| `Ra3.BattleNet.Updater.Client` | **客户端更新引擎**（无 UI、无产品耦合）—— 客户端只需引用它 |
| `Ra3.BattleNet.Updater.Server` | **服务端发布逻辑**：清单生成 + 生成期自检 + 补丁生成 |
| `Ra3.BattleNet.Updater.{XmlGenerator,Server.PatchGenerator}` | 发布流水线的两个壳 |
| `Ra3.BattleNet.Updater.Client.CLI` | 独立进程壳：跑一次更新 / 落地（`--apply`）；宿主不是 C# 时用 |
| `Ra3.BattleNet.Updater.Tests` | 单元 + 端到端测试（自带反射跑器，原因见 §6） |

约定：**核心逻辑 = 无后缀的库项目**（`Share` / `Client` / `Server`），**可执行壳 = 按角色命名**。
`Share` 被两侧共用；客户端库与服务端库**互不依赖**，各自只依赖 `Share`。

## 快速开始

```powershell
# 构建
dotnet build Ra3.BattleNet.sln -c Release

# 测试临时目录：**必须是 NTFS**（落地语义依赖共享模式 / 只读属性 / 长路径）
$env:UPDATER_TEST_TMP = 'H:\TEST\upd-tests'
dotnet run --project Ra3.BattleNet.Updater.Tests -c Release            # 全套
dotnet run --project Ra3.BattleNet.Updater.Tests -c Release Staged     # 只跑名字含 Staged 的
```

完整的两版生成 → 发布 → 客户端更新示例见 `USAGE.md` §5。

## 文档地图

| 文档 | 管什么 |
|---|---|
| `USAGE.md` | **接入方主文档**：客户端引用与用法、宿主集成（§2.8）、发布流水线、协议、测试、部署与 CDN 要求 |
| `Ra3.BattleNet.Updater.XmlGenerator/README.md` | 清单生成壳：UUID 四条来路与自动关联规则、自检报告怎么读 |
| `Ra3.BattleNet.Updater.Server.PatchGenerator/README.md` | 补丁生成壳：hdiffz 参数取舍、`--compress-files` 为什么默认关 |
| `THIRD-PARTY.md` | 随包第三方二进制（HDiffPatch / libdivsufsort）的许可、来源与更新步骤 |

