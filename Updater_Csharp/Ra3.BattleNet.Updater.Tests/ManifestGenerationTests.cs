using Ra3.BattleNet.Updater.Server;
using Ra3.BattleNet.Updater.Share.Models;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>清单生成：可复现（§5.1）与生成期自检报告（§5.2）。</summary>
public class ManifestGenerationTests
{
    [Fact]
    public void FileList_IsReproducible_ForTheSameInput()
    {
        using var tmp = new TempDir();
        var v = tmp.Sub("v");
        TestSupport.WriteTree(v, ("a.txt", "A"), ("d/b.txt", "B"), ("d/c.txt", "C"));

        static List<string> Fingerprint(ManifestGenerationResult r) => r.Manifest.Manifest.Files
            .Select(f => $"{f.UUID:N}|{f.RelativePath()}|{f.MD5}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var first = Fingerprint(ManifestGenerator.Generate(v, null, []));
        var second = Fingerprint(ManifestGenerator.Generate(v, null, []));

        // 同一份输入必须得到同样的文件列表（含 UUID）——否则重复生成会让补丁与客户端对不上
        Assert.Equal(first, second);
        Assert.Equal(3, first.Count);
    }

    [Fact]
    public void Report_ClassifiesChanges_AndSuggestsUuidFixForSuspectRename()
    {
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1,
            ("keep.txt", "same"),
            ("mod.txt", TestSupport.Big("V1")),
            ("gone.txt", TestSupport.Big("RENAME")));

        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2,
            ("keep.txt", "same"),
            ("mod.txt", TestSupport.Big("V2")),
            ("fresh.txt", TestSupport.Big("RENAME2")));   // 只改了尾部 → 疑似改名+改内容

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);

        // 这条专门测"关了自动关联时，报告要给出可执行的人工建议"
        var gen = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1, autoLink: AutoLinkMode.Off);

        Assert.Equal(1, gen.Unchanged.Count);   // keep.txt
        Assert.Equal(1, gen.Modified.Count);    // mod.txt
        Assert.Equal(1, gen.Added.Count);       // fresh.txt
        Assert.Equal(1, gen.Removed.Count);     // gone.txt
        Assert.Equal(0, gen.Moved.Count);

        var report = ManifestGenerator.FormatReport(gen, m1, v1);

        Assert.Contains("未变", report);
        Assert.Contains("已修改", report);
        Assert.Contains("新增", report);
        Assert.Contains("消失", report);

        // 必须给出**可执行**的指令：把新增条目的 UUID 改成消失条目的
        var goneUuid = gen.Removed[0].UUID.ToString("N");
        Assert.Contains("➜ 操作: 把新增条目的 <UUID> 改成 " + goneUuid, report);
        Assert.Contains("不会报错", report);
    }

    [Fact]
    public void Report_DoesNotSuggestRename_ForUnrelatedAddAndRemove()
    {
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1, ("gone.txt", "all A content that is unrelated"));
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2, ("fresh.txt", "TOTALLY DIFFERENT BYTES 0123456789"));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);

        var gen = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1, autoLink: AutoLinkMode.Off);
        var report = ManifestGenerator.FormatReport(gen, m1, v1);

        Assert.Equal(1, gen.Added.Count);
        Assert.Equal(1, gen.Removed.Count);
        Assert.DoesNotContain("➜ 操作", report);   // 无关的新增/消失不该被建议成改名
    }

    /// <summary>
    /// 5 版本模拟里 v4→v5 是活教材：新打包了一整套 .NET 运行时 →
    /// 614 新增 × 463 消失。旧实现会把这 28 万对**全部**列进报告（实测 93 MB、85 万行），
    /// 每一条都在建议人工改 UUID。§5.2 要求"有界"—— 输出侧同样必须有界。
    /// </summary>
    [Fact]
    public void Report_ForHugeAddRemoveSets_StaysBounded_InsteadOfDumpingTheCrossProduct()
    {
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");

        // 300 个消失 × 300 个新增，内容互不相关（模拟"整块内容被替换"而不是改名）
        TestSupport.WriteTree(v1, Enumerable.Range(0, 300)
            .Select(i => ($"bin/old{i}.dll", "OLD-" + i + "-" + new string((char)('a' + i % 26), 200))).ToArray());
        TestSupport.WriteTree(v2, Enumerable.Range(0, 300)
            .Select(i => ($"dotnet/new{i}.dll", "NEW-" + i + "-" + new string((char)('A' + i % 26), 200))).ToArray());

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);

        var gen = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1);
        var report = ManifestGenerator.FormatReport(gen, m1, v1);

        Assert.Equal(300, gen.Added.Count);
        Assert.Equal(300, gen.Removed.Count);
        Assert.Equal(90_000, gen.ProbeCandidates);
        Assert.Equal(ManifestGenerator.MaxProbePairs, gen.ProbedPairs);   // 探测次数被钉死
        Assert.True(report.Length < 4_000, $"报告不该随笛卡尔积膨胀，实得 {report.Length} 字符");
        Assert.DoesNotContain("补丁率 ?", report);                        // 不许出现"没算就列出来"
        Assert.Contains("候选 90000 对", report);
    }

    /// <summary>一个消失文件只能是一个新增文件的"疑似前身"（UUID 在清单里唯一）。</summary>
    [Fact]
    public void Report_DoesNotSuggestTheSameRemovedFileForTwoAddedFiles()
    {
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");

        TestSupport.WriteTree(v1, ("bin/one.dll", TestSupport.Big("RENAMED", 8192)));
        TestSupport.WriteTree(v2,
            ("bin/a.dll", TestSupport.Big("RENAMED-A", 8192)),
            ("bin/b.dll", TestSupport.Big("RENAMED-B", 8192)));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);

        // 同样是"人工建议"路径：自动关联关掉时才要求报告指出改哪一个 UUID
        var gen = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1, autoLink: AutoLinkMode.Off);
        var report = ManifestGenerator.FormatReport(gen, m1, v1);

        Assert.Equal(2, gen.Added.Count);
        Assert.Equal(1, gen.Removed.Count);
        Assert.Single(gen.SuspectRenames);                     // 只配一次，不许一旧配两新
        Assert.Equal(1, report.Split("➜ 操作").Length - 1);
    }
}
/// <summary>
/// 自动关联改名（`XmlGenerator` 默认行为，`--no-auto-link-uuids` 可关）。
/// 猜的逻辑：**同名优先 → 尺寸窗口兜底 → 贪心 1:1**。理由与实测见 XmlGenerator/README.md。
/// </summary>
public class AutoLinkTests
{
    [Fact]
    public void NameMatch_LinksUuid_SoTheClientCanPatch()
    {
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");

        // 典型的"运行时搬家"：同名、目录变了、内容也变了
        TestSupport.WriteTree(v1,
            ("bin/System.Private.CoreLib.dll", TestSupport.Big("RUNTIME-8", 16 * 1024)),
            ("bin/keep.txt", "keep"));
        TestSupport.WriteTree(v2,
            ("dotnet/shared/10.0.5/System.Private.CoreLib.dll", TestSupport.Big("RUNTIME-10", 16 * 1024)),
            ("bin/keep.txt", "keep"));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);

