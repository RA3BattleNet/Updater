# Ra3.BattleNet.Updater.Client.Applier —— 落地进程

客户端自更新的**落地入口**：宿主退出之后，把阶段一暂存好的那一版换上去（`AGENT.md` §12.5 阶段二/三）。
它**只做这一件事** —— 没有窗口、不联网、不打补丁、不接受更新会话那一套参数（那些是 `Client.CLI` 的事）。

它落地的是阶段一规划好的那一版；**顺带**还会按"两份清单的差集"把**上游已删除**的文件清掉
（同样是改名进 `UpdaterStage/old/`，判据来自清单事实而不是计划）。`--exclude-dirs` 这类参数由
`StagedApplier.BuildApplyCommand` 自动带上，宿主不用管；**旧 applier 不认识新参数时会忽略它
（"只是不删"），其余照常落地** —— 这是"随包 applier 可能比库旧"这条设计的既定行为。

> **谁用它**：宿主（或宿主的启动器）在**退出之前**把它挂起来。C# 宿主也可以让**自己的 exe** 干这件事
> （`StagedApplier` 是库里的公共类型），那样就不需要本 exe —— 但会把宿主整套 UI 依赖带进落地进程，
> 而落地阶段正在改名树里的文件（宿主文档 §A.2 讲的正是这个 blast radius）。

## 它怎么被交付

`applier_bin/{RID}/Client.Applier.exe` —— **Native AOT 编译的原生可执行文件**（单文件、没有旁路 dll），
由 `Share` 的 `Content` 声明分发：**引用客户端库的宿主，输出目录里自动就有它**，宿主不需要做任何部署。

- **目标机不需要 .NET 运行时**。宿主自己做 self-contained 也**不够** —— applier 是**独立进程**
  （它要等宿主退出才动手），继承不到宿主的自包含运行时，所以它本身必须是原生产物。
- 体积：`win-x64` 约 6.5 MB、`win-x86` 约 5.6 MB、`linux-x64` 约 7.5 MB（合计约 19 MB，随宿主产出走）。
- 宿主用 `StagedApplier.FindDefaultApplierExe()` 让库自己去找（别各自拼路径）；
- 它是清单里的**受管文件**，会随更新一起被替换（**旧 applier 落新 applier**）；
- 随包**覆盖 `HdiffTool.ShippedRids` 的每一个平台**（`win-x64` / `win-x86` / `linux-x64`）。
  漏掉一个平台的后果是：那类宿主上 `FindDefaultApplierExe()` 只返回 `null`（**自更新直接不可用**），
  而且只有运行时才发现 —— 所以有 `ApplierPackagingTests.ApplierCoversEveryShippedRid` 这条**策略测试**钉着；
  若某平台确实决定暂不带，必须同时改那条测试并写明原因。
- ⚠ 它是**签入仓库的构建产物**：改了 `Client.Applier` / `Client` / `Share` 的源码之后必须跑
  `refresh-applier.ps1`（仓库根）。忘了刷新由守卫测试 `ApplierPackagingTests.CheckedInApplier_MatchesCurrentSources`
  抓成红灯 —— 否则会出现"代码里有修复、随包发的是旧 exe"，测试还全绿。
  - `pwsh -File refresh-applier.ps1` → 按 `HdiffTool.ShippedRids` **全刷（AOT）**；
  - `-Rids win-x64` 只刷一个（策略测试会红，属预期）；`-NoLinux` 跳过 `linux-*`；
  - `-Fdd` 出**框架依赖单文件**：只给没装 C++ 工具链的机器临时调试用。签入仓库的必须是 AOT ——
    策略测试 `ApplierPackagingTests.CheckedInApplier_IsNativeAot` 会拦住（读记录里的 `Mode`）。
- **构建期的记录不随包**：`SourcesHash` / `Mode` / `Rids` / 每个 RID 的字节数都在
  `Ra3.BattleNet.Updater.Share/applier_build.json`。`applier_bin/` 整目录随宿主产出走，
  所以那里**只放产物本身**（放了记录就等于把开发机路径带进用户安装树）。

## 为什么是 Native AOT / 怎么构建

| | 框架依赖单文件 | **Native AOT（现在）** |
|---|---|---|
| 体积 / RID | 约 0.35 MB | 约 5.6–7.5 MB |
| 目标机需要 .NET | **要** | **不要** |
| 启动 | JIT | 原生代码，无 JIT |
| 构建工具链 | 无 | Windows 需 MSVC；Linux 需 clang |

```powershell
# Windows（本机出）
dotnet publish Ra3.BattleNet.Updater.Client.Applier -c Release -r win-x64 `
  --self-contained true -p:PublishAot=true
