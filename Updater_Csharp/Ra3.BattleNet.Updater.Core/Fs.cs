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
}
