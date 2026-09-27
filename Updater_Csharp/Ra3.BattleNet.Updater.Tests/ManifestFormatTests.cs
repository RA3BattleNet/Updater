using Ra3.BattleNet.Updater.Share.Models;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// manifest 格式契约（AGENT.md §3.1 / §7.3）：读写往返对称、<c>Type</c> 为 <c>Text</c> 时
/// 不得退化成 Bin、哈希字段是**裸的 32 位小写十六进制**（两侧都不加、也不容忍算法前缀）、
/// 目录路径既可能是正斜杠也可能是反斜杠（C++ 侧就写 <c>\</c> 与 <c>\dir\</c>）。
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

    /// <summary>
    /// 「两侧简单对齐」的那条线：写出来的必须是裸十六进制 + 枚举名。
    /// 哈希带算法前缀、或 <c>Type</c> 写成数字，都会让另一端对不上。
    /// </summary>
    [Fact]
    public void WrittenManifest_UsesBareHashAndEnumNames()
    {
        using var tmp = new TempDir();
        var m = TestSupport.NewManifest();
        TestSupport.Add(m, "a.bin", "\\", TestSupport.Md5("A"), FileModeEnum.Force)
            .Type = FileTypeEnum.Text;

        var path = Path.Combine(tmp.Path, "m.xml");
        m.SaveToXml(path);

        var xml = File.ReadAllText(path);

        Assert.Contains("<MD5>" + TestSupport.Md5("A") + "</MD5>", xml);
        Assert.Contains("<Type>Text</Type>", xml);
        Assert.Contains("<Mode>Force</Mode>", xml);
        Assert.DoesNotContain("MD5:", xml);
        Assert.DoesNotContain("<Type>1</Type>", xml);
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
