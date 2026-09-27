using Ra3.BattleNet.Updater.Server;
using Ra3.BattleNet.Updater.Share.Models;
using ClientApi = Ra3.BattleNet.Updater.Client.API;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>离线补丁包（Server.CLI / Client.CLI 走的链路）端到端。</summary>
public class PatchPackageTests
{
    private static (string V1, string V2, string M1, string M2, ManifestGenerationResult Gen) Prepare(TempDir tmp)
    {
        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1,
            ("bin/a.dll", TestSupport.Big("ONE")),
            ("keep.txt", "same"),
            ("data/old.bin", "shared"));

        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2,
            ("bin/a.dll", TestSupport.Big("TWO")),
            ("keep.txt", "same"),
            ("data/new.bin", "shared"),
            ("added.txt", "brand new"));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);
        var gen = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        gen.Manifest.SaveToXml(m2);

        return (v1, v2, m1, m2, gen);
    }

    [Fact]
    public void OfflinePackage_RoundTrip_HandlesModifyAddSkipAndRename()
    {
        using var tmp = new TempDir();
        var (v1, v2, m1, m2, gen) = Prepare(tmp);

        var package = tmp.Sub("package");
        var ops = Ra3.BattleNet.Updater.Server.API.CalculatePatchOperations(
            new ManifestModel(m1), new ManifestModel(m2), v1, v2, package);
        Ra3.BattleNet.Updater.Server.API.GeneratePatchPackage(ops, new ManifestModel(m2), package);

        // 载荷必须与在线链路同一套命名
        foreach (var f in Directory.GetFiles(Path.Combine(package, "patches")))
            Assert.Matches("^[0-9a-f]{32}_[0-9a-f]{32}\\.bin$", Path.GetFileName(f));

        // 三种操作都要出现：内容变了走补丁、新增走完整、纯改名走 move
        var manifest = ClientApi.LoadPatchManifest(package);
        Assert.NotNull(manifest);
        Assert.Contains(manifest!.Operations!, o => o.Type == "patch");
        Assert.Contains(manifest.Operations!, o => o.Type == "forcecopy");
        Assert.Contains(manifest.Operations!, o => o.Type == "move");

        // 客户端从 v1 出发应用补丁包
        var client = tmp.Sub("client");
        TestSupport.CopyTree(v1, client);

        Assert.True(ClientApi.ApplyPatchOperations(manifest, client, package));

        TestSupport.AssertSameAs(gen.Manifest, v2, client);
        Assert.False(File.Exists(Path.Combine(client, "data", "old.bin")), "改名后旧路径不应残留");
    }

    [Fact]
    public void OfflinePackage_TinyFile_UsesFullCopyInsteadOfPatch()
    {
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1, ("t.txt", "aaaa"));
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2, ("t.txt", "bbbb"));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        ManifestGenerator.Generate(v2, m1, [], oldRoot: v1).Manifest.SaveToXml(m2);

        var package = tmp.Sub("package");
        var ops = Ra3.BattleNet.Updater.Server.API.CalculatePatchOperations(
            new ManifestModel(m1), new ManifestModel(m2), v1, v2, package);

        Assert.Empty(Directory.GetFiles(Path.Combine(package, "patches")));
        var op = Assert.Single(ops);
        Assert.Equal(OperationTypeEnum.ForceCopy, op.Type);
    }
}
