using System.Diagnostics;
using Ra3.BattleNet.Updater.Share.Models;
using Ra3.BattleNet.Updater.Share.Utilities;

namespace Ra3.BattleNet.Updater.Client;

/// <summary>
/// 暂存更新的**阶段二 + 阶段三**：把 <c>UpdaterStage/</c> 里已就绪的内容落地，然后收尾
/// （AGENT.md §12.5 / §12.6）。
///
/// 三条铁律：
///   ① **永远改名不覆盖** —— 覆盖会把旧字节抹掉，回退就不可能了；
///   ② **先验证后动手** —— 全部动作先过一遍"能不能落地"的判定，任何一个不满足就整体不动；
///   ③ **每步幂等** —— 状态从"磁盘 + 已知 hash"推导（复用 <see cref="StageRecovery"/>），
///      所以"中途被杀"之后，下次运行本身就是它的恢复过程。
///
/// 【必须】本类是**零网络**的：它只认阶段一留在缓存里的那份远端清单原文（<see cref="ApplierConfig.ManifestFile"/>）。
/// 落地发生在宿主退出之后、无人值守，那条路上不该有"网络抖动导致更新失败"这种失败模式；
/// 而且它要落地的是**已经规划并暂存好的那一版**，不是"线上最新版"。所以配置类型
/// <see cref="ApplierConfig"/> 里**根本没有**清单地址这一项。
///
/// 【必须】本地 manifest **只在本类落地成功之后**才推进（§12.5）：阶段一不写它。
/// 落地阶段**不写 ETag**（本地状态是"会不会再取一次，不花内容字节"，见 §12.5）。
/// </summary>
public sealed class StagedApplier
{
    private readonly ApplierConfig _cfg;

    /// <summary>本轮进度回调。收尾要报一次 <see cref="UpdateStage.Done"/>，而收尾点分散在多个 return 上。</summary>
    private IProgress<UpdateProgress>? _progress;

    public StagedApplier(ApplierConfig cfg) => _cfg = cfg;

