using System.Security.Cryptography;
using System.Text;
using Ra3.BattleNet.Updater.Share.Models;

namespace Ra3.BattleNet.Updater.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        var root = Environment.GetEnvironmentVariable("UPDATER_TEST_TMP");
        if (string.IsNullOrEmpty(root)) root = System.IO.Path.GetTempPath();
        Path = System.IO.Path.Combine(root, "upd-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Sub(string name)
    {
        var p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* 尽力清理 */ }
    }
}

internal static class TestSupport
{
    public static string Md5(string text) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));

    public static string Md5File(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(MD5.HashData(s));
    }

    /// <summary>写入一棵树；内容是 UTF-8 文本。</summary>
    public static void WriteTree(string root, params (string Rel, string Content)[] files)
    {
        foreach (var (rel, content) in files)
        {
            var full = System.IO.Path.Combine(root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(false));
        }
    }

    public static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(from, file);
            var dest = System.IO.Path.Combine(to, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    public static ManifestModel NewManifest(string version = "1.0.0")
        => new(new Version(version), "test");

    /// <summary>
    /// 造一条清单条目。UUID **由 (目录, 文件名) 确定性派生** —— 和服务端生成器的规则一致
    /// （`ManifestGenerator.DeterministicUuid`）：真实的"同路径 ⇒ 同 UUID"因此自动成立。
    /// 需要"同一路径换了 UUID"这种异常场景时，测试自己显式改 <c>UUID</c>。
    /// </summary>
    public static ManifestFile Add(ManifestModel m, string fileName, string dir, string md5,
        FileModeEnum mode = FileModeEnum.Auto)
    {
        var seed = Md5((dir ?? string.Empty).Replace('\\', '/').Trim('/') + "/" + fileName);
        var uuid = new Guid(Convert.FromHexString(seed));
        var f = new ManifestFile(uuid, fileName, md5, dir, "1.0.0",
            FileTypeEnum.Bin, mode, "TEST;");
        m.Manifest.Files.Add(f);
        return f;
    }

    /// <summary>逐文件比对：manifest 列出的每个文件，两边内容哈希必须一致。</summary>
    public static void AssertSameAs(ManifestModel expected, string expectedRoot, string actualRoot)
    {
        foreach (var f in expected.Manifest.Files)
        {
            var rel = f.RelativePath().Replace('/', System.IO.Path.DirectorySeparatorChar);
            var e = System.IO.Path.Combine(expectedRoot, rel);
            var a = System.IO.Path.Combine(actualRoot, rel);

            Assert.True(File.Exists(e), $"期望文件缺失: {rel}");
            Assert.True(File.Exists(a), $"更新后文件缺失: {rel}");
            Assert.Equal(Md5File(e), Md5File(a));
            Assert.Equal(f.MD5, Md5File(a));
        }
    }

    /// <summary>构造一段"改动很小"的大内容，便于得到小补丁。</summary>
    public static string Big(string marker, int size = 64 * 1024)
    {
        var body = new string('A', size - marker.Length);
        return body + marker;
    }
}

/// <summary>同步执行的 IProgress：默认的 Progress&lt;T&gt; 会把回调投到线程池，取消时机不可控。</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;

    public SyncProgress(Action<T> action) => _action = action;

    public void Report(T value) => _action(value);
}