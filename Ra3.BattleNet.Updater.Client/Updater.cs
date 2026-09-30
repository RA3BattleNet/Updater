using System.Collections.Concurrent;
using System.Diagnostics;
using Ra3.BattleNet.Updater.Share.Models;
using Ra3.BattleNet.Updater.Share.Utilities;

namespace Ra3.BattleNet.Updater.Client;

/// <summary>
/// 更新会话。设计契约见 AGENT.md §4。
/// 硬约束：不拥有 UI、不硬编码产品路径、不改变进程生命周期、绝不抛异常（§4.12）。
/// 一个实例同一时刻只跑一个会话。
/// </summary>
public sealed class Updater
{
    private readonly UpdateConfig _cfg;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _blobLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本会话的 HTTP 客户端，仅用于把请求数写进日志（一次只跑一个会话）。</summary>
    private HttpFetcher? _fetcher;

    /// <summary>
    /// 本会话开始时探测一次：**补丁应用工具（hpatchz）能不能用**（每轮重探，工具可能被宿主补上）。
    /// 为 false 时计划阶段就把补丁降级成完整下载 —— 否则会"下一份补丁 → 打不上 → 删掉 → 再下完整文件"，
    /// 比纯完整下载还费流量（32 位宿主没有随包 win-x86 工具时就是这种病态状态）。
    /// </summary>
    private bool _patchToolAvailable = true;

    /// <summary>缺工具导致本轮降级时，写给 <c>UpdateResult.Detail</c> 的一句话（见 Finish）。</summary>
    private string? _patchToolNote;

    /// <summary>
    /// 同一进程里的第几轮更新。一次进程可能跑多轮（用户取消后重跑、宿主反复调用），
    /// 它们共用一个 update.log；run_id 若只到秒 + PID，同秒内的两轮会撞成同一个 id，
    /// 分析脚本就再也分不开这两轮的 F/R 行（I-4）。
    /// </summary>
    private static int _runSeq;






    public Updater(UpdateConfig cfg) => _cfg = cfg;