    public UpdateResult Run(IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
        => RunAsync(progress, ct).GetAwaiter().GetResult();

    public async Task<UpdateResult> RunAsync(IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        _progress = progress;   // 收尾时要报一次 Done；一个实例只跑一轮（见类注释）
        var cacheDir = Path.GetFullPath(_cfg.ResolveCacheDir());
        Fs.CreateDirectory(cacheDir);
        Fs.CreateDirectory(StageLayout.Root(_cfg.RootPath));

        // 与更新会话共用同一把锁（§4.9）：applier 和"正在跑的一轮更新"不能同时动这棵树
        var lockPath = Path.Combine(cacheDir, "update.lock");
        FileStream lockStream;
        try
        {
            lockStream = new FileStream(Fs.P(lockPath), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return new UpdateResult(UpdateOutcome.Failed, UpdateReasons.AlreadyRunning, 0, 0, 0, 0, 0, 0, 0,
                TimeSpan.Zero, "已有另一个更新实例在运行");
        }

        UpdateResult result;
        using (lockStream)
        {
            result = await CoreAsync(progress, ct).ConfigureAwait(false);
        }

        // 必须**在锁释放之后**才拉起宿主：否则刚起来的宿主第一件事跑更新就会拿到 already_running（§12.5）。
        if (result.Outcome is not UpdateOutcome.Updated) return result;

        if (RestartHostIfRequested() is { } note)
            result = result with { Detail = result.Detail.Length == 0 ? note : result.Detail + "；" + note };

        return result;
    }

    /// <summary>
    /// 落地成功后把宿主拉起来（§12.5）。**只在真正落地成功（<see cref="UpdateOutcome.Updated"/>）时**执行；
    /// 启动失败不影响落地结果，只把原因并进 <c>Detail</c>（不吞错，也不谎报成功）。
    /// 用 <c>UseShellExecute = true</c>：脱离宿主可能存在的 Job Object、并落在交互式桌面上。
    /// </summary>
    private string? RestartHostIfRequested()
    {
        if (!_cfg.RestartAfterApply || string.IsNullOrWhiteSpace(_cfg.RestartExecutable)) return null;

        try
        {
            if (_cfg.RestartDelay > TimeSpan.Zero) Thread.Sleep(_cfg.RestartDelay);

            var psi = new ProcessStartInfo(_cfg.RestartExecutable!) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(_cfg.RestartArguments)) psi.Arguments = _cfg.RestartArguments;
            if (!string.IsNullOrEmpty(_cfg.RestartWorkingDirectory)) psi.WorkingDirectory = _cfg.RestartWorkingDirectory;

            Process.Start(psi);
            return "已拉起宿主";
        }
        catch (Exception ex)
        {
            return "拉起宿主失败（不影响本次落地）：" + ex.Message;
        }
    }

    private async Task<UpdateResult> CoreAsync(IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var root = Path.GetFullPath(_cfg.RootPath);
        var sw = Stopwatch.StartNew();
        using var log = new UpdateLog(_cfg.ResolveLogPath(),
            $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{Environment.ProcessId}-apply", _cfg.MaxLogBytes);
        // §4.8：applier 与阶段一是两个进程、两个配置类型。"两边指向的不是同一处"是**唯一**会真正出事的情形
        // （更新锁与阶段一留下的远端清单都在缓存目录里），所以把实际用到的三样打在**本轮的日志最前面**，
        // 出错时第一眼核对。放在 S 行之前是有意的：它是"这一轮在哪儿干活"的入口信息，不是结果。
        log.Context("root", root);
        log.Context("cacheDir", Path.GetFullPath(_cfg.ResolveCacheDir()));
        log.Context("manifestFile", Path.GetFullPath(_cfg.ResolveManifestFile()));
        log.RunStart(DateTime.UtcNow.ToString("O"));

        var plan = StageLayout.LoadPlan(root);
        if (plan is null)
            return Finish(log, sw, string.Empty, UpdateOutcome.UpToDate, UpdateReasons.None,
                $"没有可用的待提交计划（{StageLayout.DirName}/{StageLayout.PlanFileName} 不存在或读不动）：本轮不落地",
                0, 0, 0, 0);

        // 【必须】输入的**唯一来源**是阶段一留在缓存里的那份远端清单原文（零网络）。
        // 它要做两件事：验证暂存内容、以及"落地成功后原样写入本地清单"（§3.4）。
        // 文件不在 ⇒ 拒绝落地（本地清单不被改写、暂存内容原样保留）—— **不联网重取**：
        // 那会把"落地已暂存的那一版"偷偷变成"落地线上最新版"，而且把无人值守的落地阶段
        // 变成一个会因网络抖动而失败的阶段。
        var (remoteBytes, remoteHash) = LoadRemoteManifest();
        var knownHash = remoteHash.Length > 0 ? remoteHash : plan.ManifestHash;
        if (remoteBytes is null)
            return Finish(log, sw, knownHash, UpdateOutcome.Failed, UpdateReasons.ManifestUnavailable,
                $"取不到远端清单（{_cfg.ResolveManifestFile()} 不在）：拒绝落地（本地清单不会被改写，暂存内容与计划原样保留）" +
                "；下一次更新会话会把它补齐，届时 applier 可以继续落地",
                plan.Actions.Count, 0, 0, 0);

        var remote = RemoteModel(remoteBytes);

        // 清单是远端数据、计划由它派生：路径先过信任边界（§4.14），**再**谈计划新不新 ——
        // 一份带逃逸路径的清单是"坏了或恶意"，不该和"计划过期"混为一谈。
        var unsafePath = PathSafety.FirstUnsafe(
            remote.Manifest.Files.Select(f => (string?)f.RelativePath()).Concat(
                plan.Actions.SelectMany(a => new[] { a.RelativePath, a.MoveFromRelative })));
        if (unsafePath is not null)
            return Finish(log, sw, knownHash, UpdateOutcome.Failed, UpdateReasons.PathEscape,
                $"清单或计划里有逃出安装根的路径（例：{unsafePath}）：拒绝落地",
                plan.Actions.Count, 0, 0, 0);

        // 计划是**提示**：判据是"缓存里那份清单的字节哈希是否还等于计划里记的那个"（§12.4）。
        // 正常流程下两边都出自阶段一，所以这里只会在"缓存被替换/损坏"时触发 —— 保留它作为一致性自检。
        if (!string.Equals(remoteHash, plan.ManifestHash, StringComparison.OrdinalIgnoreCase))
            return Finish(log, sw, knownHash, UpdateOutcome.Failed, UpdateReasons.StagedPlanStale,
                $"缓存里的远端清单与计划对不上（计划 {Short(plan.ManifestHash)} / 缓存 {Short(remoteHash)}）：" +
                "本轮不落地（缓存被替换或损坏了）；下一次更新会重新规划并补齐",
                plan.Actions.Count, 0, 0, 0);

        // 静默判据（§12.5）：宿主 PID 退出 + 树内没有进程在跑。超时就什么都不动。
        var quiet = await WaitForQuiescenceAsync(root, remote, progress, ct).ConfigureAwait(false);
        if (!quiet)
            return Finish(log, sw, remoteHash, UpdateOutcome.Failed, UpdateReasons.TreeBusy,
                $"等待 {_cfg.ApplierQuiescenceTimeout.TotalSeconds:F0}s 仍有进程在使用这棵树：本轮不落地",
                plan.Actions.Count, 0, 0, 0);

        // 先修：崩在两次改名之间 / 暂存内容丢了的，把目标先恢复成"整的"（§12.6）
        foreach (var a in plan.Actions.Where(x => Classify(root, x) == StageFileAction.RestoreBackup))
            RestoreBackup(root, a);

        // 再验：只要有任何一个动作到不了目标，**整体不动**。否则会落出一个新旧混合的树，
        // 而本地清单又被推进 → 那些文件永久不再被处理（这是本设计最怕的静默故障）。
        var blocked = plan.Actions
            .Where(x => Classify(root, x) is StageFileAction.Refetch or StageFileAction.RestoreBackup)
            .ToList();
        if (blocked.Count > 0)
            return Finish(log, sw, remoteHash, UpdateOutcome.Failed, UpdateReasons.StagedContentMissing,
                $"{blocked.Count} 个动作的暂存内容缺失或已损坏（例：{blocked[0].RelativePath}）：本轮不落地，等下一次更新补齐",
                plan.Actions.Count, 0, 0, 0);

        // 上一轮的备份到这一刻才清（回退窗口止于"本次落地开始"，§12.7）
        StageLayout.ClearOld(root);

        // 提交：逐个改名；入口（*.exe）排最后（§12.5）
        var order = StageRecovery.OrderForCommit(plan.Actions.Select(x => x.RelativePath)).ToList();
        var byRel = plan.Actions.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        int done = 0, moved = 0, skipped = 0, failed = 0, total = plan.Actions.Count;

        foreach (var rel in order)
        {
            ct.ThrowIfCancellationRequested();
            var action = byRel[rel];
            var t0 = Stopwatch.GetTimestamp();
            var verdict = Classify(root, action);

            if (verdict == StageFileAction.Skip)
            {
                skipped++;
                LogFile(log, action, "skip", LogStatus.Ok, UpdateReasons.None, t0);
                continue;
            }

            // 【事实】在**动手之前**就报：宿主据此显示"正在写 xxx"。
            // （历史实现只在成功之后报，且 Skip 与失败当次都不报 —— 事实是缺的。）
            progress?.Report(new UpdateProgress(done, total, action.RelativePath, UpdateStage.Apply));

            try
            {
                if (action.Kind == StagedActionKind.Move) CommitMove(root, action);
                else CommitPlace(root, action);

                if (action.Kind == StagedActionKind.Move) moved++;
                LogFile(log, action, action.Kind == StagedActionKind.Move ? "move" : "place",
                    LogStatus.Ok, UpdateReasons.None, t0);
            }
            catch (Exception ex)
            {
                // 落地失败：停止改树，**不推进本地清单**；下次运行按 §12.6 的表续做。
                failed++;
                LogFile(log, action, "place", LogStatus.IoError, Classify(ex), t0);
                break;
            }

            done++;
        }

        if (failed > 0)
            return Finish(log, sw, remoteHash, UpdateOutcome.Failed, UpdateReasons.IoError,
                "有文件落地失败：本地清单未改写（下次运行会按 §12.6 的表续做）",
                total, skipped, moved, failed);

        // 全部落地成功 —— 到这里才推进本地清单（§12.5），然后清掉暂存内容与计划。
        // 【必须】不写 ETag：它只服务于"If-None-Match 那次清单请求"，落地阶段既然不联网，
        // 就没有 ETag 可写（缓存里的旧 ETag 属于**上一版**，更不能当成本次的结果写下去）。
        // 唯一的后果是"落地之后第一次更新会话多取一遍清单正文"（约 185 KB gzip），
        // 那一次会话自己会补上 ETag，随即收敛。
        WriteLocalManifest(remoteBytes);
        StageLayout.ClearNew(root);
        StageLayout.DeletePlan(root);

        return Finish(log, sw, remoteHash, UpdateOutcome.Updated, UpdateReasons.None,
            $"已落地 {total} 个动作（其中 {skipped} 个本来就已就位）", total, skipped, moved, 0);
    }

    // ------------------------------------------------------------------ 静默判据

    /// <summary>
    /// 等"宿主退出 + 树内没有进程在跑"，两个条件共用同一个时限（§12.5）：
    /// 【必须】按 manifest 里的 exe 名做进程名扫描，命中后再比对可执行文件路径是否落在树内；
    /// 【禁止】全量枚举进程并逐个读路径（实测 405 个进程 420ms/轮，太重）；
    /// 【必须】排除 applier **自身**，否则自死锁。
    /// </summary>
    private async Task<bool> WaitForQuiescenceAsync(string root, ManifestModel remote,
        IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var exeNames = remote.Manifest.Files
            .Where(f => f.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileNameWithoutExtension(f.FileName))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var self = Environment.ProcessId;
        var started = DateTime.UtcNow;
        var timeout = _cfg.ApplierQuiescenceTimeout;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (HostExited() && !AnyProcessRunningFrom(root, exeNames, self)) return true;
            var elapsed = DateTime.UtcNow - started;
            if (elapsed >= timeout) return false;

            // 【事实】等静默可能很长（时限默认 10 分钟），所以它是一个**可分辨的阶段**，
            // 并且带上"已等多久 / 时限"——宿主据此自己显示"正在等待其他程序退出… 已 12s / 600s"。
            progress?.Report(new UpdateProgress(0, 0, string.Empty, UpdateStage.Wait,
                string.Empty, elapsed, timeout));
            await Task.Delay(_cfg.ApplierPollInterval, ct).ConfigureAwait(false);
        }
    }

    private bool HostExited()
    {
        if (_cfg.WaitForProcessId is not { } pid) return true;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return true;

            // PID 会被系统复用：只认 PID 会把"抢到同一个 PID 的无关进程"当成宿主还在，白等到超时。
            // 所以还要比对宿主自己交下来的身份 —— 名字挡掉绝大多数；**启动时刻**才是唯一实例标识，
            // 连"用户又启动了一次同名程序"也挡得住。两个值由 BuildApplyCommand 自动填，宿主不用管。
            if (_cfg.WaitForProcessName is { Length: > 0 } hostName &&
                !string.Equals(p.ProcessName, hostName, StringComparison.OrdinalIgnoreCase))
                return true;

            if (_cfg.WaitForProcessStartTicks is { } hostTicks)
            {
                try { if (p.StartTime.Ticks != hostTicks) return true; }
                catch { /* 读不到启动时刻（权限）：名字已经比过了，就认为它还是宿主 */ }
            }

            return false;
        }
        catch
        {
            return true;   // 已经不在（或读不到）：真正的判据是下面的树内进程扫描
        }
    }

