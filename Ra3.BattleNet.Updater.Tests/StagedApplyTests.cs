using System.Text;
using Ra3.BattleNet.Updater.Client;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// 暂存更新（AGENT.md §12）的落地语义。三组：
/// ① 状态判定表与提交顺序（纯函数，输入空间钉死）；
/// ② 这套设计所依赖的文件系统事实（改名 vs 覆盖/删除、共享模式、只读属性）；
/// ③ 一个**参考实现**跑完整的三阶段往返与各强杀点，验证「任何阶段被杀都能被判定并收敛」。
///
/// 为什么把参考实现放在测试里：产品 applier 还没写，而 §12.6 的失败语义恰恰是整条链路里
/// 最容易写错、也最难事后发现的部分（写错的症状是「某次异常退出后更新永远不生效」）。
/// 先让设计变成可执行的断言。产品 applier 落地后，这份参考实现应改为驱动真实 applier。
///
/// 范围说明：本组不覆盖「新版本里消失的文件」（删除集，§12.10 明确暂不做），
/// 也不覆盖 ACL / EFS / 硬链接等元数据保真（同为 §12.10 的非目标）。
/// </summary>
public class StagedApplyTests
{
    private static readonly Dictionary<string, string> V1 = new()
    {
        ["app.exe"] = "APP-v1",
        ["bin/a.dll"] = "A-v1",
        ["bin/b.dll"] = "B-unchanged",
        ["data/x.dat"] = "X-v1",
    };

    private static readonly Dictionary<string, string> V2 = new()
    {
        ["app.exe"] = "APP-v2",
        ["bin/a.dll"] = "A-v2",
        ["bin/b.dll"] = "B-unchanged",     // 未变：既不该被暂存，也不该被提交
        ["data/x.dat"] = "X-v2",
        ["bin/new.dll"] = "NEW",           // 新增：提交时没有备份，回退时应被删掉
    };

    // ============================== ① 纯函数 ==============================

    /// <summary>§12.6 的判定表逐行钉死。判定顺序即优先级：「已经好了」必须压过一切。</summary>
    [Fact]
    public void Classify_PinsTheRecoveryTable()
    {
        // 目标已是新内容 → 已完成（暂存/备份在不在都不影响）
        Assert.Equal(StageFileState.Applied, StageRecovery.Classify(true, true, true, false));
        Assert.Equal(StageFileState.Applied, StageRecovery.Classify(true, true, false, true));
        Assert.Equal(StageFileState.Applied, StageRecovery.Classify(true, true, false, false));
        // 目标缺失 + 暂存在 + 备份在 → 崩在两次改名之间
        Assert.Equal(StageFileState.Interrupted, StageRecovery.Classify(false, false, true, true));
        // 目标缺失 + 暂存没了 + 备份在 → 只能回退
        Assert.Equal(StageFileState.Rollback, StageRecovery.Classify(false, false, false, true));
        // 目标在（旧内容）+ 暂存在 → 正常提交
        Assert.Equal(StageFileState.Ready, StageRecovery.Classify(true, false, true, false));
        Assert.Equal(StageFileState.Ready, StageRecovery.Classify(true, false, true, true));
        // 新增条目：目标本来就不存在 + 暂存在 → 直接就位，无需备份
        Assert.Equal(StageFileState.Ready, StageRecovery.Classify(false, false, true, false));
        // 暂存内容丢了 → 必须重下
        Assert.Equal(StageFileState.NeedDownload, StageRecovery.Classify(true, false, false, false));
        Assert.Equal(StageFileState.NeedDownload, StageRecovery.Classify(true, false, false, true));
        Assert.Equal(StageFileState.NeedDownload, StageRecovery.Classify(false, false, false, false));
    }

    /// <summary>状态到动作一一对应，防止「加了状态忘了加动作」。</summary>
    [Fact]
    public void Decide_MapsEveryStateToAnAction()
    {
        Assert.Equal(StageFileAction.Skip, StageRecovery.Decide(StageFileState.Applied));
        Assert.Equal(StageFileAction.Commit, StageRecovery.Decide(StageFileState.Ready));
        Assert.Equal(StageFileAction.ResumeCommit, StageRecovery.Decide(StageFileState.Interrupted));
        Assert.Equal(StageFileAction.RestoreBackup, StageRecovery.Decide(StageFileState.Rollback));
        Assert.Equal(StageFileAction.Refetch, StageRecovery.Decide(StageFileState.NeedDownload));
    }

