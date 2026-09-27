using System.Security.Cryptography;
using System.Text;

namespace Ra3.BattleNet.Updater.Core;

/// <summary>长路径友好的文件系统操作（AGENT.md §7.5）。</summary>
internal static class Fs
{
    /// <summary>必要时加 \\?\ 前缀，使超过 MAX_PATH 的路径可用。</summary>
    public static string P(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        if (path.Length < 240) return path;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (!Path.IsPathFullyQualified(path)) return path;
        return @"\\?\" + path.Replace('/', '\\');
    }

    public static bool Exists(string path) => File.Exists(P(path));

    public static void CreateDirectory(string path) => Directory.CreateDirectory(P(path));

    public static void Move(string from, string to, bool overwrite) => File.Move(P(from), P(to), overwrite);

    public static void Delete(string path)
    {
        var p = P(path);
        if (File.Exists(p)) File.Delete(p);
    }

    public static long Length(string path) => new FileInfo(P(path)).Length;

    public static FileStream OpenRead(string path) =>
        new(P(path), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);

    public static FileStream OpenWrite(string path, bool append) =>
        new(P(path), append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);

    public static byte[] ReadAllBytes(string path) => File.ReadAllBytes(P(path));

    public static string ReadAllText(string path) => File.ReadAllText(P(path), Encoding.UTF8);

    public static void WriteAllBytes(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) CreateDirectory(dir);
        File.WriteAllBytes(P(path), data);
    }

    public static void WriteAllText(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) CreateDirectory(dir);
        File.WriteAllText(P(path), text, new UTF8Encoding(false));
    }

    /// <summary>确保目标目录存在，然后原子替换目标文件（同卷 rename）。</summary>
    public static void Place(string tempFile, string targetPath)
    {
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) CreateDirectory(dir);
        Move(tempFile, targetPath, overwrite: true);
    }

    /// <summary>开头是不是 gzip 魔数（1f 8b）—— 判断"落盘的到底压过没有"。</summary>
    public static bool LooksGzipped(string path)
    {
        try
        {
            using var s = OpenRead(path);
            return s.ReadByte() == 0x1F && s.ReadByte() == 0x8B;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把 gzip 文件解到 dest（先写临时文件再原子替换）。**失败不留半个目标文件**，返回 false。
    /// gzip 自带 CRC32 与原始长度，所以"解压能过"就等于"传输完整"（§4.3 ⑥ 的前置）。
    /// </summary>
    public static bool TryGunzip(string gzPath, string dest)
    {
        var tmp = dest + ".gunzip";
        try
        {
            using (var input = OpenRead(gzPath))
            using (var gz = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress))
            using (var output = OpenWrite(tmp, append: false))
                gz.CopyTo(output, 1 << 16);

            Move(tmp, dest, overwrite: true);
            return true;
        }
        catch
        {
            Delete(tmp);
            return false;
        }
    }
}

/// <summary>内容哈希。协议里只要求 MD5（AGENT.md §3.4）。</summary>
internal static class Hashing
{
    public static string Md5(byte[] data) => Convert.ToHexStringLower(MD5.HashData(data));

    public static string Md5File(string path)
    {
        using var s = Fs.OpenRead(path);
        return Convert.ToHexStringLower(MD5.HashData(s));
    }

    /// <summary>
    /// 异步文件哈希：客户端每个文件的校验都在并发路径上，
    /// 用同步读会白占线程池线程（AGENT.md §4.6 并发模型）。
    /// </summary>
    public static async Task<string> Md5FileAsync(string path, CancellationToken ct)
    {
        await using var s = Fs.OpenRead(path);
        var hash = await MD5.HashDataAsync(s, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
