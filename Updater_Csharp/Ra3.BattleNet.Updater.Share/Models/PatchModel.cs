namespace Ra3.BattleNet.Updater.Share.Models
{
    // 补丁操作模型
    public class PatchOperation
    {
        public OperationTypeEnum Type { get; set; }
        public ManifestFile File { get; set; }
        public string? SourcePath { get; set; }    // 用于新增文件 / 移动：新版本文件的本地路径
        public string? PatchPath { get; set; }     // 用于补丁文件
        public long PatchSize { get; set; }
        public string? SourceMD5 { get; set; }     // 旧文件MD5
        public string? TargetMD5 { get; set; }     // 新文件MD5
        public string? RelativePath { get; set; }  // 在补丁包中的相对路径
        public string? OldFilePath { get; set; }   // 旧版本文件的相对路径（客户端据此找到待打补丁的本地文件）
    }

    /// <summary>
    /// 操作类型枚举
    /// </summary>
    public enum OperationTypeEnum
    {
        ForceCopy,
        Patch,
        Move
    }

    // 补丁清单模型
    // 【2026-09-28】删掉 BaseVersion / TargetVersion：它们**写而不读**
    // （生成端填的是清单根版本、恒 "1.0.0"；应用端只遍历 Operations）。
    // 反序列化容忍未知字段，所以老包里带着这两个键也照样能读。
    public class PatchManifest
    {
        public List<OperationInfo>? Operations { get; set; }
    }

    public class OperationInfo
    {
        public string? Type { get; set; }

        /// <summary>新版本文件的相对路径（客户端的目标位置）。</summary>
        public string? FilePath { get; set; }

        /// <summary>旧版本文件的相对路径；Patch / Move 需要它来定位本地前身。</summary>
        public string? OldFilePath { get; set; }

        public string? UUID { get; set; }

        /// <summary>在补丁包中的载荷路径（完整文件或补丁）。</summary>
        public string? RelativePath { get; set; }

        public long Size { get; set; }
        public string? SourceMD5 { get; set; }
        public string? TargetMD5 { get; set; }
    }
}