# Linux AOT **不能跨 OS 编译**（Cross-OS native compilation is not supported）⇒ 在 Linux / WSL 里执行同一命令；
# refresh-applier.ps1 默认走 WSL（-WslDistro 指定发行版）
```

两个构建期的坑（脚本里已经处理）：

- Debian 上 `/usr/bin/llvm-objcopy` 常是指向未安装 `lib/llvm-19/` 的**坏符号链接**，AOT 收尾 strip 会报
  `llvm-objcopy : error : No such file or directory`（编译其实已完成）。脚本用 `-p:ObjCopyName=objcopy`
  让 GNU objcopy 干这一步，产物一致（已核对：stripped ELF）。
- AOT 产物旁边会生成几十 MB 的 `.pdb`/符号文件：**不要签入**（脚本只拷可执行文件本身）。

## AOT 对库的影响（已经处理）

applier 是个独立进程，但**它跑的就是库的代码**（`StagedApplier` + Client + Share）。所以 AOT 之前先要把库
变成 AOT/裁剪兼容，否则会出现"能编译、运行期静默不工作"：

- 所有 JSON 走 **source generation**（`plan.json` 与结果 JSON 的上下文在 `Client/JsonContexts.cs`）。
  改之前实测过：AOT 产物对着一个合法 `plan.json` 报"没有可用的待提交计划"（反射式 JSON 在 AOT 下被禁，
  而 `LoadPlan` 的 catch-all 把它吞成"没有计划"）——**落地静默不生效**。
- `Client` / `Share` / `Client.Applier` 都开了 `<IsAotCompatible>true</IsAotCompatible>` ⇒ 分析器零告警，
  以后再塞反射式 JSON 会被拦成构建告警。
- 对**宿主**同样有效的结论：宿主自己走 `PublishAot`（或 `PublishTrimmed`）时，库不会再成为障碍。

## 宿主怎么接（三步）

```csharp
// ① 阶段一：暂存（宿主还在跑）
var cfg = new UpdateConfig { RootPath = installDir, ManifestUrl = manifestUrl, ApplyMode = ApplyMode.Staged };
var r = new Updater(cfg).Run(progress);

// ② 需要重启时：**先挂 applier，再退出**（它会等我们的 PID，所以现在挂不会打架）
if (r.PendingRestart)
{
    var applierCfg = new ApplierConfig { RootPath = installDir };
    Process.Start(StagedApplier.BuildApplyCommand(StagedApplier.FindDefaultApplierExe()!, applierCfg));
    RequestShutdown("update staged");     // 交给你自己的主线程去关
}

// ③ 用户点关闭 → 进程真的退出 → applier 落地（窗口/提示是你的事，库只给事实字段）
```
要点：**不要等 applier 结束**（它等的就是宿主退出，互等即死锁）；applier 一启动就持有更新锁，
之后**不要**再开更新会话；别把它放进带 `KILL_ON_JOB_CLOSE` 的 Job Object（细节见 [`../USAGE.md`](../USAGE.md) §2.8）。

## 用法

```
Client.Applier.exe --root <安装目录> [--cache-dir <目录>] [--manifest-file <路径>]
                   [--wait-for-pid <PID>] [--wait-for-name <名字>] [--wait-for-start <ticks>]
                   [--quiescence-timeout <秒>] [--poll-seconds <秒>]
                   [--restart <exe>] [--restart-args <原文>] [--restart-cwd <目录>] [--restart-delay <秒>]
```

宿主一般不用手写这些参数：`StagedApplier.BuildApplyCommand(exe, applierConfig)` 会生成它们
（并把"我是谁"的身份三件套自动填好）。**不认识的参数会被忽略**（向前兼容：现场这个 exe 可能还是旧的，
而参数是新版库生成的）—— 这就是库里的 `ApplierConfig.FromArgs(args, UnknownArgPolicy.Ignore)`：
**解析实现只有一份**，`Client.CLI --apply` 用的是同一个函数的 `UnknownArgPolicy.Strict`（白名单之外一律拒）。

退出码：`0` 已落地 / 本来就没东西要落地 ｜ `1` 没落地（原因见 `update.log` 的 `C` 行）｜ `2` 参数错误。

## 它**不做**什么（都是故意的）

- **不画窗口**：窗口归宿主。本 exe 只把事实报出去 —— `IProgress<UpdateProgress>` 的阶段标识、计数、文件名、
  动作名（`Action`）、以及 `wait` 阶段的已过时长/时限（字段表见 [`../USAGE.md`](../USAGE.md) §2.2），
  宿主拿它自己算进度条、状态、超时。
- **不联网**：输入只有阶段一留在缓存里的那份清单原文；它不在就**拒绝落地**，绝不联网重取
  （落地的是"已经规划并暂存好的那一版"，不是"线上最新版"）。
- **不打印结果**：stdout 不是本 exe 的契约；机器可读的结论在 `update.log`
  （`C` 行 `result`/`reason`/`detail`，以及 `R` 行的计数）。