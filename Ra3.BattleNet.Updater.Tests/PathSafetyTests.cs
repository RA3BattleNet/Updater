using Ra3.BattleNet.Updater.Share.Models;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// §4.14 路径信任边界：manifest 是**远端数据**，由它派生出来的相对路径必须落在安装根内。
/// 判据看的是**定义**，不是最终解析结果 —— `sub/../../x` 即使能解析回根内也一律拒绝。
/// </summary>
public class PathSafetyTests
{
    [Fact]
    public void EscapeVectors_AreRejected_AndOrdinaryPathsAreAccepted()
    {
        var bad = new[]
        {
            "",                          // 空
            "..",                        // 就是上一层
            "../x.dll",
            "..\\x.dll",
            "sub/../../x.dll",           // 中途跨出根（即使能落回来也拒）
            "sub\\..\\..\\x.dll",
            "/abs/x.dll",                // 绝对路径
            "\\abs\\x.dll",
            "//server/share/x.dll",      // UNC
            "C:/x.dll",                  // 盘符
            "C:x.dll",                   // 驱动器相对
            "c:/windows/system32/evil.dll",
            "?/C:/x.dll",                // \\?\ 注入被 Trim('/') 削过之后的样子
            "data/file.txt:stream",      // ADS
            "data/a?b.dll",              // 通配 / 也让 \\?\ 无法成立
            "data/a*b.dll",
            "x\u0000.dll",
        };
        var good = new[]
        {
            "manifest.xml",
            "bin/a.dll",
            "data/config/notes.txt",
            "a b/c d/e.dll",
            "日冕/地编伴侣/lua例子/示例.lua",
            "dotnet/shared/Microsoft.NETCore.App/10.0.5/System.Private.CoreLib.dll",
        };

        foreach (var p in bad) Assert.False(PathSafety.IsSafeRelative(p), "应当拒绝：" + p);
        foreach (var p in good) Assert.True(PathSafety.IsSafeRelative(p), "应当放行：" + p);
    }

    [Fact]
    public void FirstUnsafe_ReturnsTheOffender()
    {
        Assert.Null(PathSafety.FirstUnsafe(["bin/a.dll", "data/notes.txt"]));
        Assert.Equal("../evil.dll", PathSafety.FirstUnsafe(["bin/a.dll", "../evil.dll", "C:/x.dll"]));
    }
}