namespace Ra3.BattleNet.Updater.Share.Models;

/// <summary>
/// 服务端静态树的对象命名（AGENT.md §3.2 / §3.3）——**唯一实现处**，客户端与服务端都从这里取。
///
/// 为什么带 <c>.bin</c> 后缀：Cloudflare 的边缘缓存**只按扩展名白名单**决定缓不缓存，
/// 无扩展名的对象（老的 <c>files/{md5}</c>）与 <c>.hdiff</c> 都进不了白名单（实测全是
/// <c>cf-cache-status: DYNAMIC</c>），而 <c>.bin</c> 实测 MISS→HIT，且**缓存命中时 Range 仍返回 206**，
/// 续传不受影响。这些对象都是内容寻址（md5 定名）、永不变更，缓存它们没有任何正确性风险。
///
/// 注意：<c>manifest.xml</c> **故意保持 <c>.xml</c>** —— 它是唯一会变的对象，CF 不缓存它反而是好事
/// （否则"客户端永远以为自己最新"），而 <c>text/xml</c> 能吃到 CF 的 br 压缩（19,507 → 3,264 B）。
/// </summary>
public static class UpdaterProtocol
{
    /// <summary>清单文件名（唯一会变的对象）。</summary>
    public const string ManifestFileName = "manifest.xml";

    /// <summary>完整文件所在目录。</summary>
    public const string FilesDir = "files";

    /// <summary>补丁所在目录。</summary>
    public const string PatchesDir = "patches";

    /// <summary>完整文件的文件名：内容寻址 + <c>.bin</c>（为了进 CF 的默认缓存白名单）。</summary>
    public static string FullFileName(string md5) => md5 + ".bin";

    /// <summary>补丁的文件名：内容对命名 + <c>.bin</c>。</summary>
    public static string PatchFileName(string oldMd5, string newMd5) => $"{oldMd5}_{newMd5}.bin";

    /// <summary>完整文件相对路径：<c>files/{md5}.bin</c>。</summary>
    public static string FullRelativePath(string md5) => $"{FilesDir}/{FullFileName(md5)}";

    /// <summary>补丁相对路径：<c>patches/{old}_{new}.bin</c>。</summary>
    public static string PatchRelativePath(string oldMd5, string newMd5) =>
        $"{PatchesDir}/{PatchFileName(oldMd5, newMd5)}";
}