        // 默认（不传参数）就已经关联上了
        var on = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1);
        // 显式关掉：确定性规则认不出来 → 记成新增/消失
        var off = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1, autoLink: AutoLinkMode.Off);

        Assert.Equal(1, off.Added.Count);
        Assert.Equal(1, off.Removed.Count);
        Assert.Empty(off.AutoLinked);

        // 开开关：按同名接上，且 UUID 等于前身的
        var link = Assert.Single(on.AutoLinked);
        Assert.Equal(RenameLinkEvidence.Name, link.Evidence);
        Assert.Equal("System.Private.CoreLib.dll", link.Added.FileName);
        Assert.Equal(link.Removed.UUID, link.Added.UUID);

        // 权威输入是"写出去的 XML 再读回来"：UUID 必须真的被改过
        var outXml = Path.Combine(tmp.Path, "v2.xml");
        on.Manifest.SaveToXml(outXml);
        var reread = new ManifestModel(outXml).Manifest.Files.Single(f => f.FileName == "System.Private.CoreLib.dll");
        Assert.Equal(link.Removed.UUID, reread.UUID);

        // 报告必须说清楚"改了什么、凭什么改的"
        var report = ManifestGenerator.FormatReport(on, m1, v1);
        Assert.Contains("已自动关联改名 1 对", report);
        Assert.Contains("依据 同名", report);
    }

    [Fact]
    public void UuidStaysUnique_AfterAutoLink()
    {
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");

        // 多个同名文件（各语言目录），确保贪心 1:1 不会把同一个 UUID 用两次
        TestSupport.WriteTree(v1,
            ("bin/en/App.resources.dll", TestSupport.Big("EN-1", 8 * 1024)),
            ("bin/ja/App.resources.dll", TestSupport.Big("JA-1", 8 * 1024)),
            ("bin/zh/App.resources.dll", TestSupport.Big("ZH-1", 8 * 1024)));
        TestSupport.WriteTree(v2,
            ("dotnet/en/App.resources.dll", TestSupport.Big("EN-2", 8 * 1024)),
            ("dotnet/ja/App.resources.dll", TestSupport.Big("JA-2", 8 * 1024)),
            ("dotnet/zh/App.resources.dll", TestSupport.Big("ZH-2", 8 * 1024)));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);

        var on = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1, autoLink: AutoLinkMode.ByNameAndSize);

        Assert.Equal(3, on.AutoLinked.Count);
        // 一个消失文件只能被用一次 ⇒ 新清单里 UUID 唯一（否则 Manifest 读取时会跳过重复条目）
        var uuids = on.Manifest.Manifest.Files.Select(f => f.UUID).ToList();
        Assert.Equal(uuids.Count, uuids.Distinct().Count());
    }

    [Fact]
    public void ByNameMode_DoesNotUseTheWeakSizeSignal()
    {
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");

        // 名字完全不同、尺寸一样但**内容不同**（内容相同会被 MD5 规则直接继承成 Moved，这里就测不到了）
        TestSupport.WriteTree(v1, ("bin/OldName.bin", TestSupport.Big("OLD-CONTENT", 8 * 1024)));
        TestSupport.WriteTree(v2, ("bin/NewName.bin", TestSupport.Big("NEW-CONTENT", 8 * 1024)));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);

        var byName = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1, autoLink: AutoLinkMode.ByName);
        var full = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1, autoLink: AutoLinkMode.ByNameAndSize);

        Assert.Empty(byName.AutoLinked);                             // 保守模式：不猜
        var link = Assert.Single(full.AutoLinked);                   // 打开兜底才配
        Assert.Equal(RenameLinkEvidence.Size, link.Evidence);
    }

    [Fact]
    public void SizeWindow_RejectsWildlyDifferentSizes()
    {
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");

        // 名字不同、尺寸差 100 倍 → 不该被硬凑成一对
        TestSupport.WriteTree(v1, ("bin/Old.bin", TestSupport.Big("OLD", 64 * 1024)));
        TestSupport.WriteTree(v2, ("bin/New.bin", TestSupport.Big("NEW", 640)));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);

        var on = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1, autoLink: AutoLinkMode.ByNameAndSize);
        Assert.Empty(on.AutoLinked);
    }

    /// <summary>
    /// 自动关联的尺寸窗口是 **2 倍**（§5.2、README），而不是探测预筛那 4 倍 ——
    /// 这里的结果会被**直接写进清单**，宁可少配也不能配错。
    /// 这两个数过去共用一个常量，于是"文档说 2 倍、代码按 4 倍放行"（I-5）。
    /// </summary>
    [Fact]
    public void AutoLinkSizeWindow_IsTwice_AndIsNotTheProbePrefilter()
    {
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");

        // 名字不同、尺寸差 3 倍：按 4 倍会配成一对，按 2 倍必须拒绝
        TestSupport.WriteTree(v1, ("bin/Old.bin", TestSupport.Big("OLD", 64 * 1024)));
        TestSupport.WriteTree(v2, ("bin/New.bin", TestSupport.Big("NEW", 192 * 1024)));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);

        Assert.Empty(ManifestGenerator.Generate(v2, m1, [], oldRoot: v1).AutoLinked);

        // 两个窗口是**两个不同的数**，不许再合并
        Assert.Equal(2.0, ManifestGenerator.AutoLinkSizeWindowFactor);
        Assert.NotEqual(ManifestGenerator.ProbeSizePrefilterFactor, ManifestGenerator.AutoLinkSizeWindowFactor);

        // 1.5 倍在窗口内 → 正常配对（证明上面为空不是因为"根本配不上"）
        TestSupport.WriteTree(v2, ("bin/New.bin", TestSupport.Big("NEW", 96 * 1024)));
        var inside = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1);
        var link = Assert.Single(inside.AutoLinked);
        Assert.Equal(RenameLinkEvidence.Size, link.Evidence);
    }

    [Fact]
    public void AutoLinkIsOnByDefault_AndCanBeTurnedOff()
    {
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v1, ("bin/a.dll", TestSupport.Big("A1", 4096)));
        TestSupport.WriteTree(v2, ("dotnet/a.dll", TestSupport.Big("A2", 4096)));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);

        // 默认（不传参数）就应当关联上 —— 需求方 2026-09-27 决定默认打开
        var dflt = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1);
        Assert.Single(dflt.AutoLinked);

        // 显式关掉
        var off = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1, autoLink: AutoLinkMode.Off);
        Assert.Empty(off.AutoLinked);
    }

    /// <summary>默认打开之后，报告不能又让人去改一个已经自动改好的 UUID。</summary>
    [Fact]
    public void AutoLinkedPairs_DoNotReappearInTheHumanConfirmationList()
    {
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v1, ("bin/a.dll", TestSupport.Big("A1", 8192)));
        TestSupport.WriteTree(v2, ("dotnet/a.dll", TestSupport.Big("A2", 8192)));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);

        var gen = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1);
        var report = ManifestGenerator.FormatReport(gen, m1, v1);

        Assert.Single(gen.AutoLinked);
        Assert.Contains("已自动关联改名 1 对", report);
        Assert.DoesNotContain("➜ 操作", report);           // 已经改好了，别再让人改一遍
    }
}