    /// <summary>提交顺序：入口排最后、组内原序不变（入口由文件扩展名推出，不需要宿主声明）。</summary>
    [Fact]
    public void OrderForCommit_PutsEntryExeLast_KeepsTheRestInOrder()
    {
        var ordered = StageRecovery.OrderForCommit(new[] { "bin/a.exe", "data/x.dat", "bin/b.dll", "app.exe" });
        Assert.Equal(new[] { "data/x.dat", "bin/b.dll", "bin/a.exe", "app.exe" }, ordered);
    }

    // ========================= ② 依赖的文件系统事实 =========================

    /// <summary>
    /// 只要有一个句柄没给 <c>FILE_SHARE_DELETE</c>，该文件就既不能改名、也不能覆盖、也不能删除。
    /// 这正是「提交阶段只做改名」的必要性来源：改名与覆盖/删除的**可行域不一样**。
    /// POSIX 没有共享模式，本断言只在 Windows 上有意义（那里本来也不存在这个约束）。
    /// </summary>
    [Fact]
    public void Windows_WithoutDeleteShare_RenameOverwriteAndDeleteAreAllBlocked()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var target = Path.Combine(tmp.Path, "f.dat");
        File.WriteAllText(target, "old");
        var fresh = Path.Combine(tmp.Path, "f.new");
        File.WriteAllText(fresh, "new");

