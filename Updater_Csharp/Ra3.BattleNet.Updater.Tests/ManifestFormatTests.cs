using Ra3.BattleNet.Updater.Core;
using Ra3.BattleNet.Updater.Share.Models;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// manifest 格式契约（AGENT.md §3.1 / §7.3）：
/// 读写往返对称、<c>Type</c> 为 <c>Text</c> 时不得退化成 Bin、
/// 且必须能读**仓库里真实存在的遗留写法**。
/// </summary>
public class ManifestFormatTests
{
    [Fact]
    public void RoundTrip_PreservesEveryField_IncludingTextType()
    {
        using var tmp = new TempDir();

        var src = TestSupport.NewManifest("1.2.3");
        var f = TestSupport.Add(src, "readme.txt", "\\docs\\", TestSupport.Md5("hello"),
            FileModeEnum.Skip);
        f.Type = FileTypeEnum.Text;
        f.KindOf = "APPLICATION;TEXT;";
        f.Version = new Version(4, 5, 6);
        src.Tags.Commit = "往返测试";

        var path = Path.Combine(tmp.Path, "m.xml");
        src.SaveToXml(path);

        var back = new ManifestModel(path);

        Assert.Equal("1.2.3", back.Version.ToString());
        Assert.Equal("往返测试", back.Tags.Commit);
        Assert.Equal(src.Tags.UUID, back.Tags.UUID);
        // GenTime 在格式里就是 Unix 秒（§3.1），亚秒精度按设计丢失
        Assert.Equal(src.Tags.GenTime.ToUnixTimeSeconds(), back.Tags.GenTime.ToUnixTimeSeconds());

        Assert.Single(back.Manifest.Files);
        var g = back.Manifest.Files[0];

        Assert.Equal(f.UUID, g.UUID);
        Assert.Equal("readme.txt", g.FileName);
        Assert.Equal(TestSupport.Md5("hello"), g.MD5);
        Assert.Equal(f.Path, g.Path);
        Assert.Equal(new Version(4, 5, 6), g.Version);
        Assert.Equal(FileTypeEnum.Text, g.Type);            // 旧 C++ 版在 Type 上就栽过（as_int）
        Assert.Equal(FileModeEnum.Skip, g.Mode);
        Assert.Equal("APPLICATION;TEXT;", g.KindOf);
    }

    [Fact]
    public void WrittenHash_IsBareLowercaseHex_WithoutAlgorithmPrefix()
    {
        using var tmp = new TempDir();
        var m = TestSupport.NewManifest();
        TestSupport.Add(m, "a.bin", "\\", TestSupport.Md5("A"));

        var path = Path.Combine(tmp.Path, "m.xml");
        m.SaveToXml(path);

        var xml = File.ReadAllText(path);
        Assert.Contains("<MD5>" + TestSupport.Md5("A") + "</MD5>", xml);
        Assert.DoesNotContain("MD5:", xml);
    }

    /// <summary>
    /// 逐字抄自仓库遗留样例 Updater/Updater_Cpp/test/new/manifest.xml：
    /// Type 是**数字** 1、MD5 带 MD5: 前缀、Path 是反斜杠根路径。
    /// 这三处在旧实现里全都踩过坑，必须能被同一份代码读进来。
    /// </summary>
    private const string LegacyManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Metadata Version="2.0.0">
          <Tags>
            <UUID>bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb</UUID>
            <GenTime>1700000001</GenTime>
            <Commit>测试新版本 v2</Commit>
          </Tags>
          <Includes />
          <Manifest>
            <File>
              <UUID>11111111111111111111111111111111</UUID>
              <FileName>test.txt</FileName>
              <MD5>MD5:ad6356ae5c390825abc0b2409192a765</MD5>
              <Path>\</Path>
              <Version>2.0.0</Version>
              <Type>1</Type>
              <Mode>Auto</Mode>
              <KindOf>TEST;NEW;</KindOf>
            </File>
            <File>
              <UUID>22222222222222222222222222222222</UUID>
              <FileName>nested.bin</FileName>
              <MD5>md5:39EBBB4AD56E8B32423526FFBF1C455E</MD5>
              <Path>\CoronaResources\Data\</Path>
              <Version>2.0.0</Version>
              <Type>Bin</Type>
              <Mode>Force</Mode>
              <KindOf>NULL</KindOf>
            </File>
          </Manifest>
        </Metadata>
        """;

    [Fact]
    public void LegacyManifest_IsReadable_HashNormalized_AndPathUsable()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "legacy.xml");
        File.WriteAllText(path, LegacyManifest);

        var m = new ManifestModel(path);

        Assert.Equal("2.0.0", m.Version.ToString());
        Assert.Equal(new Guid("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), m.Tags.UUID);
        Assert.Equal(2, m.Manifest.Files.Count);

        var a = m.Manifest.Files[0];
        Assert.Equal("ad6356ae5c390825abc0b2409192a765", a.MD5);   // 前缀被剥掉
        Assert.Equal(FileTypeEnum.Text, a.Type);                 // 数字 1 也要认
        Assert.Equal(FileModeEnum.Auto, a.Mode);
        Assert.Equal("test.txt", a.RelativePath());              // Path="\" ⇒ 就是根下的文件名

        var b = m.Manifest.Files[1];
        Assert.Equal("39ebbb4ad56e8b32423526ffbf1c455e", b.MD5);   // 大小写与前缀都归一
        Assert.Equal("CoronaResources/Data/nested.bin", b.RelativePath());
        Assert.Equal(FileModeEnum.Force, b.Mode);
    }

    /// <summary>
    /// 容忍遗留写法的**商业理由**：读不进来就会被判成"本地清单损坏"，
    /// 于是本地明明是对的也要重下整棵树。这里把它钉死成"零下载"。
    /// </summary>
    [Fact]
    public void LegacyLocalManifest_StillYieldsASkipPlan_SoNothingIsRedownloaded()
    {
        using var tmp = new TempDir();

        var client = tmp.Sub("client");
        TestSupport.WriteTree(client, ("test.txt", "legacy content"));

        var legacyLocal = LegacyManifest.Replace(
            "MD5:ad6356ae5c390825abc0b2409192a765",
            TestSupport.Md5File(Path.Combine(client, "test.txt")));
        var localPath = Path.Combine(client, "manifest.xml");
        File.WriteAllText(localPath, legacyLocal);

        // 远端清单里，同一个文件（同 UUID、内容未变）只是换了版本号
        var remote = TestSupport.NewManifest("2.0.0");
        remote.Manifest.Files.Add(new ManifestFile(
            new Guid("11111111111111111111111111111111"), "test.txt",
            TestSupport.Md5("legacy content"), "\\", "2.0.0",
            FileTypeEnum.Bin, FileModeEnum.Auto, "TEST;"));

        var plan = UpdatePlanner.Build(remote, new ManifestModel(localPath),
            new UpdateConfig { RootPath = client, ManifestUrl = "http://example.invalid/manifest.xml" });

        Assert.Equal(1, plan.Total);
        Assert.Equal(1, plan.Unchanged);
        Assert.Equal(0, plan.ToDownload);
    }
}
