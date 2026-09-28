namespace Ra3.BattleNet.Updater.Share.Models;

public static class ManifestFileExtensions
{
    /// <summary>相对路径（正斜杠），由 Path + FileName 合成。</summary>
    public static string RelativePath(this ManifestFile file)
    {
        var p = (file.Path ?? string.Empty).Replace('\\', '/').Trim('/');
        return p.Length == 0 ? file.FileName : p + "/" + file.FileName;
    }
}
