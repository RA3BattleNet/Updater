using Ra3.BattleNet.Updater.Share.Models;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// manifest 格式契约（AGENT.md §3.1 / §7.3）：读写往返对称、哈希字段是**裸的 32 位小写十六进制**
/// （两侧都不加、也不容忍算法前缀）、目录路径既可能是正斜杠也可能是反斜杠
/// （C++ 侧就写 <c>\</c> 与 <c>\dir\</c>）。
///
/// 【2026-09-28 决定】文件级 <c>Version</c> / <c>Type</c> / <c>KindOf</c> 已剔除：
/// 这里同时钉住两件事 —— **我们不写它们**，以及**老清单里带着它们也照样能读**（往下兼容）。
/// </summary>
public class ManifestFormatTests
{
    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        using var tmp = new TempDir();

        var src = TestSupport.NewManifest("1.2.3");
        var f = TestSupport.Add(src, "readme.txt", "\\docs\\", TestSupport.Md5("hello"),
            FileModeEnum.Skip);
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
        Assert.Equal(FileModeEnum.Skip, g.Mode);
    }

    /// <summary>
    /// 「两侧简单对齐」的那条线：写出来的必须是裸十六进制 + 枚举名；
    /// 并且**不再写**文件级 `Version`/`Type`/`KindOf`（2026-09-28 剔除）。
    /// </summary>
    [Fact]
    public void WrittenManifest_UsesBareHashAndEnumNames_AndOmitsTheRemovedFields()
    {
        using var tmp = new TempDir();
        var m = TestSupport.NewManifest();
        TestSupport.Add(m, "a.bin", "\\", TestSupport.Md5("A"), FileModeEnum.Force);

        var path = Path.Combine(tmp.Path, "m.xml");
        m.SaveToXml(path);

        var xml = File.ReadAllText(path);

        Assert.Contains("<MD5>" + TestSupport.Md5("A") + "</MD5>", xml);
        Assert.Contains("<Mode>Force</Mode>", xml);
        Assert.DoesNotContain("MD5:", xml);

        // 剔除的三个字段：连元素都不该出现（根节点的 Version 是**属性**，不受影响）
        Assert.DoesNotContain("<Version>", xml);
        Assert.DoesNotContain("<Type>", xml);
        Assert.DoesNotContain("<KindOf>", xml);
        Assert.Contains("<Metadata Version=\"1.0.0\">", xml);   // 根节点保持原样
    }

    /// <summary>
    /// **往下兼容**：老清单（官方与历史版本都有）里带着 `Version`/`Type`/`KindOf`，
    /// 现在读端直接忽略它们 —— 不能因为多几个元素就报错。
    /// </summary>
    [Fact]
    public void LegacyManifestWithRemovedFields_StillParses()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "legacy.xml");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <Metadata Version="1.0.0">
              <Tags><UUID>11111111111111111111111111111111</UUID><GenTime>1700000001</GenTime><Commit>x</Commit></Tags>
              <Includes />
              <Manifest>
                <File>
                  <UUID>22222222222222222222222222222222</UUID>
                  <FileName>a.bin</FileName>
                  <MD5>ad6356ae5c390825abc0b2409192a765</MD5>
                  <Path>\bin\</Path>
                  <Version>4.5.6</Version>
                  <Type>Text</Type>
                  <Mode>Auto</Mode>
                  <KindOf>APPLICATION;TEXT;</KindOf>
                </File>
              </Manifest>
            </Metadata>
            """);

        var file = new ManifestModel(path).Manifest.Files.Single();

        Assert.Equal("a.bin", file.FileName);
        Assert.Equal("ad6356ae5c390825abc0b2409192a765", file.MD5);
        Assert.Equal(FileModeEnum.Auto, file.Mode);
        Assert.Equal("bin/a.bin", file.RelativePath());
    }

    /// <summary>
    /// 哈希字段带算法前缀（`MD5:ad63…`）是**错误的写法**，不做兼容、也不剥前缀：
    /// 必须当场响亮地失败，而不是被静默接受后拿去拼出错误的 URL。
    /// </summary>
    [Fact]
    public void HashField_WithAlgorithmPrefix_IsRejectedLoudly()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "bad.xml");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <Metadata Version="1.0.0">
              <Tags><UUID>11111111111111111111111111111111</UUID><GenTime>1700000001</GenTime><Commit>x</Commit></Tags>
              <Includes />
              <Manifest>
                <File>
                  <UUID>22222222222222222222222222222222</UUID>
                  <FileName>a.bin</FileName>
                  <MD5>MD5:ad6356ae5c390825abc0b2409192a765</MD5>
                  <Path>\</Path>
                  <Version>1.0.0</Version>
                  <Type>Bin</Type>
                  <Mode>Auto</Mode>
                  <KindOf>NULL</KindOf>
                </File>
              </Manifest>
            </Metadata>
            """);

        Assert.Throws<ArgumentException>(() => new ManifestModel(path));
    }

    /// <summary>
    /// 目录路径的归一化：C++ 侧写的是 <c>\</c>（根）与 <c>\Dir\Sub\</c>（子目录）。
    /// 「目录 + 文件名」合成相对路径这条语义与分隔符无关。
    /// </summary>
    [Fact]
    public void BackslashDirectoryPaths_AreNormalizedToRelativePaths()
    {
        using var tmp = new TempDir();
        var m = TestSupport.NewManifest();
        TestSupport.Add(m, "root.txt", "\\", TestSupport.Md5("1"));
        TestSupport.Add(m, "nested.bin", "\\CoronaResources\\Data\\", TestSupport.Md5("2"));

        var path = Path.Combine(tmp.Path, "m.xml");
        m.SaveToXml(path);

        var files = new ManifestModel(path).Manifest.Files.ToDictionary(f => f.FileName);

        Assert.Equal("root.txt", files["root.txt"].RelativePath());
        Assert.Equal("CoronaResources/Data/nested.bin", files["nested.bin"].RelativePath());
    }
}
