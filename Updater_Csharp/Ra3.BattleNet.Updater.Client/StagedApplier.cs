using System.Diagnostics;
using Ra3.BattleNet.Updater.Share.Models;

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
/// 【必须】本地 manifest 与 ETag **只在本类落地成功之后**才推进（§12.5）：阶段一不写它们。
/// </summary>
public sealed class StagedApplier
{
    private readonly UpdateConfig _cfg;
    private HttpFetcher? _fetcher;

    public StagedApplier(UpdateConfig cfg) => _cfg = cfg;

    public UpdateResult Run(IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
        => RunAsync(progress, ct).GetAwaiter().GetResult();

    public async Task<UpdateResult> RunAsync(IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
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

        using (lockStream)
        {
            try
            {
                using var fetcher = new HttpFetcher(_cfg.MaxConcurrency);
                _fetcher = fetcher;
                return await CoreAsync(progress, ct).ConfigureAwait(false);
            }
            finally
            {
                _fetcher = null;
            }
        }
    }

    private async Task<UpdateResult> CoreAsync(IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var root = Path.GetFullPath(_cfg.RootPath);
        var sw = Stopwatch.StartNew();
        using var log = new UpdateLog(_cfg.ResolveLogPath(),
            $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{Environment.ProcessId}-apply", _cfg.MaxLogBytes);
        log.RunStart(DateTime.UtcNow.ToString("O"));

        var plan = StageLayout.LoadPlan(root);
        if (plan is null)
            return Finish(log, sw, string.Empty, UpdateOutcome.UpToDate, UpdateReasons.None,
                $"没有待提交的暂存更新（{StageLayout.DirName}/{StageLayout.PlanFileName} 不存在）",
                0, 0, 0, 0, string.Empty);

        // 远端清单要它做两件事：验证暂存内容、以及"落地成功后原样写入本地清单"（§3.4）。
        // 优先用阶段一留在缓存里的字节 —— 那样离线也能落地；缓存不在才联网重取（顺手拿到 ETag）。
        var (remoteBytes, remoteHash, httpVersion, etag) = await LoadRemoteManifestAsync(ct).ConfigureAwait(false);
        var knownHash = remoteHash.Length > 0 ? remoteHash : plan.ManifestHash;
        if (remoteBytes is null)
            return Finish(log, sw, knownHash, UpdateOutcome.Failed, UpdateReasons.ManifestUnavailable,
                "取不到远端清单：拒绝落地（本地清单不会被改写，暂存内容原样保留）",
                plan.Actions.Count, 0, 0, 0, httpVersion);

        // 计划是**提示**：判据是"远端清单的字节哈希是否还等于计划里记的那个"（§12.4）。
        if (!string.Equals(remoteHash, plan.ManifestHash, StringComparison.OrdinalIgnoreCase))
            return Finish(log, sw, knownHash, UpdateOutcome.Failed, UpdateReasons.StagedPlanStale,
                $"远端清单已变（计划 {Short(plan.ManifestHash)} / 远端 {Short(remoteHash)}）：本轮不落地，等下一次更新重新规划",
                plan.Actions.Count, 0, 0, 0, httpVersion);

        var remote = RemoteModel(remoteBytes);

        // 静默判据（§12.5）：宿主 PID 退出 + 树内没有进程在跑。超时就什么都不动。
        var quiet = await WaitForQuiescenceAsync(root, remote, progress, ct).ConfigureAwait(false);
        if (!quiet)
            return Finish(log, sw, remoteHash, UpdateOutcome.Failed, UpdateReasons.TreeBusy,
                $"等待 {_cfg.ApplierQuiescenceTimeout.TotalSeconds:F0}s 仍有进程在使用这棵树：本轮不落地",
                plan.Actions.Count, 0, 0, 0, httpVersion);

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
                plan.Actions.Count, 0, 0, 0, httpVersion);

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

            progress?.Report(new UpdateProgress(++done, total, action.RelativePath, UpdateStage.Apply));
        }

        if (failed > 0)
            return Finish(log, sw, remoteHash, UpdateOutcome.Failed, UpdateReasons.IoError,
                "有文件落地失败：本地清单未改写（下次运行会按 §12.6 的表续做）",
                total, skipped, moved, failed, httpVersion);

        // 全部落地成功 —— 到这里才推进本地清单与 ETag（§12.5），然后清掉暂存内容与计划。
        WriteLocalManifest(remoteBytes);
        SaveEtag(etag);
        StageLayout.ClearNew(root);
        StageLayout.DeletePlan(root);

        return Finish(log, sw, remoteHash, UpdateOutcome.Updated, UpdateReasons.None,
            $"已落地 {total} 个动作（其中 {skipped} 个本来就已就位）", total, skipped, moved, 0, httpVersion);
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
        var deadline = DateTime.UtcNow + _cfg.ApplierQuiescenceTimeout;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (HostExited() && !AnyProcessRunningFrom(root, exeNames, self)) return true;
            if (DateTime.UtcNow >= deadline) return false;

            progress?.Report(new UpdateProgress(0, 0, string.Empty, UpdateStage.Check));
            await Task.Delay(_cfg.ApplierPollInterval, ct).ConfigureAwait(false);
        }
    }

    private bool HostExited()
    {
        if (_cfg.WaitForProcessId is not { } pid) return true;
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
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

    private async Task<(byte[]? Bytes, string Hash, string HttpVersion, string? ETag)> LoadRemoteManifestAsync(
        CancellationToken ct)
    {
        var cachePath = Path.Combine(Path.GetFullPath(_cfg.ResolveCacheDir()), "manifest.remote.xml");
        if (Fs.Exists(cachePath))
        {
            var cached = Fs.ReadAllBytes(cachePath);
            return (cached, Hashing.Md5(cached), string.Empty, null);
        }

        var got = await _fetcher!.GetManifestAsync(_cfg.ManifestUrl, null, ct).ConfigureAwait(false);
        if (!got.Ok || got.Content is null) return (null, string.Empty, got.HttpVersion ?? string.Empty, null);

        var bytes = got.Content;
        var hash = Hashing.Md5(bytes);
        Fs.WriteAllBytes(cachePath, bytes);   // 与阶段一一致：缓存里留一份远端原文
        return (bytes, hash, got.HttpVersion ?? string.Empty, got.ETag);
    }

    /// <summary>远端 manifest 的字节已经（或刚刚）落在缓存里；<see cref="ManifestModel"/> 要一个路径，用它即可。</summary>
    private ManifestModel RemoteModel(byte[] bytes)
    {
        var path = Path.Combine(Path.GetFullPath(_cfg.ResolveCacheDir()), "manifest.remote.xml");
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

    private void SaveEtag(string? etag)
    {
        // 用缓存里的清单落地时拿不到 ETag → 不写，下次做一次完整 GET（无害）
        if (string.IsNullOrEmpty(etag)) return;
        Fs.WriteAllText(Path.Combine(Path.GetFullPath(_cfg.ResolveCacheDir()), "manifest.etag"), etag!);
    }

    private void LogFile(UpdateLog log, StagedAction a, string action, int status, string reason, long t0)
    {
        log.File(string.Empty, null, a.Kind == StagedActionKind.Move ? a.MoveFromRelative : null,
            a.Md5, a.RelativePath, action, status, reason, 0,
            (long)Stopwatch.GetElapsedTime(t0).TotalMilliseconds, 0);
    }

    private UpdateResult Finish(UpdateLog log, Stopwatch sw, string manifestHash, UpdateOutcome outcome,
        string reason, string detail, int total, int skipped, int moved, int failed, string httpVersion)
    {
        sw.Stop();
        var payload = _fetcher?.PayloadBytes ?? 0;
        var wire = _fetcher?.WireBytes ?? 0;
        log.Run(manifestHash, total, skipped, moved, 0, 0, failed, 0, (long)sw.Elapsed.TotalMilliseconds,
            outcome.ToString(), _fetcher?.Requests ?? 0, payload, wire);

        return new UpdateResult(outcome, reason, total, skipped, moved, 0, 0, failed, 0, sw.Elapsed, detail,
            httpVersion, payload, wire, _fetcher?.WireSentBytes ?? 0, _fetcher?.WireReceivedBytes ?? 0);
    }

    /// <summary>
    /// 生成"宿主退出后由谁来落地"的命令（§12.5）。**库不自己 spawn**（§4.12：不改变进程生命周期），
    /// 由宿主决定怎么挂：退出路径里启动、计划任务、或一次性开机项。
    /// 【必须】不要重定向 stdio：管道会随宿主退出而失效，applier 写日志就会出错。
    /// </summary>
    public static ProcessStartInfo BuildApplyCommand(string applierExecutable, UpdateConfig cfg)
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
                     ("--manifest-url", cfg.ManifestUrl),
                     ("--local-manifest", cfg.LocalManifestPath),
                     ("--cache-dir", cfg.CacheDir),
                     ("--tools-dir", cfg.ToolsDir),
                     ("--log", cfg.LogPath),
                     ("--wait-for-pid", Environment.ProcessId.ToString()),
                     ("--quiescence-timeout", ((int)cfg.ApplierQuiescenceTimeout.TotalSeconds).ToString()),
                 })
        {
            if (string.IsNullOrEmpty(value)) continue;
            psi.ArgumentList.Add(name);
            psi.ArgumentList.Add(value);
        }

        return psi;
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