    private static bool AnyProcessRunningFrom(string root, List<string> exeNames, int self)
    {
        if (exeNames.Count == 0) return false;

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        foreach (var name in exeNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (p.Id != self && p.MainModule?.FileName is { } file
                        && file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // 读不到路径（别的用户/权限不足）→ 不算命中。真正的保护是"逐文件校验 + 改名本身的可行域"。
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ 落地动作

    /// <summary>目标的磁盘状态 → 动作（§12.6 那张表；纯函数在 <see cref="StageRecovery"/>）。</summary>
    private static StageFileAction Classify(string root, StagedAction a)
    {
        var target = Full(root, a.RelativePath);
        var targetIsNew = HashIs(target, a.Md5);

        if (a.Kind == StagedActionKind.Move)
        {
            if (targetIsNew) return StageFileAction.Skip;
            var src = a.MoveFromRelative is null ? null : Full(root, a.MoveFromRelative);
            if (HashIs(src, a.Md5)) return StageFileAction.Commit;
            return Fs.Exists(StageLayout.OldPath(root, a.RelativePath))
                ? StageFileAction.RestoreBackup
                : StageFileAction.Refetch;
        }

        return StageRecovery.Decide(StageRecovery.Classify(
            Fs.Exists(target), targetIsNew,
            Fs.Exists(StageLayout.NewPath(root, a.RelativePath)),
            Fs.Exists(StageLayout.OldPath(root, a.RelativePath))));
    }

    /// <summary>把暂存内容改名就位；旧文件先改名进备份（**永不覆盖**，否则没法回退）。</summary>
    private static void CommitPlace(string root, StagedAction a)
    {
        var staged = StageLayout.NewPath(root, a.RelativePath);
        if (!HashIs(staged, a.Md5)) throw new IOException("暂存内容缺失或哈希不符：" + a.RelativePath);

        BackUpTarget(root, a);
        var target = Full(root, a.RelativePath);
        EnsureParent(target);
        Fs.Move(staged, target, overwrite: false);
    }

    /// <summary>树内改名（内容不变、只有路径变，§12.4）。</summary>
    private static void CommitMove(string root, StagedAction a)
    {
        var src = a.MoveFromRelative is null ? null : Full(root, a.MoveFromRelative);
        if (src is null || !HashIs(src, a.Md5)) throw new IOException("改名来源缺失或哈希不符：" + a.MoveFromRelative);

        BackUpTarget(root, a);
        var target = Full(root, a.RelativePath);
        EnsureParent(target);
        Fs.Move(src, target, overwrite: false);
    }

    private static void BackUpTarget(string root, StagedAction a)
    {
        var target = Full(root, a.RelativePath);
        if (!Fs.Exists(target)) return;
        var backup = StageLayout.OldPath(root, a.RelativePath);
        EnsureParent(backup);
        Fs.Move(target, backup, overwrite: true);   // 同名旧备份（上一轮残留）直接顶掉
    }

    /// <summary>回退：把备份改名回目标（§12.6 的 RestoreBackup）。</summary>
    private static void RestoreBackup(string root, StagedAction a)
    {
        var backup = StageLayout.OldPath(root, a.RelativePath);
        if (!Fs.Exists(backup)) return;
        var target = Full(root, a.RelativePath);
        if (Fs.Exists(target)) Fs.Delete(target);
        EnsureParent(target);
        Fs.Move(backup, target, overwrite: false);
    }

    // ------------------------------------------------------------------ 清单与日志

    /// <summary>
    /// 读阶段一留在缓存里的**远端清单原文**。**不联网**：文件不在就返回 <c>null</c>，
    /// 调用方据此拒绝落地（`manifest_unavailable`）。
    /// </summary>
    private (byte[]? Bytes, string Hash) LoadRemoteManifest()
    {
        var path = Path.GetFullPath(_cfg.ResolveManifestFile());
        if (!Fs.Exists(path)) return (null, string.Empty);
        var bytes = Fs.ReadAllBytes(path);
        return (bytes, Hashing.Md5(bytes));
    }

    /// <summary>
    /// 交给 <see cref="ManifestModel"/>（它要一个路径）。原地写回同一份字节是幂等的：
    /// 那个路径**就是**它的来源，写回去只是把"读到的内容"再落一次，不会改内容。
    /// </summary>
    private ManifestModel RemoteModel(byte[] bytes)
    {
        var path = Path.GetFullPath(_cfg.ResolveManifestFile());
        Fs.WriteAllBytes(path, bytes);
        return new ManifestModel(path);
    }

    private void WriteLocalManifest(byte[] remoteBytes)
    {
        // 本地清单的**字节**就是版本身份（§3.4）：原样写远端字节
        var local = Path.GetFullPath(_cfg.ResolveLocalManifestPath());
        var tmp = local + ".tmp";
        Fs.WriteAllBytes(tmp, remoteBytes);
        Fs.Place(tmp, local);
    }

    private void LogFile(UpdateLog log, StagedAction a, string action, int status, string reason, long t0)
    {
        log.File(string.Empty, null, a.Kind == StagedActionKind.Move ? a.MoveFromRelative : null,
            a.Md5, a.RelativePath, action, status, reason, 0,
            (long)Stopwatch.GetElapsedTime(t0).TotalMilliseconds, 0);
    }

    private UpdateResult Finish(UpdateLog log, Stopwatch sw, string manifestHash, UpdateOutcome outcome,
        string reason, string detail, int total, int skipped, int moved, int failed)
    {
        sw.Stop();
        // 零网络：requests / payload / wire 一律 0（列数与列含义**不变**，见 §4.11 —— 分析脚本按列读）。
        log.Run(manifestHash, total, skipped, moved, 0, 0, failed, 0, (long)sw.Elapsed.TotalMilliseconds,
            outcome.ToString(), 0, 0, 0);

        // 【必须】把**结论**也写进日志（用已有的 C 行型，不动任何既有列）。
        // 会话级失败（tree_busy / manifest_unavailable / path_escape / staged_plan_stale /
        // staged_content_missing）以前**在日志里一个字都没有** —— 宿主自己 exe 当 applier 时它能把
        // Reason/Detail 弹给用户，但排查时用户只会把日志发过来，那条路不能是断的。
        log.Context("result", outcome.ToString());
        if (reason.Length > 0) log.Context("reason", OneLine(reason));
        if (detail.Length > 0) log.Context("detail", OneLine(detail));

        _progress?.Report(new UpdateProgress(total, total, string.Empty, UpdateStage.Done));

        return new UpdateResult(outcome, reason, total, skipped, moved, 0, 0, failed, 0, sw.Elapsed, detail,
            string.Empty, 0, 0, 0, 0);
    }

    /// <summary>日志是 TAB 分列的一行一记录：值里出现 TAB/换行会把列数搞乱，统一压成空格。</summary>
    private static string OneLine(string s) =>
        s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>
    /// 找一个可用的 applier 可执行文件（AGENT.md §12.5）。
    ///
    /// **布局是库定的，所以由库来解析**：宿主只该写一句 <c>FindDefaultApplierExe()</c>，
    /// 而不是各自拼一遍"程序目录/applier_bin/&lt;rid&gt;/…" —— 那种拼法迟早会跟布局漂移，
    /// 而漂移的后果是"宿主退出之后才发现没人来落地"。
    ///
    /// 查找顺序（与 <c>HdiffTool.Find</c> 的约定一致）：
    /// <c>工具目录</c> → <c>工具目录/{rid}</c> → <c>程序目录</c> → <c>程序目录/tools[/{rid}]</c> →
    /// <c>程序目录/applier_bin/{rid}</c> → <c>程序目录/applier_bin</c> → PATH。
    /// 返回 <c>null</c> 表示"这台机器上找不到 applier"（宿主据此决定：交回整包 / 报错）。
    /// </summary>
    public static string? FindDefaultApplierExe(string? toolsDir = null)
    {
        // 与 HdiffTool 同一套 RID 口径；文件名不带扩展名的平台（linux/macOS）也一样
        var exeName = OperatingSystem.IsWindows() ? "Client.Applier.exe" : "Client.Applier";
        var rid = HdiffTool.Rid;
        var baseDir = AppContext.BaseDirectory;

        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(toolsDir))
        {
            dirs.Add(toolsDir!);
            dirs.Add(Path.Combine(toolsDir!, rid));
        }

        dirs.Add(baseDir);
        dirs.Add(Path.Combine(baseDir, "tools"));
        dirs.Add(Path.Combine(baseDir, "tools", rid));
        dirs.Add(Path.Combine(baseDir, "applier_bin", rid));   // ← 随包携带的位置
        dirs.Add(Path.Combine(baseDir, "applier_bin"));

        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, exeName);
            if (Fs.Exists(candidate)) return candidate;
        }

        // PATH 兜底（与 HdiffTool 同款；找不到就是找不到，不抛）
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir.Trim(), exeName);
                if (Fs.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 生成"宿主退出后由谁来落地"的命令（§12.5）。**库不自己 spawn**（§4.12：不改变进程生命周期），
    /// 由宿主决定怎么挂：退出路径里启动、计划任务、或一次性开机项。
    /// 【必须】不要重定向 stdio：管道会随宿主退出而失效，applier 写日志就会出错。
    /// </summary>
    public static ProcessStartInfo BuildApplyCommand(string applierExecutable, ApplierConfig cfg)
    {
        var psi = new ProcessStartInfo(Path.GetFullPath(applierExecutable))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("--apply");
        foreach (var (name, value) in new (string, string?)[]
                 {
                     ("--root", Path.GetFullPath(cfg.RootPath)),
                     // 【必须】没有 --manifest-url / --tools-dir / 并发与备用地址：落地阶段不联网，也不下内容。
                     // 它唯一的输入是阶段一留在缓存里的那份清单原文（默认路径由两边共用的默认值求出）。
                     ("--manifest-file", cfg.ManifestFile),
                     ("--local-manifest", cfg.LocalManifestPath),
                     ("--cache-dir", cfg.CacheDir),
                     ("--log", cfg.LogPath),
                     ("--wait-for-pid", Environment.ProcessId.ToString()),
                     ("--wait-for-name", SelfProcessName()),
                     ("--wait-for-start", SelfStartTicks()),
                     // 只有宿主显式打开开关才带这些；打开后宿主自己的 exe 与原始参数**原文**由这里自动填
                     ("--restart", _RestartExe(cfg)),
                     ("--restart-args", _RestartArgs(cfg)),
                     ("--restart-cwd", _RestartCwd(cfg)),
                     ("--restart-delay", cfg.RestartAfterApply ? ((int)cfg.RestartDelay.TotalSeconds).ToString() : null),
                     ("--quiescence-timeout", ((int)cfg.ApplierQuiescenceTimeout.TotalSeconds).ToString()),
                     ("--poll-seconds", ((int)cfg.ApplierPollInterval.TotalSeconds).ToString()),
                 })
        {
            if (string.IsNullOrEmpty(value)) continue;
            psi.ArgumentList.Add(name);
            psi.ArgumentList.Add(value);
        }

        return psi;
    }

    private static string? _RestartExe(ApplierConfig cfg) =>
        cfg.RestartAfterApply ? (cfg.RestartExecutable ?? Environment.ProcessPath) : null;

    private static string? _RestartArgs(ApplierConfig cfg) =>
        cfg.RestartAfterApply ? (cfg.RestartArguments ?? SelfCommandLineTail()) : null;

    private static string? _RestartCwd(ApplierConfig cfg) =>
        cfg.RestartAfterApply ? (cfg.RestartWorkingDirectory ?? Environment.CurrentDirectory) : null;

    /// <summary>
    /// 宿主原始命令行里**去掉 exe 那一段之后的原文** —— 引号原封不动，我们不做任何重新解释。
    /// 开头既可能是带引号的路径，也可能是裸路径，两种都剥掉。
    /// </summary>
    private static string? SelfCommandLineTail()
    {
        var line = Environment.CommandLine;
        if (string.IsNullOrEmpty(line)) return null;

        int cut;
        if (line[0] == '"')
        {
            cut = line.IndexOf('"', 1);
            if (cut < 0) return null;
            cut++;
        }
        else
        {
            cut = line.IndexOf(' ');
            if (cut < 0) return null;
        }

        var tail = line[cut..].Trim();
        return tail.Length == 0 ? null : tail;
    }

    // 「我是谁」由宿主这一侧填好交下去 —— PID 会被复用，光凭 PID 认不准（见 HostExited）。
    private static string? SelfProcessName()
    {
        try { using var p = Process.GetCurrentProcess(); return p.ProcessName; }
        catch { return null; }
    }

    private static string? SelfStartTicks()
    {
        try { using var p = Process.GetCurrentProcess(); return p.StartTime.Ticks.ToString(); }
        catch { return null; }
    }

    // ------------------------------------------------------------------ 小工具

    private static string Full(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static void EnsureParent(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Fs.CreateDirectory(dir);
    }

    private static bool HashIs(string? path, string expected)
    {
        if (path is null || !Fs.Exists(path)) return false;
        try
        {
            return string.Equals(Hashing.Md5File(path), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string Classify(Exception ex) => ex switch
    {
        UnauthorizedAccessException => UpdateReasons.IoError,
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => UpdateReasons.FileInUse,
        _ => UpdateReasons.IoError,
    };

    private static string Short(string hash) => hash.Length <= 8 ? hash : hash[..8];
}