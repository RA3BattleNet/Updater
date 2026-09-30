# RA3BattleNet Updater

文件级**增量更新**系统（C# / .NET 10）：服务端只产出**静态文件树**（可整体交给 CDN），
客户端按内容寻址、只下载变化的部分；拿不到增量时干净回落完整下载。

> 1. 它是**一个客户端侧和服务端侧库 + 一条发布流水线**，不是开箱即用的启动器 —— UI、时机、退出都由宿主（游戏客户端 / 启动器）决定。
> 2. 宿主侧接入通常只有**一个 switch**：`new Updater(cfg).Run()` → 按 `UpdateResult.Outcome` 分支。
> 3. 要替换**宿主自己的** exe/dll（自更新）时必须用**暂存模式**：库只把内容暂存好，宿主退出后由独立进程 **applier** 落地。

核心设计、现状与已知限制见 [`USAGE.md`](USAGE.md) §0；本文件只负责**导航**。

## 从哪开始

| 我想做的事 | 去看 |
|---|---|
| 把更新接进宿主（C# 或非 C# 都行） | [`USAGE.md`](USAGE.md) §2 —— **§2.8 是宿主集成与 applier 的全部规矩** |
| 搭服务端发布流水线（生成清单 / 补丁） | [`USAGE.md`](USAGE.md) §3，参数细节见两个壳的 README |
| 了解协议与清单格式（要自己实现一端时读） | [`USAGE.md`](USAGE.md) §4 |
| 本地把它跑通一遍 | [`USAGE.md`](USAGE.md) §5 |
| 跑测试 / 交付前自检 | [`USAGE.md`](USAGE.md) §6（含 **§6.1 交付前必做的干净 clone 构建**） |
| 读 / 改**客户端引擎**内部 | [`Ra3.BattleNet.Updater.Client/README.md`](Ra3.BattleNet.Updater.Client/README.md) |
| 读 / 改**共享层**（协议模型、路径边界、外部工具） | [`Ra3.BattleNet.Updater.Share/README.md`](Ra3.BattleNet.Updater.Share/README.md) |
| 读 / 改**服务端发布逻辑** | [`Ra3.BattleNet.Updater.Server/README.md`](Ra3.BattleNet.Updater.Server/README.md) |
| 读 / 改**清单生成壳**（UUID 关联规则） | [`Ra3.BattleNet.Updater.XmlGenerator/README.md`](Ra3.BattleNet.Updater.XmlGenerator/README.md) |
| 读 / 改**补丁生成壳**（hdiffz 参数取舍） | [`Ra3.BattleNet.Updater.Server.PatchGenerator/README.md`](Ra3.BattleNet.Updater.Server.PatchGenerator/README.md) |
| 读 / 改**独立进程壳**（命令与退出码） | [`Ra3.BattleNet.Updater.Client.CLI/README.md`](Ra3.BattleNet.Updater.Client.CLI/README.md) |
| 加测试 / 看懂测试怎么组织的 | [`Ra3.BattleNet.Updater.Tests/README.md`](Ra3.BattleNet.Updater.Tests/README.md) |
| 看随包第三方二进制与许可 | [`THIRD-PARTY.md`](THIRD-PARTY.md) |

## 仓库结构

| 路径 | 角色 |
|---|---|
| `Ra3.BattleNet.Updater.Share` | 共享层：协议模型（清单格式）、路径信任边界、哈希、外部工具封装 |
| `Ra3.BattleNet.Updater.Client` | **客户端更新引擎**（无 UI、无产品耦合）—— 客户端只需引用它 |
| `Ra3.BattleNet.Updater.Server` | **服务端发布逻辑**：清单生成 + 生成期自检 + 补丁生成 |
| `Ra3.BattleNet.Updater.XmlGenerator` | 壳：生成 `manifest.xml`（含自检报告） |
| `Ra3.BattleNet.Updater.Server.PatchGenerator` | 壳：生成 `patches/` 与 `files/` |
| `Ra3.BattleNet.Updater.Client.CLI` | 壳：独立进程跑一次更新 / 落地（`--apply`）——给人、脚本与测试用 |
| `Ra3.BattleNet.Updater.Client.Applier` | 壳：**只做落地**的入口；随客户端库分发（引用库的宿主自动带上），是清单里的受管文件所以能被更新 |
| `Ra3.BattleNet.Updater.Tests` | 单元 + 端到端测试（自带反射跑器，原因见它的 README） |

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

完整的两版生成 → 发布 → 客户端更新示例见 [`USAGE.md`](USAGE.md) §5。

## 当前状态（**接入前扫一眼**）

- **已验证**：协议与清单、增量 / 补丁 / 纯改名 / 回落的正确性、中断续做、
  暂存更新的两个阶段（含 Cloudflare R2 真机端到端）、宿主身份判定、路径信任边界。
- **未验证 / 明确不做**：真宿主集成未验证 · 删除集不做（上游删掉的文件会留在盘上）·
  非 Windows 未验证 · 库不提权（宿主不提权时要求安装根可写）· 没有 CI（人工跑全套）。
  细节与取舍见 [`USAGE.md`](USAGE.md) §0.2。
- **最容易踩的三个坑**（全部条目在 [`USAGE.md`](USAGE.md) §2.8）：
  1. 替换宿主自己的文件 → 必须 `ApplyMode.Staged`，并在**宿主退出之前**把 applier 挂起来；
  2. **不要等 applier 结束**（它等的就是宿主退出，互等即死锁）；退出动作放在主线程的关闭序列里；
  3. 测试与验收**必须 NTFS**；安装根必须对当前用户可写（阶段一就要写 `UpdaterStage/`）。
