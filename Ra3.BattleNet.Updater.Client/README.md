# Ra3.BattleNet.Updater.Client —— 客户端更新引擎（内部导览）

> 面向**要改这个库的人**。只想"用它"的话看 [`../USAGE.md`](../USAGE.md) §2（接入）
> 与 §2.8（宿主集成 / 自更新 / applier）。

## 它是什么 / 谁用（一句话）

宿主（游戏客户端 / 启动器）引用本库 → `new Updater(cfg).Run()` 拿到 `UpdateResult` → 按 `Outcome` 分支走。
要替换宿主**自己的** exe/dll（自更新）就用暂存模式：阶段一只暂存，宿主退出后由 `StagedApplier` 落地。

## 公共接口（写宿主时只用碰这四组）

| 类型 | 用途 |
|---|---|
| `Updater` + `UpdateConfig` | 跑一轮（阶段一）。`Run(progress, ct)` / `RunAsync(...)`；**绝不抛异常、不弹 UI、不退出进程** |
| `UpdateResult` | 结果：`Outcome` / `Reason` / `Detail` + 计数 + 三个字节口径；`Applied`、`PendingRestart`、`ToJson()`（一行 JSON 契约，字段只增不改） |
| `StagedApplier` + `ApplierConfig` | 落地（阶段二/三）。**零网络** —— `ApplierConfig` 里没有清单地址；`BuildApplyCommand(exe, applierCfg)` 生成"宿主退出后该跑什么"的命令 |
| `UpdateProgress` | 进度：`Current` / `Total` / `FileName` / `Stage`（`UpdateStage.{Check,Move,Patch,Download,Done,Apply}`） |

## 最小示例

```csharp
var cfg = new UpdateConfig { RootPath = installDir, ManifestUrl = manifestUrl };   // 就地更新
var r = new Updater(cfg).Run();
switch (r.Outcome)
{
    case UpdateOutcome.UpToDate:
    case UpdateOutcome.Updated:           break;                          // 直接启动
    case UpdateOutcome.Staged:            /* 仅暂存模式会出现：提示重启 */   break;
    case UpdateOutcome.NeedsHostFallback: /* 走你自己的整包逻辑 */          break;
    case UpdateOutcome.Failed:            /* 提示重试；r.Detail 里有原因 */ break;
}
```

自更新（`ApplyMode.Staged`）、applier 的挂法与全部义务见 [`../USAGE.md`](../USAGE.md) §2.8。

## 模块地图（按这个顺序读）

