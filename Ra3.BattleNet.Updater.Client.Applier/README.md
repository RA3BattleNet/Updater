# Ra3.BattleNet.Updater.Client.Applier —— 落地进程

客户端自更新的**落地入口**：宿主退出之后，把阶段一暂存好的那一版换上去（`AGENT.md` §12.5 阶段二/三）。
它**只做这一件事** —— 没有窗口、不联网、不打补丁、不接受更新会话那一套参数（那些是 `Client.CLI` 的事）。

> **谁用它**：宿主（或宿主的启动器）在**退出之前**把它挂起来。C# 宿主也可以让**自己的 exe** 干这件事
> （`StagedApplier` 是库里的公共类型），那样就不需要本 exe —— 但会把宿主整套 UI 依赖带进落地进程，
> 而落地阶段正在改名树里的文件（宿主文档 §A.2 讲的正是这个 blast radius）。

## 它怎么被交付

`applier_bin/{RID}/Client.Applier.exe`（**单文件、框架依赖**）由 `Share` 的 `Content` 声明分发：
**引用客户端库的宿主，输出目录里自动就有它**，宿主不需要做任何部署。

- 宿主用 `StagedApplier.FindDefaultApplierExe()` 让库自己去找（别各自拼路径）；
- 它是清单里的**受管文件**，会随更新一起被替换（**旧 applier 落新 applier**）；
- 随包**覆盖 `HdiffTool.ShippedRids` 的每一个平台**（`win-x64` / `win-x86` / `linux-x64`，共约 0.8 MB）。
  漏掉一个平台的后果是：那类宿主上 `FindDefaultApplierExe()` 只返回 `null`（**自更新直接不可用**），
  而且只有运行时才发现 —— 所以有 `ApplierPackagingTests.ApplierCoversEveryShippedRid` 这条**策略测试**钉着；
  若某平台确实决定暂不带，必须同时改那条测试并写明原因。
- ⚠ 它是**签入仓库的构建产物**：改了 `Client.Applier` / `Client` / `Share` 的源码之后必须跑
  `refresh-applier.ps1`（仓库根）。忘了刷新由守卫测试 `ApplierPackagingTests.CheckedInApplier_MatchesCurrentSources`
  抓成红灯 —— 否则会出现"代码里有修复、随包发的是旧 exe"，测试还全绿。
  - `pwsh -File refresh-applier.ps1` → 按 `HdiffTool.ShippedRids` 全刷；
    `-Rids win-x64,win-x86` 只刷指定的那几个（会让上面的策略测试变红，属预期）。
- **构建期的记录不随包**：源码指纹 + RID 列表在 `Ra3.BattleNet.Updater.Share/applier_build.json`。
  `applier_bin/` 整目录随宿主产出走，所以那里**只放产物本身**（放了记录就等于把开发机路径带进用户安装树）。

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
而参数是新版库生成的）。解析实现只有一份 —— 在库里的 `ApplierConfig.FromArgs`。

退出码：`0` 已落地 / 本来就没东西要落地 ｜ `1` 没落地（原因见 `update.log` 的 `C` 行）｜ `2` 参数错误。

## 它**不做**什么（都是故意的）

- **不画窗口**：窗口归宿主。本 exe 只把事实报出去（`IProgress<UpdateProgress>`：阶段 / 计数 / 文件名 /
  等待已过时长与时限），宿主拿它自己算进度条、状态、超时。
- **不联网**：输入只有阶段一留在缓存里的那份清单原文；它不在就**拒绝落地**，绝不联网重取
  （落地的是"已经规划并暂存好的那一版"，不是"线上最新版"）。
- **不打印结果**：stdout 不是本 exe 的契约；机器可读的结论在 `update.log`
  （`C` 行 `result`/`reason`/`detail`，以及 `R` 行的计数）。