    public UpdateResult Run(IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
        => RunAsync(progress, ct).GetAwaiter().GetResult();

    /// <summary>
    /// 跑一次更新。同一安装根目录同一时刻只允许一个会话（AGENT.md §4.9）。
    /// 用缓存目录里的**独占文件锁**而不是命名 Mutex：Mutex 有线程亲和性，
    /// 会在 await 之后跨线程释放而失败。
    /// </summary>
    public async Task<UpdateResult> RunAsync(IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
    {
        var cacheDir = Path.GetFullPath(_cfg.ResolveCacheDir());
        Fs.CreateDirectory(cacheDir);

        var lockPath = Path.Combine(cacheDir, "update.lock");
        FileStream lockStream;
        try
        {
            lockStream = new FileStream(Fs.P(lockPath), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return new UpdateResult(UpdateOutcome.Failed, UpdateReasons.AlreadyRunning,
                0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, "已有另一个更新实例在运行");
        }

        using (lockStream)
        {
            return await RunCoreAsync(progress, ct).ConfigureAwait(false);
        }
    }

    private async Task<UpdateResult> RunCoreAsync(IProgress<UpdateProgress>? progress, CancellationToken externalCt)
    {
        // 整体时限（§4.6）：管住"每个请求都没超时、但整轮永远跑不完"的情况。
        using var session = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        session.CancelAfter(_cfg.SessionTimeout);
        var ct = session.Token;

        var sw = Stopwatch.StartNew();
        var tally = new Tally();

        // 补丁工具在不在，**开工前问一次**（§7.1）：缺了就让计划把补丁降级成完整下载，
        // 而不是等下载完补丁、应用失败、删掉补丁之后再回落。
        _patchToolAvailable = HdiffTool.FindPatchTool(_cfg.ResolveToolsDir()) is not null;
        _patchToolNote = null;

        var localManifestPath = Path.GetFullPath(_cfg.ResolveLocalManifestPath());
        var cacheDir = Path.GetFullPath(_cfg.ResolveCacheDir());
        Fs.CreateDirectory(cacheDir);
        WriteCacheNotice(cacheDir);

        using var log = new UpdateLog(_cfg.ResolveLogPath(),
            $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{Environment.ProcessId}-{Interlocked.Increment(ref _runSeq)}",
            _cfg.MaxLogBytes);

        log.RunStart(DateTime.UtcNow.ToString("O"));

        var staged = _cfg.ApplyMode == ApplyMode.Staged;

        // §12.7：同一棵树上两种落地模式互斥。若存在待提交计划却走直接更新，
        // 随后运行的 applier 会拿旧计划覆盖刚由直接模式换好的新文件 —— 必须拒绝。
        var pendingStagedPlan = StageLayout.HasPendingPlan(_cfg.RootPath);

        if (!staged && pendingStagedPlan)
            return Finish(log, tally, sw, UpdateOutcome.Failed, UpdateReasons.PendingStagedApply,
                $"检测到已暂存但未落地的更新（{StageLayout.DirName}/）：请先让宿主退出以完成落地，或删除该目录后重试");

        // 暂存模式：**没有待提交计划时**，本轮开始前把上一轮的备份清掉
        //（§12.7：回退窗口止于"下一次更新开始"）。有待提交计划时不清 ——
        // 那份备份仍是它的回退材料，交给 applier 在落地时清。
        if (staged && !pendingStagedPlan)
            StageLayout.ClearOld(_cfg.RootPath);

        var etagPath = Path.Combine(cacheDir, "manifest.etag");
        var remotePath = Path.Combine(cacheDir, "manifest.remote.xml");

        // 本地 manifest 的**字节**就是版本身份（§3.4）
        string? localHash = Fs.Exists(localManifestPath) ? Hashing.Md5(Fs.ReadAllBytes(localManifestPath)) : null;
        var etag = Fs.Exists(etagPath) ? Fs.ReadAllText(etagPath).Trim() : null;

        using var fetcher = new HttpFetcher(_cfg.MaxConcurrency);
        _fetcher = fetcher;

        progress?.Report(new UpdateProgress(0, 0, string.Empty, UpdateStage.Check));

        var manifest = await fetcher.GetManifestAsync(_cfg.ManifestUrl, localHash is null ? null : etag, ct);
        if (!manifest.Ok)
            return Finish(log, tally, sw, UpdateOutcome.Failed, UpdateReasons.ManifestUnavailable, manifest.Reason, manifest.HttpVersion ?? string.Empty);

        // 304 只说"远端清单没变"，**不说明"我的树就是那一版"**。
        // 降级/覆盖安装、还原备份都会让本地清单落后于 ETag 对应的版本；而缓存默认在安装根之外时，
        // 重装/还原并不会清掉它 —— 旧代码在这里直接返回 UpToDate，于是**永久静默地不再更新**。
        // 所以命中 304 也必须拿缓存里的远端清单，再核一次本地状态。
        byte[] remoteBytes;
        if (manifest.NotModified)
        {
            if (Fs.Exists(remotePath))
            {
                remoteBytes = Fs.ReadAllBytes(remotePath);        // 零网络：远端清单就在缓存里
            }
            else
            {
                // 缓存里没有远端清单（被删 / 缓存不全）→ 304 不可信，无条件重取一次（只有清单）
                manifest = await fetcher.GetManifestAsync(_cfg.ManifestUrl, null, ct);
                if (!manifest.Ok)
                    return Finish(log, tally, sw, UpdateOutcome.Failed, UpdateReasons.ManifestUnavailable, manifest.Reason, manifest.HttpVersion ?? string.Empty);
                if (manifest.NotModified)
                    return Finish(log, tally, sw, UpdateOutcome.Failed, UpdateReasons.ManifestUnavailable,
                        "服务端对无条件请求仍返回 304：无法判断本地是否落后于远端", manifest.HttpVersion ?? string.Empty);
                remoteBytes = manifest.Content!;
                Fs.WriteAllBytes(remotePath, remoteBytes);
            }
        }
        else
        {
            remoteBytes = manifest.Content!;
            Fs.WriteAllBytes(remotePath, remoteBytes);
        }

        var remoteHash = Hashing.Md5(remoteBytes);
        tally.ManifestHash = remoteHash;

        // ETag 只能在「本地清单确实等于远端清单」之后才写：
        // 它表达的是"我的本地状态对应哪一版远端清单"。提前写会让下一次运行
        // 凭 If-None-Match 拿到 304，从而误判"已最新"（而本地其实还没更新）。
        // `--verify-unchanged` 例外：开了它就**不许**走这条捷径 —— 那条路的意义就是逐文件核对磁盘，
        // 否则宿主的"修复资源"按钮在"本来已最新"这个主场景下会空转、却报成功。
        if (localHash is not null
            && string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase)
            && !_cfg.VerifyUnchangedFiles)
        {
            SaveEtag(etagPath, manifest.ETag);
            return Finish(log, tally, sw, UpdateOutcome.UpToDate, UpdateReasons.None, string.Empty, manifest.HttpVersion ?? string.Empty);
        }

        // 暂存模式下目标版本变了 → 旧暂存内容作废（内容寻址，缓存里还在，重下代价≈0）；
        // 不作废就会让 applier 拿着过时的 .new 去落地。版本没变则**续做**（见 AlreadyPlaced）。
        if (staged)
        {
            var pendingPlan = StageLayout.LoadPlan(_cfg.RootPath);
            if (pendingPlan is not null
                && !string.Equals(pendingPlan.ManifestHash, remoteHash, StringComparison.OrdinalIgnoreCase))
                StageLayout.Reset(_cfg.RootPath);
        }

        ManifestModel remote;
        try
        {
            remote = new ManifestModel(remotePath);
        }
        catch (Exception ex)
        {
            return Finish(log, tally, sw, UpdateOutcome.Failed, UpdateReasons.ManifestUnavailable, ex.Message, manifest.HttpVersion ?? string.Empty);
        }

        // manifest 是**远端数据**：由它派生出的相对路径必须落在安装根内（§4.14）。
        // 在动任何东西之前拒绝 —— 一条越界路径就说明这份清单要么坏了、要么是恶意的。
        if (PathSafety.FirstUnsafe(remote.Manifest.Files.Select(f => (string?)f.RelativePath())) is { } unsafePath)
            return Finish(log, tally, sw, UpdateOutcome.Failed, UpdateReasons.PathEscape,
                $"清单里有逃出安装根的路径（例：{unsafePath}）：整个更新拒绝执行",
                manifest.HttpVersion ?? string.Empty);

        ManifestModel? local = null;
        var localCorrupt = false;
        if (localHash is not null)
        {
            try
            {
                local = new ManifestModel(localManifestPath);
            }
            catch
            {
                // 本地清单坏了 —— 属于「本地不可信」，但仍可继续（会大量走完整下载）
                localCorrupt = true;
            }
        }

        // 本地清单**也是数据**：它由上一次写下来，同样可能被篡改、位翻转、或从旧备份还原。
        // 它的相对路径**确实参与落地** —— 规划器用它给出 `Move` 的**改名来源**（`UpdatePlanner.cs` 的
        // `Full(root, same.RelativePath())`），`PlanAction.Move` 会真的把那个路径搬进树里。
        // 所以边界与远端那份一模一样，而且同样必须在**动任何东西之前**（§4.14）。
        // 远端那道在上面，这道是它缺的另一半；解析失败（没有 `local` 对象）时本校验自然跳过。
        if (local is not null
            && PathSafety.FirstUnsafe(local.Manifest.Files.Select(f => (string?)f.RelativePath())) is { } unsafeLocal)
            return Finish(log, tally, sw, UpdateOutcome.Failed, UpdateReasons.PathEscape,
                $"本地清单里有逃出安装根的路径（例：{unsafeLocal}）：整个更新拒绝执行",
                manifest.HttpVersion ?? string.Empty);

        // 没有可信基线（`local == null`：没有本地清单，或它坏了）时**不盲目全量**：
        // 用库自己的口径逐文件算磁盘哈希，合成一份「仅本次规划」的基线（AGENT.md §4.10）。
        // 为什么必须在**规划之前**：工作量保险丝（§4.4）看的是计划里的待下载数，而"没有基线"
        // 会让它把"全部文件"都算成要下载 —— 树明明已经对，却先一步折回宿主整包（§4.4 里那条
        // 实施约束讲的就是它）。合成基线把命中的文件判成 `Skip`，保险丝于是看到**真实**工作量。
        // 【必须】这份基线**不落盘**：唯一允许写本地清单的时机是「逐文件哈希验证通过」（下面那条
        // 全部命中）或「落地成功」（既有路径）。清单是验证的产物，不是假设的产物。
        AdoptedBaseline? adopted;
        try
        {
            adopted = local is null && _cfg.AdoptLocalTreeWhenNoBaseline
                ? await AdoptLocalTreeAsync(remote, progress, ct).ConfigureAwait(false)
                : null;
        }
        catch (OperationCanceledException)
        {
            var detail = externalCt.IsCancellationRequested
                ? "已取消"
                : $"超出整体时限（{_cfg.SessionTimeout}）";
            return Finish(log, tally, sw, UpdateOutcome.Failed, UpdateReasons.IoError, detail);
        }

        var plan = UpdatePlanner.Build(remote, adopted?.Local ?? local, _cfg, _patchToolAvailable);
        tally.Total = plan.Total;
        tally.Skip = plan.Unchanged;

        // 受管文件**全部命中** ⇒ 这棵树是逐文件核过的、就是远端那一版 → 直接收尾（带宽 0）。
        // 【必须】不要让它走完正常流程：暂存模式下会写出一个**空计划**并返回 `Staged`，
        // 宿主于是提示"需要重启"，用户白重启一次而什么都没发生。此时写本地清单（远端原文）+ ETag 就够。
        if (adopted is { AllHit: true })
        {
            var manifestTmp = localManifestPath + ".tmp";
            Fs.WriteAllBytes(manifestTmp, remoteBytes);
            Fs.Place(manifestTmp, localManifestPath);
            SaveEtag(etagPath, manifest.ETag);
            return Finish(log, tally, sw, UpdateOutcome.UpToDate, UpdateReasons.None, string.Empty,
                manifest.HttpVersion ?? string.Empty);
        }

        // 补丁工具不可用时，在 Detail 里留一句（否则用户只会看到"这次怎么全在整包下载"）——
        // 归因的权威位置仍是 F 行的 reason=patch_tool_missing。
        if (!_patchToolAvailable && plan.Entries.Any(e => e.Action == PlanAction.Full && e.Predecessor is not null))
            _patchToolNote = $"补丁工具（hpatchz）在本机不可用（RID={HdiffTool.Rid}，随包平台：{string.Join("/", HdiffTool.ShippedRids)}）：" +
                             "本轮全部按完整下载处理（reason=patch_tool_missing），不会浪费补丁流量。";

        // 资源保护判据（默认关闭）：**不依赖版本号语义**，只用两份 manifest 得出的文件数。
        // 注意这不是「文件多就走全量」——变更文件多恰恰是增量最该发挥作用的场景。
        // 它只是运维可选的保险丝，默认阈值为 0 = 不启用。
        var overFiles = _cfg.FullPackageThresholdFiles > 0
            && plan.ToDownload > _cfg.FullPackageThresholdFiles;
        var overRatio = _cfg.FullPackageThresholdRatio > 0
            && plan.ToDownload >= _cfg.FullPackageRatioMinFiles
            && plan.ToDownload > plan.Total * _cfg.FullPackageThresholdRatio;

        if (overFiles || overRatio)
        {
            return Finish(log, tally, sw, UpdateOutcome.NeedsHostFallback, UpdateReasons.WorkloadTooLarge,
                $"待下载 {plan.ToDownload}/{plan.Total} 个文件，超过配置的保护阈值", manifest.HttpVersion ?? string.Empty);
        }

        if (localCorrupt)
            log.File(string.Empty, null, localManifestPath, remoteHash, null, "full", LogStatus.NeedsFullPackage,
                UpdateReasons.LocalCorrupt, 0, 0);

        // 没有可信基线时，计划里每个文件都会是「完整下载」—— 那等于把整个产品重下。
        // 层一（在规划之前）：`AdoptLocalTreeAsync` 已经按磁盘哈希把命中的文件变成 `Skip`，
        //   所以到这里的条目本来就少了（AGENT.md §4.10）。
        // 层二（就在下面）：对**本次要动**的条目再问一次"落地点上是否已经是目标内容" —— 那是 `AlreadyPlaced`，
        //   它同时承担"上次跑到一半"的续做语义（§4.3 步骤① / §12.5）。
        //   注意别把它和 `LooksAlreadyUpdated` 搞混：后者**只**在 `VerifyUnchangedFiles` 打开时对已经判 `Skip`
        //   的条目复核磁盘，不参与"要不要做这个文件"的决策。
        var work = plan.Entries.ToList();
        var pending = new List<PlanEntry>();

        for (var i = 0; i < work.Count; i++)
        {
            var e = work[i];

            if (e.Action == PlanAction.Skip)
            {
                if (!Fs.Exists(e.TargetPath))
                {
                    e = e with { Action = PlanAction.Full };
                    work[i] = e;
                    tally.Skip--;
                    pending.Add(e);
                    continue;
                }

                if (_cfg.VerifyUnchangedFiles && !LooksAlreadyUpdated(e))
                {
                    e = e with { Action = PlanAction.Full };
                    work[i] = e;
                    tally.Skip--;
                    pending.Add(e);
                }

                continue;
            }

            // 改名条目的前身必须还在；不在就只能整份下。先在这里定性，好让待提交计划记的是**真实意图**。
            if (e.Action == PlanAction.Move && (e.PredecessorPath is null || !Fs.Exists(e.PredecessorPath)))
            {
                e = e with { Action = PlanAction.Full };
                work[i] = e;
            }

            // §4.3 步骤①：目标路径上**已经就是目标内容** → 跳过。
            // 计划只看得见两份 manifest，看不见两件事：
            //   - "上次跑到一半已经把这个文件弄好了"（本设计"有失败就不写本地清单"，
            //     所以一次部分失败的运行必然留下"磁盘比清单新"的状态）；
            //   - "别的程序/用户已经把它换成新版了"。
            // 不查这一下，重跑就会把已经弄好的文件再完整下载一遍 —— 实测 v4→v5 中断后重跑
            // 多下 30%（373 MB vs 一次跑完 286 MB），F6 的"已完成的不重复下载"就不成立。
            // 代价只是对**本次要动的文件**各算一次哈希（未变文件不碰），远小于重下的带宽。
            // 暂存模式下这一步同时就是**续做**：落地点是 UpdaterStage/new，已暂存好的内容会被认出来。
            // 注意这里**不改 work 里的动作** —— 该文件仍然要进待提交计划，
            // 否则"全部已暂存"的那一轮会写出一个空计划，applier 就没事可做了。
            if (e.Action is PlanAction.Patch or PlanAction.Full && AlreadyPlaced(e))
            {
                tally.Skip++;
                continue;
            }

            pending.Add(e);
        }

        // 提交阶段要处理的条目（= 原始计划里非 Skip 的那些，含"本轮已就绪、无需下载"的）。
        // 暂存模式把它写成待提交计划；直接更新模式不用它（文件已经就地换好了）。
        var toLand = work.Where(x => x.Action != PlanAction.Skip).ToList();
        var totalWork = pending.Count;
        var done = 0;

        async Task ProcessOneAsync(PlanEntry entry)
        {
            var t0 = Stopwatch.GetTimestamp();
            var (action, status, reason, bytes, payload) = await ProcessEntryAsync(entry, fetcher, cacheDir, ct)
                .ConfigureAwait(false);
            var ms = (long)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            Interlocked.Add(ref tally.Bytes, bytes);
            Interlocked.Add(ref tally.Payload, payload);
            switch (action)
            {
                case PlanAction.Move: Interlocked.Increment(ref tally.Move); break;
                case PlanAction.Patch: Interlocked.Increment(ref tally.Patch); break;
                default: Interlocked.Increment(ref tally.Full); break;
            }

            if (status != LogStatus.Ok) Interlocked.Increment(ref tally.Fail);

            log.File(entry.Target.UUID.ToString("N"), entry.PredecessorHash, entry.PredecessorPath,
                entry.Target.MD5, entry.TargetPath, ActionName(action), status, reason, bytes, ms, payload);

            var n = Interlocked.Increment(ref done);
            progress?.Report(new UpdateProgress(n, totalWork, entry.Target.FileName, ActionName(action)));
        }

        // 自适应并发（§4.6）：起始 2，成功就 +1 到上限，出现失败就回退一步。
        // 面对受限源站时，这比固定高并发克制得多。
        //
        // 调度用「连续流水线」而不是「一批一等」：始终把在飞文件数填到 target，
        // 每完成一个就补位。批等（Task.WhenAll(wave)）会被一批里最慢的那个文件拖住，
        // 481 个补丁就是几百个空转的栅栏。这里没有栅栏。
        // 起点不高于上限：MaxConcurrency=1 就真的是 1（把上限也抬到起始值会让"限制并发"配置失效）。
        var maxConcurrency = Math.Max(1, _cfg.MaxConcurrency);
        var target = Math.Min(UpdateConfig.StartConcurrency, maxConcurrency);
        var queue = new Queue<PlanEntry>(pending);
        var inFlight = new List<Task>();
        var failuresSeen = 0;

        try
        {
            while (queue.Count > 0 || inFlight.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                while (queue.Count > 0 && inFlight.Count < target)
                    inFlight.Add(ProcessOneAsync(queue.Dequeue()));

                if (inFlight.Count == 0) break;

                var finished = await Task.WhenAny(inFlight).ConfigureAwait(false);
                inFlight.Remove(finished);
                await finished.ConfigureAwait(false);

                var failures = Volatile.Read(ref tally.Fail);
                target = failures > failuresSeen
                    ? Math.Max(UpdateConfig.StartConcurrency, target - 1)
                    : Math.Min(maxConcurrency, target + 1);
                failuresSeen = failures;
            }
        }
        catch (OperationCanceledException)
        {
            var detail = externalCt.IsCancellationRequested
                ? "已取消"
                : $"超出整体时限（{_cfg.SessionTimeout}）";
            return Finish(log, tally, sw, UpdateOutcome.Failed, UpdateReasons.IoError, detail);
        }

        if (tally.Fail == 0 && staged)
        {
            // 暂存模式：**不写本地 manifest、不写 ETag** —— 那两件事挪到 applier 落地成功之后（§12.5）。
            // 提前写会让下一次运行"计划器只比对两份 manifest"从而永远不再处理这些文件；
            // ETag 提前写更会让下一次请求拿到 304、直接返回"已最新"：更新彻底不再发生，而且不报错。
            var actions = toLand
                .Select(e => new StagedAction(
                    e.Target.RelativePath(),
                    e.Target.MD5,
                    e.Action == PlanAction.Move ? StagedActionKind.Move : StagedActionKind.Place,
                    e.Action == PlanAction.Move && e.PredecessorPath is { } src
                        ? Path.GetRelativePath(_cfg.RootPath, src).Replace('\\', '/')
                        : null))
                .OrderBy(a => a.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            StageLayout.SavePlan(_cfg.RootPath, new StagedPlan(remoteHash, actions));
            StageLayout.PruneNewExcept(_cfg.RootPath,
                actions.Where(a => a.Kind == StagedActionKind.Place).Select(a => a.RelativePath));

            return Finish(log, tally, sw, UpdateOutcome.Staged, UpdateReasons.None,
                $"已暂存 {actions.Count} 个动作到 {StageLayout.DirName}/：宿主退出后由 applier 落地，届时才生效",
                manifest.HttpVersion ?? string.Empty);
        }

        if (tally.Fail == 0)
        {
            // 全部成功才写本地 manifest，且**原样字节**（§3.4 / §4.5）
            var tmp = localManifestPath + ".tmp";
            Fs.WriteAllBytes(tmp, remoteBytes);
            Fs.Place(tmp, localManifestPath);
            SaveEtag(etagPath, manifest.ETag);
            return Finish(log, tally, sw, UpdateOutcome.Updated, UpdateReasons.None, string.Empty, manifest.HttpVersion ?? string.Empty);
        }

        // 失败收敛（§4.5）：连完整下载都失败的文件达到 5 个，判定本地状态不可信，交回宿主
        var tolerance = _cfg.EffectiveFailTolerance(totalWork);
        var outcome = tally.Fail >= tolerance ? UpdateOutcome.NeedsHostFallback : UpdateOutcome.Failed;
        var why = tally.Fail >= tolerance ? UpdateReasons.LocalCorrupt : UpdateReasons.DownloadFailed;
        return Finish(log, tally, sw, outcome, why, $"{tally.Fail} 个文件失败");
    }

    /// <summary>
    /// 没有可信基线时，用**磁盘哈希**合成一份「仅本次规划」的基线（AGENT.md §4.10）。
    ///
    /// 口径必须与规划器**完全一致**，所以"哪些远端条目受管"和"相对路径怎么拼"都直接复用
    /// <see cref="UpdatePlanner"/>（`ManagedTargets` / `Full`）—— 客户端自己再抄一遍是这套设计里最容易
    /// 出事的地方：一旦有偏差就会写出**错的基线**，而"清单说完成、文件其实没换"是本设计最怕的静默故障。
    ///
    /// 只做一件事：**命中**的远端条目原样进合成基线（于是规划器把它们判成 `Skip`）。没命中的**不进**
    /// 基线 —— 没有可信前身就不能打补丁，只能完整下载（`no_local` 那条路），顺带也就不可能把两个
    /// 不同文件接在一起。
    ///
    /// 进度按库自己的口径报 `check`（不新增枚举）：宿主能显示"正在核对本地文件 N/M"。
    /// 大树上逐文件回调太密，按 ~100 次封顶抽稀，最后一条一定报。
    /// </summary>
    private async Task<AdoptedBaseline> AdoptLocalTreeAsync(ManifestModel remote,
        IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var root = Path.GetFullPath(_cfg.RootPath);
        var targets = UpdatePlanner.ManagedTargets(remote, _cfg);
        var step = Math.Max(1, targets.Count / 100);
        var synthetic = new ManifestModel(new Version(1, 0, 0), "adopted-from-disk");
        var hits = 0;

        for (var i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var target = targets[i];
            if (await MatchesDiskAsync(UpdatePlanner.Full(root, target.RelativePath()), target.MD5, ct)
                    .ConfigureAwait(false))
            {
                hits++;
                synthetic.Manifest.Files.Add(target);   // 原样引用：规划器只读 UUID / MD5 / 路径
            }

            if (i == targets.Count - 1 || i % step == 0)
                progress?.Report(new UpdateProgress(i + 1, targets.Count, target.FileName, UpdateStage.Check));
        }

        return new AdoptedBaseline(synthetic, targets.Count, hits);
    }

    /// <summary>
    /// 磁盘上这个路径是否**就是**目标内容。读不了（被独占、权限不足、IO 错误）就当作"不是" ——
    /// 这里回答的是"能不能少下一次"，答案不确定时应当继续往下走，让正常流程去**如实报出那个具体原因**。
    /// 取消/超时是例外：那是整轮的中止信号，必须照常往上抛（否则会被静默当成"文件不一致"）。
    /// </summary>
    private static async Task<bool> MatchesDiskAsync(string path, string expectedMd5, CancellationToken ct)
    {
        if (!Fs.Exists(path)) return false;
        try
        {
            return string.Equals(await Hashing.Md5FileAsync(path, ct).ConfigureAwait(false),
                expectedMd5, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>「没有可信基线时合成基线」的结果。只描述**本次规划**，不落盘、不作为版本声明。</summary>
    private sealed record AdoptedBaseline(ManifestModel Local, int Checked, int Hits)
    {
        /// <summary>受管文件全部命中磁盘 ⇒ 这棵树就是远端那一版（逐文件核过）。</summary>
        public bool AllHit => Hits == Checked;
    }

    /// <summary>
    /// 目标路径上是否已经是目标内容（§4.3 步骤①）。
    /// 读不了（文件被独占、权限不足…）就当作"不是" —— 这里回答的是"能不能少下一次"，
    /// 答案不确定时应当继续往下走，让正常流程去**如实报出那个具体原因**
    /// （被占用/IO 错误会由 FullAsync 分类成 file_in_use / io_error）。异常绝不能从这里逃出去（§4.12）。
    /// </summary>
    private static bool LooksAlreadyUpdated(PlanEntry e)
    {
        try
        {
            return Fs.Exists(e.TargetPath)
                && string.Equals(Hashing.Md5File(e.TargetPath), e.Target.MD5, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>本次要落地的东西应该放在哪：直接更新 = 目标路径；暂存更新 = UpdaterStage/new/…（§12.3）。</summary>
    private string PlacePath(PlanEntry e) =>
        _cfg.ApplyMode == ApplyMode.Staged
            ? StageLayout.NewPath(_cfg.RootPath, e.Target.RelativePath())
            : e.TargetPath;

    /// <summary>
    /// **落地点**上是否已经是目标内容。直接更新时等价于 <see cref="LooksAlreadyUpdated"/>；
    /// 暂存更新时问的是 <c>UpdaterStage/new</c> —— 于是"上次跑到一半"天然续做，不重下（§12.5）。
    /// </summary>
    private bool AlreadyPlaced(PlanEntry e)
    {
        var path = PlacePath(e);
        try
        {
            return Fs.Exists(path)
                && string.Equals(Hashing.Md5File(path), e.Target.MD5, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<(PlanAction Action, int Status, string Reason, long Bytes, long Payload)> ProcessEntryAsync(
        PlanEntry entry, HttpFetcher fetcher, string cacheDir, CancellationToken ct)
    {
        switch (entry.Action)
        {
            case PlanAction.Move:
            {
                // 暂存模式：**一个字节都不动树**（§12.2①），这次改名交给 applier 在宿主退出后做（§12.4）。
                // 前身此刻不在（与上面的预判之间有竞态）→ 本轮报失败、不写计划，下一轮重新规划。
                if (_cfg.ApplyMode == ApplyMode.Staged)
                {
                    if (entry.PredecessorPath is { } s && Fs.Exists(s))
                        return (PlanAction.Move, LogStatus.Ok, UpdateReasons.None, 0, 0);

                    return (PlanAction.Move, LogStatus.IoError, UpdateReasons.NoLocal, 0, 0);
                }

                if (entry.PredecessorPath is { } src && Fs.Exists(src))
                {
                    try
                    {
                        Fs.Place(src, entry.TargetPath);
                        return (PlanAction.Move, LogStatus.Ok, UpdateReasons.None, 0, 0);
                    }
                    catch (Exception ex)
                    {
                        return await FullAsync(entry, fetcher, cacheDir, Classify(ex), ct).ConfigureAwait(false);
                    }
                }

                return await FullAsync(entry, fetcher, cacheDir, UpdateReasons.NoLocal, ct).ConfigureAwait(false);
            }

            case PlanAction.Patch:
            {
                var (ok, reason, bytes, payload) = await TryPatchAsync(entry, fetcher, cacheDir, ct).ConfigureAwait(false);
                if (ok) return (PlanAction.Patch, LogStatus.Ok, UpdateReasons.None, bytes, payload);

                // 补丁失败 → 回落完整下载：把已经为补丁花掉的字节也算进这一行（否则"真花了多少带宽"会少算）
                var fallback = await FullAsync(entry, fetcher, cacheDir, reason, ct).ConfigureAwait(false);
                return fallback with { Payload = fallback.Payload + payload };
            }

            default:
                // §4.11：full 行必须能归因。两种来路：
                //   ① 本地没有可用的前身（UUID 对不上、内容索引里也没有）→ no_local；
                //   ② 本轮**缺补丁工具**，计划阶段把本该打补丁的条目降级成了 full → patch_tool_missing。
                // 【2026-09-28】以前只有 ①，于是 32 位宿主（没有 win-x86 hpatchz）每轮都表现为
                // "先下一份补丁白费、再下全量"，而且日志里只留 patch_failed，看不出是缺工具。
                var fullReason = !_patchToolAvailable && entry.Predecessor is not null
                    ? UpdateReasons.PatchToolMissing
                    : UpdateReasons.NoLocal;
                return await FullAsync(entry, fetcher, cacheDir, fullReason, ct).ConfigureAwait(false);
        }
    }

    /// <summary>尝试补丁：GET patches/{old}_{new}.bin，404 即回落（§3.3 / §4.3）。</summary>
    private async Task<(bool Ok, string Reason, long Bytes, long Payload)> TryPatchAsync(
        PlanEntry entry, HttpFetcher fetcher, string cacheDir, CancellationToken ct)
    {
        if (entry.PredecessorPath is null || entry.PredecessorHash is null || !Fs.Exists(entry.PredecessorPath))
            return (false, UpdateReasons.NoLocal, 0, 0);

        var patchName = UpdaterProtocol.PatchFileName(entry.PredecessorHash, entry.Target.MD5);
        var patchPath = Path.Combine(cacheDir, patchName);

        long bytes = 0, payload = 0;
        if (!Fs.Exists(patchPath))
        {
            var got = await FetchAsync(fetcher, UpdaterProtocol.PatchRelativePath(entry.PredecessorHash, entry.Target.MD5), patchPath, ct).ConfigureAwait(false);
            if (!got.Ok)
                return (false, got.StatusCode == 404 ? UpdateReasons.NoPatch : UpdateReasons.PatchFailed, 0, got.PayloadBytes);
            bytes = got.Bytes;
            payload = got.PayloadBytes;
        }

        var outPath = patchPath + ".out";
        Fs.Delete(outPath);

        if (!await HdiffTool.ApplyAsync(_cfg.ResolveToolsDir(), entry.PredecessorPath, patchPath, outPath, ct)
                .ConfigureAwait(false))
        {
            Fs.Delete(patchPath);
            return (false, UpdateReasons.PatchFailed, bytes, payload);
        }

        if (!string.Equals(
                await Hashing.Md5FileAsync(outPath, ct).ConfigureAwait(false),
                entry.Target.MD5, StringComparison.OrdinalIgnoreCase))
        {
            Fs.Delete(outPath);
            Fs.Delete(patchPath);
            return (false, UpdateReasons.PatchFailed, bytes, payload);
        }

        Fs.Place(outPath, PlacePath(entry));
        return (true, UpdateReasons.None, bytes, payload);
    }

    /// <summary>
    /// 完整下载：<c>GET files/{md5}.bin</c>（§3.2）。
    /// 【必须】下载完要**校验**（§4.3 ⑥），而且校验发生在放到目标路径之前 ——
    /// 这是唯一能挡住"传完了但内容是坏的"（代理返回垃圾、传输被截断…）的一步。
    /// 注意：**不访问** <c>files/{md5}.bin.gz</c> 预压缩旁挂 —— 那条路已按决策移除（见 AGENT.md §4.6），
    /// 服务端仍可生成旁挂（默认关闭），将来要消费它时按规范里的协议重新实现。
    /// </summary>
    private async Task<(PlanAction Action, int Status, string Reason, long Bytes, long Payload)> FullAsync(
        PlanEntry entry, HttpFetcher fetcher, string cacheDir, string reason, CancellationToken ct)
    {
        var blob = Path.Combine(cacheDir, entry.Target.MD5);
        var gate = _blobLocks.GetOrAdd(blob, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);

        long bytes = 0, payload = 0;
        try
        {
            if (!await BlobIsGoodAsync(blob, entry.Target.MD5, ct).ConfigureAwait(false))
            {
                Fs.Delete(blob);

                // 校验失败要重试（§2.3）："HTTP 报成功但内容不对"真实存在 ——
                // 中间设备改写、两个进程写同一个 cache、或者源站自己就是坏的。
                // 重下一次的代价远小于把一个坏文件安置进用户目录（§4.3⑥）。
                for (var round = 1; ; round++)
                {
                    var got = await FetchAsync(fetcher, UpdaterProtocol.FullRelativePath(entry.Target.MD5), blob, ct).ConfigureAwait(false);
                    if (!got.Ok)
                        return (PlanAction.Full, LogStatus.RetryExceeded, UpdateReasons.DownloadFailed,
                                bytes, payload + got.PayloadBytes);

                    bytes += got.Bytes;
                    payload += got.PayloadBytes;

                    if (await BlobIsGoodAsync(blob, entry.Target.MD5, ct).ConfigureAwait(false))
                        break;

                    Fs.Delete(blob);
                    if (round >= 2)
                        return (PlanAction.Full, LogStatus.VerifyFailed, UpdateReasons.VerifyFailed, bytes, payload);
                }
            }

            try
            {
                Fs.Place(blob, PlacePath(entry));
            }
            catch (Exception ex)
            {
                return (PlanAction.Full, LogStatus.IoError, Classify(ex), bytes, payload);
            }
        }
        finally
        {
            gate.Release();
        }

        return (PlanAction.Full, LogStatus.Ok, reason, bytes, payload);
    }

    private static async Task<bool> BlobIsGoodAsync(string blob, string expectedMd5, CancellationToken ct) =>
        Fs.Exists(blob)
        && string.Equals(await Hashing.Md5FileAsync(blob, ct).ConfigureAwait(false), expectedMd5, StringComparison.OrdinalIgnoreCase);

    /// <summary>按主地址 + 备用地址顺序取资源（§4.6 多源回退）。404 也继续试下一个源 —— 配了备用源就意味着"同一份内容可能只在其中一个源上"。</summary>
    private async Task<DownloadOutcome> FetchAsync(HttpFetcher fetcher, string relative, string dest, CancellationToken ct)
    {
        var last = new DownloadOutcome(false, 0, 0, "没有可用的基准地址");
        foreach (var baseUrl in BaseUrls())
        {
            last = await fetcher.DownloadAsync(baseUrl + relative, dest, ct).ConfigureAwait(false);
            if (last.Ok) return last;
            if (ct.IsCancellationRequested) return last;
        }

        return last;
    }

    private IEnumerable<string> BaseUrls()
    {
        yield return _cfg.ResolveBaseUrl();
        foreach (var b in _cfg.FallbackBaseUrls)
            yield return b.EndsWith('/') ? b : b + "/";
    }

    private UpdateResult Finish(UpdateLog log, Tally t, Stopwatch sw, UpdateOutcome outcome, string reason, string detail, string httpVersion = "")
    {
        sw.Stop();
        // R 行的 payload 用**会话总计**（含清单正文与错误正文的服务端声明长度）。
        // 注意：它是"客户端可见的载荷"，**不是**真实网线字节（真值要拿边缘出口统计，见 OPEN_ISSUES M-1）。
        // 304 时它的补数正好抵消上面多加的清单大小。

        var payload = _fetcher?.PayloadBytes ?? 0;
        // wire = 本进程真正上网的字节（连接层计数，M-1）。做带宽验收看它，别用 payload。
        var wire = _fetcher?.WireBytes ?? 0;
        log.Run(t.ManifestHash, t.Total, t.Skip, t.Move, t.Patch, t.Full, t.Fail, t.Bytes,
            (long)sw.Elapsed.TotalMilliseconds, outcome.ToString(), _fetcher?.Requests ?? 0, payload, wire);

        // 成功路径本来 Detail 为空；缺补丁工具导致降级时在这里补一句，
        // 让宿主/壳（`--json`）能立刻看出"这轮为什么全在整包下载"。
        var detailOut = detail.Length > 0 ? detail : (_patchToolNote ?? string.Empty);

        return new UpdateResult(outcome, reason, t.Total, t.Skip, t.Move, t.Patch, t.Full, t.Fail, t.Bytes,
            sw.Elapsed, detailOut, httpVersion, payload, wire,
            _fetcher?.WireSentBytes ?? 0, _fetcher?.WireReceivedBytes ?? 0);
    }

    private static void SaveEtag(string etagPath, string? etag)
    {
        if (string.IsNullOrEmpty(etag)) return;
        Fs.WriteAllText(etagPath, etag!);
    }

    private static string ActionName(PlanAction action) => action switch
    {
        PlanAction.Skip => "skip",
        PlanAction.Move => "move",
        PlanAction.Patch => "patch",
        _ => "full",
    };

    private static string Classify(Exception ex) => ex switch
    {
        UnauthorizedAccessException => UpdateReasons.IoError,
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => UpdateReasons.FileInUse,
        IOException => UpdateReasons.IoError,
        _ => UpdateReasons.IoError,
    };

    private static void WriteCacheNotice(string cacheDir)
    {
        var notice = Path.Combine(cacheDir, "请勿将任何重要文件放在此处.txt");
        if (Fs.Exists(notice)) return;
        Fs.WriteAllText(notice,
            "此目录由增量更新程序自动管理，用于存放临时下载的文件与补丁。\n" +
            "可以安全地整体删除；请勿在此放置任何重要文件。\n");
    }

    private sealed class Tally
    {
        public int Total;
        public int Skip;
        public int Move;
        public int Patch;
        public int Full;
        public int Fail;
        public long Bytes;
        public long Payload;
        public string ManifestHash = string.Empty;
    }
}