| 文件 | 干什么 |
|---|---|
| `Updater.cs` | **一次更新会话的主流程**（阶段一）：取清单 → 计划 → 下载/打补丁 → 写盘 → 收尾；也是暂存模式阶段一的入口 |
| `UpdatePlanner.cs` | 由「远端清单 + 本地清单 + 磁盘真实哈希」算出动作表（`skip`/`move`/`patch`/`full`）；排除目录与工作量保险丝也在这 |
| `HttpFetcher.cs` | 并发与滑动窗口、Range 续传（`.part`）、`If-None-Match`/304、多源回落、停滞超时 |
| `UpdateConfig.cs` | 全部公共配置（`init` 属性）；缓存/日志默认路径的推导也在这 |
| `UpdateResult.cs` | 结果类型 + `UpdateReasons` 原因码 + **`ToJson()`（一行 JSON 契约）** |
| `UpdateLog.cs` / `UpdateProgress.cs` | 日志（TAB 分行，**格式是契约**；`S`/`F`/`R` 三种行 + applier 写的 `C` 上下文行）与进度回调 |
| `Fs.cs` | 长路径安全的文件操作（自动加 `\\?\`） |
| `StageLayout.cs` | 暂存区布局：`UpdaterStage/{new,old}/`、`plan.json` 的读写与清理 |
| `StagedApplier.cs` | **阶段二 + 三**：宿主退出后把暂存内容落地（只做改名、先修后验、落地才推进清单）。**零网络**：配置是 `ApplierConfig`，它没有清单地址 —— 输入只有阶段一留在缓存里的那份清单原文 |
| `ApplierConfig.cs` | **落地阶段的配置**（与 `UpdateConfig` 彼此独立、不派生；类型层面没有 URL） |
| `InstallPaths.cs` | 缓存/日志默认路径与**安装根指纹**的唯一实现 —— 阶段一与 applier 必须求出逐字相同的结果（锁按缓存目录定位） |
| `StageRecovery.cs` | **纯函数**：由「磁盘 + 已知哈希」判定每个文件该跳过/续做/回退，无 IO、好测 |
| `../Ra3.BattleNet.Updater.Share/` | 协议模型（`ManifestModel`）、路径信任边界（`PathSafety`）、哈希与外部工具（`HdiffTool`） |

## 关键机制（改之前先知道）

- **计划不是决策**：每次运行都重新从「远端清单 + 本地清单 + 磁盘真实哈希」判定，
  所以中断后重跑天然幂等，**不依赖任何进度文件**。
- **304 不是结论**：命中 `If-None-Match` 只说明"远端清单没变"，**不说明"我的树就是那一版"** ——
  仍必须用缓存里的远端清单核一次本地清单。否则降级/覆盖安装、还原备份会让它**永久静默地**判"已最新"
  （缓存默认在安装根之外，重装不会清）。
- **本地清单只在全部成功之后写**；暂存模式更严：本地清单**只在落地成功之后**写
  （提前写会让下次请求拿到 304 → 更新静默不再发生）。落地阶段**不写 ETag**（它只服务于
  "下次清单请求能不能 304"）；后果只是落地后第一次会话多取一遍清单，那一次自己会补上。
- **落地阶段零网络**：`StagedApplier` 的类型是 `ApplierConfig`，**没有清单地址这一项**。
  它只读阶段一留在缓存里的远端清单原文（不在 ⇒ `manifest_unavailable` 拒绝落地，不联网重取）。
- **路径信任边界**：清单是**远端数据**，由它派生的相对路径必须先过 `PathSafety`
  （越界 → `path_escape`，整个更新拒绝执行）。**本地清单走同一道** —— 它同样可能被篡改、位翻转、
  或从旧备份还原，而规划器会拿它的路径当 `Move` 的**改名来源**（越界 = 把根外的文件搬进树）。
- **没有基线也不盲目全量**：`local == null`（清单不存在或损坏）且 `AdoptLocalTreeWhenNoBaseline` 开着时，
  `Updater.AdoptLocalTreeAsync` 用**规划器的同一口径**（`UpdatePlanner.ManagedTargets` / `Full`）逐文件核对磁盘哈希，
  命中的进一份**不落盘**的合成基线 → 计划里它们是 `Skip`。全部命中就直接写清单（远端原文）+ ETag 并返回
  `UpToDate`（否则暂存模式会写出空计划、骗用户重启一次）。**它必须在规划之前**：保险丝（§4.4）
  看的是计划里的待下载数，没有基线时那个数是"全部文件"。
  没命中的**不试补丁**（没有可信前身），只能完整下载。
- **并发**：滑动窗口 + 自适应（默认上限 4）；`MaxConcurrency=1` 时**严格为 1**（有测试钉着）。
- **工作量保险丝**：待更新文件数超阈值 → `NeedsHostFallback`，交回宿主走整包（默认关闭）。
- **暂存三阶段**：① 只暂存（不动树、不写清单）② 宿主退出后 applier 落地（等宿主 PID + 树静默，**只改名不覆盖**）
  ③ 收尾（清 `new/`；`old/` 留到下一轮开始）。
- **认宿主**：`(PID, 进程名, 启动时刻)` 三元组 —— PID 会被系统复用，只认 PID 会白等到超时。

## 改这里的注意事项

- 动 `UpdateLog` 的列名/顺序 → 那是契约，**只允许往行尾追加列或新增行型**（`C` 行就是这么加的）。
- 动 `UpdateResult.ToJson()` → 字段**只增不改**；`Applied` / `PendingRestart` 是独立进程壳判断
  "要不要重启"的唯一信号。
- 动落地语义 → 先读 `StageRecovery` 的判定表，别绕开"**永远改名不覆盖**"和"**每步幂等**"。
- 改完必须做两件事：**干净 clone 构建**（[`../USAGE.md`](../USAGE.md) §6.1）+ 全套测试。