using Ra3.BattleNet.Updater.Server;

namespace Ra3.BattleNet.Updater.Tests;

public class PatchGenerationTests
{
    private static (string V1, string V2, string M1, string M2) Prepare(TempDir tmp, int size = 64 * 1024, string content1 = "v1", string content2 = "v2")
    {
        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1, ("s.txt", TestSupport.Big(content1, size)));
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2, ("s.txt", TestSupport.Big(content2, size)));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        ManifestGenerator.Generate(v2, m1, [], oldRoot: v1).Manifest.SaveToXml(m2);

        return (v1, v2, m1, m2);
    }

    [Fact]
    public void MinSizePreFilter_SkipsSmallFiles()
    {
        using var tmp = new TempDir();
        // 显式设置预过滤：1 KB 文件根本不尝试生成补丁（默认不过滤，靠尺寸后判断）
        var (v1, v2, m1, m2) = Prepare(tmp, size: 1024);
        var server = tmp.Sub("server");

        var summary = PatchGenerator.Generate(m2, v2, [new Baseline(m1, v1)], server, minFileSize: 16 * 1024);

        Assert.Equal(0, summary.PatchesCreated);
        Assert.Empty(Directory.GetFiles(Path.Combine(server, "patches")));
    }

    [Fact]
    public void MinSizeZero_ProducesContentPairNamedPatch_AndVerifies()
    {
        using var tmp = new TempDir();
        var (v1, v2, m1, m2) = Prepare(tmp);
        var server = tmp.Sub("server");

        var summary = PatchGenerator.Generate(m2, v2, [new Baseline(m1, v1)], server, minFileSize: 0);

        Assert.Equal(1, summary.PatchesCreated);
        Assert.Equal(0, summary.PatchesFailed);

        var patch = Assert.Single(Directory.GetFiles(Path.Combine(server, "patches")));
        var name = Path.GetFileName(patch);
        Assert.Matches("^[0-9a-f]{32}_[0-9a-f]{32}\\.hdiff$", name);

        // 名字里的两个端点必须与清单一致（old 在前、new 在后，AGENT.md §3.3）
        var oldMd5 = TestSupport.Md5File(Path.Combine(v1, "s.txt"));
        var newMd5 = TestSupport.Md5File(Path.Combine(v2, "s.txt"));
        Assert.Equal($"{oldMd5}_{newMd5}.hdiff", name);
    }

    [Fact]
    public void SameContentPair_IsGeneratedOnlyOnce()
    {
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1, ("a.bin", TestSupport.Big("ONE")), ("b.bin", TestSupport.Big("ONE")));
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2, ("a.bin", TestSupport.Big("TWO")), ("b.bin", TestSupport.Big("TWO")));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        ManifestGenerator.Generate(v2, m1, [], oldRoot: v1).Manifest.SaveToXml(m2);

        var server = tmp.Sub("server");
        var summary = PatchGenerator.Generate(m2, v2, [new Baseline(m1, v1)], server, minFileSize: 0);

        // 两个文件的内容对完全相同 → 只产出一份补丁
        Assert.Equal(1, summary.PatchesCreated);
        Assert.Single(Directory.GetFiles(Path.Combine(server, "patches")));
    }

    [Fact]
    public void PatchNotSmallerThanTheFile_IsDiscarded()
    {
        // minFileSize=0 强制尝试生成：极小文件的补丁必然比原文大，必须弃用
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1, ("tiny.txt", "aaaa"));
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2, ("tiny.txt", "bbbb"));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        ManifestGenerator.Generate(v2, m1, [], oldRoot: v1).Manifest.SaveToXml(m2);

        var server = tmp.Sub("server");
        var summary = PatchGenerator.Generate(m2, v2, [new Baseline(m1, v1)], server, minFileSize: 0);

        Assert.Equal(0, summary.PatchesCreated);
        Assert.Empty(Directory.GetFiles(Path.Combine(server, "patches")));
    }
}
