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
}