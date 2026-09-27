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

        var gen = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1);

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

        var gen = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1);
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

        var gen = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1);
        var report = ManifestGenerator.FormatReport(gen, m1, v1);

        Assert.Equal(2, gen.Added.Count);
        Assert.Equal(1, gen.Removed.Count);
        Assert.Single(gen.SuspectRenames);                     // 只配一次，不许一旧配两新
        Assert.Equal(1, report.Split("➜ 操作").Length - 1);
    }
}