        using (var hold = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            AssertBlocked(() => File.Move(target, target + ".ren"), "改名");
            AssertBlocked(() => File.Move(fresh, target, overwrite: true), "覆盖");
            AssertBlocked(() => File.Delete(target), "删除");
        }
    }

    /// <summary>给足 delete 共享之后，同一个文件就能改名了 —— 差别只在这一个位。</summary>
    [Fact]
    public void Windows_WithDeleteShare_RenameSucceeds()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var target = Path.Combine(tmp.Path, "f.dat");
        File.WriteAllText(target, "old");

        using (var hold = new FileStream(target, FileMode.Open, FileAccess.ReadWrite,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            File.Move(target, target + ".ren");   // 不抛异常即通过
        }

        Assert.False(File.Exists(target));
        Assert.True(File.Exists(target + ".ren"));
    }

    /// <summary>
    /// 只读属性：**可以改名**，但覆盖与删除都会失败。
    /// 这也是「先改名再就位」顺带解决只读文件的原因 —— 若选「直接覆盖」，这里就会卡住。
    /// </summary>
    [Fact]
    public void Windows_ReadOnlyFile_CanBeRenamed_ButNotOverwrittenOrDeleted()
    {
        // 只读属性的语义依赖文件系统：这里钉的是 NTFS（exFAT 上行为不同，实测过）。
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        if (!IsNtfs(tmp.Path)) return;
        var target = Path.Combine(tmp.Path, "ro.dat");
        File.WriteAllText(target, "old");
        File.SetAttributes(target, FileAttributes.ReadOnly);
        try
        {
            AssertBlocked(() => File.Delete(target), "删除只读文件");
            var fresh = Path.Combine(tmp.Path, "ro.new");
            File.WriteAllText(fresh, "new");
            AssertBlocked(() => File.Move(fresh, target, overwrite: true), "覆盖只读文件");

            File.Move(target, target + ".ren");       // 改名允许
            Assert.True(File.Exists(target + ".ren"));
        }
        finally
        {
            foreach (var p in new[] { target, target + ".ren" })
                if (File.Exists(p)) File.SetAttributes(p, FileAttributes.Normal);   // 让 TempDir 能清干净
        }
    }

    // ==================== ③ 参考实现：三阶段往返 + 强杀点 ====================

    /// <summary>正常路径：暂存 → 提交 → 与目标版本逐字节一致；备份齐全；回退能回到旧版本。</summary>
    [Fact]
    public void TwoPhaseApply_Converges_AndTheBackupsAllowRollback()
    {
        using var tmp = new TempDir();
        var client = tmp.Sub("client");
        var manifest = Path.Combine(tmp.Path, "manifest.txt");
        TestSupport.WriteTree(client, Pairs(V1));
        File.WriteAllText(manifest, "V1");

        var changed = Changed(V1, V2);
        var s = new RefStager(client, manifest);

        s.Stage(changed);
        AssertTreeIs(client, V1);                                  // 阶段一不碰现有文件
        s.Commit(StageRecovery.OrderForCommit(changed.Keys), "V2");

        AssertTreeIs(client, V2);
        Assert.Equal("V2", File.ReadAllText(manifest));
        foreach (var rel in changed.Keys)
            if (V1.ContainsKey(rel)) Assert.True(File.Exists(s.Old(rel)), $"缺备份: {rel}");
        Assert.False(File.Exists(s.Old("bin/new.dll")));            // 新增文件没有备份

        // 产品级回退：备份改名回去；新增文件直接删掉；manifest 也要回退
        Rollback(s, changed);
        File.WriteAllText(manifest, "V1");
        AssertTreeIs(client, V1);
        Assert.Equal("V1", File.ReadAllText(manifest));
    }

    /// <summary>阶段一被杀：树零改动，manifest 也没动 —— 重跑一遍（或续做）即可。</summary>
    [Fact]
    public void KillDuringStaging_LeavesTheTreeUntouched()
    {
        using var tmp = new TempDir();
        var client = tmp.Sub("client");
        var manifest = Path.Combine(tmp.Path, "manifest.txt");
        TestSupport.WriteTree(client, Pairs(V1));
        File.WriteAllText(manifest, "V1");

        var changed = Changed(V1, V2);
        var s = new RefStager(client, manifest);
        Assert.Throws<Killed>(() => s.Stage(changed, dieAfterFiles: 1));

        AssertTreeIs(client, V1);
        Assert.Equal("V1", File.ReadAllText(manifest));
    }

    /// <summary>崩在两次改名之间（目标缺失）：判定为 Interrupted，续做后收敛。</summary>
    [Fact]
    public void KillBetweenBackupAndPlacement_IsInterrupted_AndResumes()
    {
        using var tmp = new TempDir();
        var client = tmp.Sub("client");
        var manifest = Path.Combine(tmp.Path, "manifest.txt");
        TestSupport.WriteTree(client, Pairs(V1));
        File.WriteAllText(manifest, "V1");

        var changed = Changed(V1, V2);
        var rel = StageRecovery.OrderForCommit(changed.Keys)[0];
        var s = new RefStager(client, manifest);
        s.Stage(changed);
        Assert.Throws<Killed>(() => s.Commit(new[] { rel }, "V2", dieAfterRenames: 1));

        Assert.False(File.Exists(Full(client, rel)));               // 目标缺失
        Assert.True(File.Exists(s.New(rel)));
        Assert.True(File.Exists(s.Old(rel)));
        Assert.Equal(StageFileState.Interrupted, StageRecovery.Classify(false, false, true, true));

        Recover(s, changed, manifest, "V2");
        AssertTreeIs(client, V2);
        Assert.Equal("V2", File.ReadAllText(manifest));
    }

    /// <summary>就位之后、推进 manifest 之前被杀：判定为 Applied，跳过即可。</summary>
    [Fact]
    public void KillAfterPlacement_IsClassifiedAsApplied()
    {
        using var tmp = new TempDir();
        var client = tmp.Sub("client");
        var manifest = Path.Combine(tmp.Path, "manifest.txt");
        TestSupport.WriteTree(client, Pairs(V1));
        File.WriteAllText(manifest, "V1");

        var changed = Changed(V1, V2);
        var rel = StageRecovery.OrderForCommit(changed.Keys)[0];
        var s = new RefStager(client, manifest);
        s.Stage(changed);
        Assert.Throws<Killed>(() => s.Commit(new[] { rel }, "V2", dieBeforeManifest: true));

        var target = Full(client, rel);
        Assert.True(File.Exists(target));
        Assert.Equal(TestSupport.Md5(changed[rel]), TestSupport.Md5File(target));
        Assert.False(File.Exists(s.New(rel)));
        Assert.True(File.Exists(s.Old(rel)));
        Assert.Equal(StageFileState.Applied, StageRecovery.Classify(true, true, false, true));

        Recover(s, changed, manifest, "V2");
        AssertTreeIs(client, V2);
    }

    /// <summary>
    /// 提交到一半被杀：树既不是全新也不是全旧，但**每个文件的状态都能被判定**，
    /// 且任一时刻最多只有一个文件处于「缺失」态；续做后收敛；manifest 绝不提前推进。
    /// </summary>
    [Fact]
    public void KillMidwayThroughManyFiles_Converges_AndNeverAdvancesTheManifestEarly()
    {
        using var tmp = new TempDir();
        var client = tmp.Sub("client");
        var manifest = Path.Combine(tmp.Path, "manifest.txt");
        TestSupport.WriteTree(client, Pairs(V1));
        File.WriteAllText(manifest, "V1");

        var changed = Changed(V1, V2);
        var s = new RefStager(client, manifest);
        s.Stage(changed);
        Assert.Equal("V1", File.ReadAllText(manifest));             // 阶段一结束也不推进

        Assert.Throws<Killed>(() => s.Commit(StageRecovery.OrderForCommit(changed.Keys), "V2", dieAfterRenames: 3));
        Assert.Equal("V1", File.ReadAllText(manifest));             // 提交中断更不能推进

        var missing = 0;
        foreach (var (rel, content) in changed)
        {
            var target = Full(client, rel);
            var exists = File.Exists(target);
            var isNew = exists && TestSupport.Md5File(target) == TestSupport.Md5(content);
            var state = StageRecovery.Classify(exists, isNew, File.Exists(s.New(rel)), File.Exists(s.Old(rel)));
            if (state == StageFileState.Interrupted) missing++;
            Assert.NotEqual(StageFileState.Rollback, state);        // 本次模拟没丢过暂存内容
        }
        Assert.InRange(missing, 0, 1);                              // 逐文件两步改名 ⇒ 最多一个缺失

        Recover(s, changed, manifest, "V2");
        AssertTreeIs(client, V2);
        Assert.Equal("V2", File.ReadAllText(manifest));             // 全部收敛后才推进
    }

    // ============================== 基础设施 ==============================

    private sealed class Killed : Exception
    {
    }

    /// <summary>卷是否是 NTFS（只读属性/改名的语义在别的文件系统上不一样）。</summary>
    private static bool IsNtfs(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string Full(string root, string rel) =>
        Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static (string, string)[] Pairs(IReadOnlyDictionary<string, string> files) =>
        files.Select(kv => (kv.Key, kv.Value)).ToArray();

    /// <summary>只取"内容变了 / 新增"的条目 —— 与产品一致：未变的文件既不暂存也不提交。</summary>
    private static Dictionary<string, string> Changed(
        IReadOnlyDictionary<string, string> from, IReadOnlyDictionary<string, string> to) =>
        to.Where(kv => !from.TryGetValue(kv.Key, out var old) || old != kv.Value)
          .ToDictionary(kv => kv.Key, kv => kv.Value);

    private static void AssertBlocked(Action action, string what)
    {
        var ex = Record.Exception(action);
        Assert.NotNull(ex);
        Assert.True(ex is IOException or UnauthorizedAccessException,
            $"{what}：期望共享冲突/拒绝访问，实际 {ex!.GetType().Name}");
    }

    /// <summary>树必须与给定版本逐字节一致（`UpdaterStage` 不算树的一部分）。</summary>
    private static void AssertTreeIs(string root, IReadOnlyDictionary<string, string> expected)
    {
        var actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Where(rel => !rel.StartsWith("UpdaterStage/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(rel => rel, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(), actual);
        foreach (var (rel, content) in expected)
            Assert.Equal(TestSupport.Md5(content), TestSupport.Md5File(Full(root, rel)));
    }

    /// <summary>恢复：逐文件用**真实的** <see cref="StageRecovery"/> 判定并执行 —— 产品 applier 的骨架。</summary>
    private static void Recover(
        RefStager s, IReadOnlyDictionary<string, string> changed, string manifest, string newManifest)
    {
        foreach (var (rel, content) in changed)
        {
            var target = Full(s.Root, rel);
            var exists = File.Exists(target);
            var isNew = exists && TestSupport.Md5File(target) == TestSupport.Md5(content);
            var action = StageRecovery.Decide(StageRecovery.Classify(
                exists, isNew, File.Exists(s.New(rel)), File.Exists(s.Old(rel))));

            if (action == StageFileAction.Skip) continue;

            if (action == StageFileAction.RestoreBackup)
            {
                if (File.Exists(target)) File.Delete(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(s.Old(rel), target);
                continue;
            }

            // Commit / ResumeCommit / Refetch 都归结为「把目标变成新内容」
            if (!File.Exists(s.New(rel)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(s.New(rel))!);
                File.WriteAllText(s.New(rel), content, new UTF8Encoding(false));
            }
            if (File.Exists(target))
            {
                if (File.Exists(s.Old(rel))) File.Delete(s.Old(rel));
                Directory.CreateDirectory(Path.GetDirectoryName(s.Old(rel))!);
                File.Move(target, s.Old(rel));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(s.New(rel), target);
        }

        File.WriteAllText(manifest, newManifest);
    }

    /// <summary>产品级回退：有备份的改名回去；没有备份的（新增文件）直接删掉。</summary>
    private static void Rollback(RefStager s, IReadOnlyDictionary<string, string> changed)
    {
        foreach (var rel in changed.Keys.Reverse())
        {
            var target = Full(s.Root, rel);
            if (File.Exists(s.Old(rel)))
            {
                if (File.Exists(target)) File.Delete(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(s.Old(rel), target);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
    }

    /// <summary>
    /// **参考实现**（只在测试里）：按 AGENT.md §12.5 的三阶段跑一遍。
    /// 它不是产品实现，只是把「阶段边界的失败语义」变成可执行断言的最小骨架。
    /// </summary>
    private sealed class RefStager
    {
        private readonly string _manifest;

        public RefStager(string root, string manifest)
        {
            Root = root;
            StageRoot = Path.Combine(root, "UpdaterStage");
            _manifest = manifest;
        }

        public string Root { get; }
        public string StageRoot { get; }

        public string New(string rel) =>
            Path.Combine(StageRoot, "new", rel.Replace('/', Path.DirectorySeparatorChar));

        public string Old(string rel) =>
            Path.Combine(StageRoot, "old", rel.Replace('/', Path.DirectorySeparatorChar));

        private static void EnsureParent(string path) =>
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        /// <summary>阶段一：逐文件落到 <c>UpdaterStage/new/…</c>，不碰任何现有文件。</summary>
        public void Stage(IReadOnlyDictionary<string, string> files, int dieAfterFiles = -1)
        {
            var n = 0;
            foreach (var (rel, content) in files)
            {
                EnsureParent(New(rel));
                File.WriteAllText(New(rel), content, new UTF8Encoding(false));
                if (dieAfterFiles >= 0 && ++n > dieAfterFiles) throw new Killed();
            }
        }

        /// <summary>
        /// 阶段二：提交。每个文件两次改名（旧→备份、暂存→目标）；
        /// <paramref name="dieAfterRenames"/> 是「第几次改名之后被杀」，因此 1 = 正好崩在两次改名之间。
        /// 【必须】本地 manifest 只在全部成功之后推进（§12.5）。
        /// </summary>
        public void Commit(IReadOnlyList<string> rels, string newManifest, int dieAfterRenames = -1, bool dieBeforeManifest = false)
        {
            var ops = 0;
            foreach (var rel in rels)
            {
                var target = Full(Root, rel);
                if (File.Exists(target))
                {
                    if (File.Exists(Old(rel))) File.Delete(Old(rel));   // §12：备份重名先清
                    if (ops == dieAfterRenames) throw new Killed();
                    EnsureParent(Old(rel));
                    File.Move(target, Old(rel));
                    ops++;
                }
                if (ops == dieAfterRenames) throw new Killed();
                EnsureParent(target);
                File.Move(New(rel), target);
                ops++;
            }
            if (dieBeforeManifest) throw new Killed();
            File.WriteAllText(_manifest, newManifest);
        }
    }
}