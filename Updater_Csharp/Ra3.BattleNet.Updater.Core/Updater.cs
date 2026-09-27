using System.Collections.Concurrent;
using System.Diagnostics;
using Ra3.BattleNet.Updater.Share.Models;
using Ra3.BattleNet.Updater.Share.Utilities;

namespace Ra3.BattleNet.Updater.Core;

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

        var localManifestPath = Path.GetFullPath(_cfg.ResolveLocalManifestPath());
        var cacheDir = Path.GetFullPath(_cfg.ResolveCacheDir());
        Fs.CreateDirectory(cacheDir);
        WriteCacheNotice(cacheDir);

        using var log = new UpdateLog(_cfg.ResolveLogPath(),
            $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{Environment.ProcessId}",
            _cfg.MaxLogBytes);

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

        if (manifest.NotModified)
            return Finish(log, tally, sw, UpdateOutcome.UpToDate, UpdateReasons.None, string.Empty, manifest.HttpVersion ?? string.Empty);

        var remoteBytes = manifest.Content!;
        var remoteHash = Hashing.Md5(remoteBytes);
        tally.ManifestHash = remoteHash;
        Fs.WriteAllBytes(remotePath, remoteBytes);

        // ETag 只能在「本地清单确实等于远端清单」之后才写：
        // 它表达的是"我的本地状态对应哪一版远端清单"。提前写会让下一次运行
        // 凭 If-None-Match 拿到 304，从而误判"已最新"（而本地其实还没更新）。
        if (localHash is not null && string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
        {
            SaveEtag(etagPath, manifest.ETag);
            return Finish(log, tally, sw, UpdateOutcome.UpToDate, UpdateReasons.None, string.Empty, manifest.HttpVersion ?? string.Empty);
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

        var plan = UpdatePlanner.Build(remote, local, _cfg);
        tally.Total = plan.Total;
        tally.Skip = plan.Unchanged;

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

        // 本地清单缺失/损坏时，计划里每个文件都会是「完整下载」—— 那等于把整个产品重下。
        // 正确做法是**退化为按磁盘哈希校验**：命中就跳过。花 CPU，不花带宽（AGENT.md §4.10）。
        // 这个判断现在对所有"本次要动"的文件都生效（见下面的 LooksAlreadyUpdated）。
        var work = plan.Entries.ToList();
        for (var i = 0; i < work.Count; i++)
        {
            var e = work[i];

            if (e.Action == PlanAction.Skip)
            {
                if (!Fs.Exists(e.TargetPath))
                {
                    work[i] = e with { Action = PlanAction.Full };
                    tally.Skip--;
                    continue;
                }

                if (_cfg.VerifyUnchangedFiles && !LooksAlreadyUpdated(e))
                {
                    work[i] = e with { Action = PlanAction.Full };
                    tally.Skip--;
                }

                continue;
            }

            // §4.3 步骤①：目标路径上**已经就是目标内容** → 跳过。
            // 计划只看得见两份 manifest，看不见两件事：
            //   - "上次跑到一半已经把这个文件弄好了"（本设计"有失败就不写本地清单"，
            //     所以一次部分失败的运行必然留下"磁盘比清单新"的状态）；
            //   - "别的程序/用户已经把它换成新版了"。
            // 不查这一下，重跑就会把已经弄好的文件再完整下载一遍 —— 实测 v4→v5 中断后重跑
            // 多下 30%（373 MB vs 一次跑完 286 MB），F6 的"已完成的不重复下载"就不成立。
            // 代价只是对**本次要动的文件**各算一次哈希（未变文件不碰），远小于重下的带宽。
            if (e.Action is PlanAction.Patch or PlanAction.Full && LooksAlreadyUpdated(e))
            {
                work[i] = e with { Action = PlanAction.Skip };
                tally.Skip++;
            }
        }

        var pending = work.Where(e => e.Action != PlanAction.Skip).ToList();
        var totalWork = pending.Count;
        var done = 0;

        async Task ProcessOneAsync(PlanEntry entry)
        {
            var t0 = Stopwatch.GetTimestamp();
            var (action, status, reason, bytes, wire) = await ProcessEntryAsync(entry, fetcher, cacheDir, ct)
                .ConfigureAwait(false);
            var ms = (long)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            Interlocked.Add(ref tally.Bytes, bytes);
            Interlocked.Add(ref tally.Wire, wire);
            switch (action)
            {
                case PlanAction.Move: Interlocked.Increment(ref tally.Move); break;
                case PlanAction.Patch: Interlocked.Increment(ref tally.Patch); break;
                default: Interlocked.Increment(ref tally.Full); break;
            }

            if (status != LogStatus.Ok) Interlocked.Increment(ref tally.Fail);

            log.File(entry.Target.UUID.ToString("N"), entry.PredecessorHash, entry.PredecessorPath,
                entry.Target.MD5, entry.TargetPath, ActionName(action), status, reason, bytes, ms, wire);

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

    private async Task<(PlanAction Action, int Status, string Reason, long Bytes, long Wire)> ProcessEntryAsync(
        PlanEntry entry, HttpFetcher fetcher, string cacheDir, CancellationToken ct)
    {
        switch (entry.Action)
        {
            case PlanAction.Move:
            {
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
                var (ok, reason, bytes, wire) = await TryPatchAsync(entry, fetcher, cacheDir, ct).ConfigureAwait(false);
                if (ok) return (PlanAction.Patch, LogStatus.Ok, UpdateReasons.None, bytes, wire);

                // 补丁失败 → 回落完整下载：把已经为补丁花掉的字节也算进这一行（否则"真花了多少带宽"会少算）
                var fallback = await FullAsync(entry, fetcher, cacheDir, reason, ct).ConfigureAwait(false);
                return fallback with { Wire = fallback.Wire + wire };
            }

            default:
                return await FullAsync(entry, fetcher, cacheDir, UpdateReasons.None, ct).ConfigureAwait(false);
        }
    }

    /// <summary>尝试补丁：GET patches/{old}_{new}.hdiff，404 即回落（§3.3 / §4.3）。</summary>
    private async Task<(bool Ok, string Reason, long Bytes, long Wire)> TryPatchAsync(
        PlanEntry entry, HttpFetcher fetcher, string cacheDir, CancellationToken ct)
    {
        if (entry.PredecessorPath is null || entry.PredecessorHash is null || !Fs.Exists(entry.PredecessorPath))
            return (false, UpdateReasons.NoLocal, 0, 0);

        var patchName = $"{entry.PredecessorHash}_{entry.Target.MD5}.hdiff";
        var patchPath = Path.Combine(cacheDir, patchName);

        long bytes = 0, wire = 0;
        if (!Fs.Exists(patchPath))
        {
            var got = await FetchAsync(fetcher, "patches/" + patchName, patchPath, ct).ConfigureAwait(false);
            if (!got.Ok)
                return (false, got.StatusCode == 404 ? UpdateReasons.NoPatch : UpdateReasons.PatchFailed, 0, got.WireBytes);
            bytes = got.Bytes;
            wire = got.WireBytes;
        }

        var outPath = patchPath + ".out";
        Fs.Delete(outPath);

        if (!await HdiffTool.ApplyAsync(_cfg.ResolveToolsDir(), entry.PredecessorPath, patchPath, outPath, ct)
                .ConfigureAwait(false))
        {
            Fs.Delete(patchPath);
            return (false, UpdateReasons.PatchFailed, bytes, wire);
        }

        if (!string.Equals(
                await Hashing.Md5FileAsync(outPath, ct).ConfigureAwait(false),
                entry.Target.MD5, StringComparison.OrdinalIgnoreCase))
        {
            Fs.Delete(outPath);
            Fs.Delete(patchPath);
            return (false, UpdateReasons.PatchFailed, bytes, wire);
        }

        Fs.Place(outPath, entry.TargetPath);
        return (true, UpdateReasons.None, bytes, wire);
    }

    /// <summary>
    /// 完整下载：<c>GET files/{md5}</c>（§3.2）。
    /// 【必须】下载完要**校验**（§4.3 ⑥），而且校验发生在放到目标路径之前 ——
    /// 这是唯一能挡住"传完了但内容是坏的"（代理返回垃圾、传输被截断…）的一步。
    /// 注意：**不访问** <c>files/{md5}.gz</c> 预压缩旁挂 —— 那条路已按决策移除（见 AGENT.md §4.6），
    /// 服务端仍可生成旁挂（默认关闭），将来要消费它时按规范里的协议重新实现。
    /// </summary>
    private async Task<(PlanAction Action, int Status, string Reason, long Bytes, long Wire)> FullAsync(
        PlanEntry entry, HttpFetcher fetcher, string cacheDir, string reason, CancellationToken ct)
    {
        var blob = Path.Combine(cacheDir, entry.Target.MD5);
        var gate = _blobLocks.GetOrAdd(blob, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);

        long bytes = 0, wire = 0;
        try
        {
            if (!await BlobIsGoodAsync(blob, entry.Target.MD5, ct).ConfigureAwait(false))
            {
                Fs.Delete(blob);
                var got = await FetchAsync(fetcher, "files/" + entry.Target.MD5, blob, ct).ConfigureAwait(false);
                if (!got.Ok)
                    return (PlanAction.Full, LogStatus.RetryExceeded, UpdateReasons.DownloadFailed, 0, got.WireBytes);

                bytes = got.Bytes;
                wire = got.WireBytes;

                if (!await BlobIsGoodAsync(blob, entry.Target.MD5, ct).ConfigureAwait(false))
                {
                    Fs.Delete(blob);
                    return (PlanAction.Full, LogStatus.VerifyFailed, UpdateReasons.VerifyFailed, bytes, wire);
                }
            }

            try
            {
                Fs.Place(blob, entry.TargetPath);
            }
            catch (Exception ex)
            {
                return (PlanAction.Full, LogStatus.IoError, Classify(ex), bytes, wire);
            }
        }
        finally
        {
            gate.Release();
        }

        return (PlanAction.Full, LogStatus.Ok, reason, bytes, wire);
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
        // R 行的 wire 用**会话总计**（含 manifest 本身），这正是"这次更新一共花了多少带宽"
        var wire = _fetcher?.WireBytes ?? 0;
        log.Run(t.ManifestHash, t.Total, t.Skip, t.Move, t.Patch, t.Full, t.Fail, t.Bytes,
            (long)sw.Elapsed.TotalMilliseconds, outcome.ToString(), _fetcher?.Requests ?? 0, wire);

        return new UpdateResult(outcome, reason, t.Total, t.Skip, t.Move, t.Patch, t.Full, t.Fail, t.Bytes,
            sw.Elapsed, detail, httpVersion, wire);
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
        public long Wire;
        public string ManifestHash = string.Empty;
    }
}
