using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ra3.BattleNet.Updater.Share.Models
{
    // 补丁操作模型
    public class PatchOperation
    {
        public OperationTypeEnum Type { get; set; }
        public ManifestFile File { get; set; }
        public string? SourcePath { get; set; }    // 用于新增文件
        public string? PatchPath { get; set; }     // 用于补丁文件
        public long PatchSize { get; set; }
        public string? SourceMD5 { get; set; }     // 旧文件MD5
        public string? TargetMD5 { get; set; }     // 新文件MD5
        public string? RelativePath { get; set; }  // 在补丁包中的相对路径
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
    public class PatchManifest
    {
        public string? BaseVersion { get; set; }
        public string? TargetVersion { get; set; }
        public List<OperationInfo>? Operations { get; set; }
    }

    public class OperationInfo
    {
        public string? Type { get; set; }
        public string? FilePath { get; set; } // 原始文件路径
        public string? UUID { get; set; } // 文件唯一标识
        public string? RelativePath { get; set; } // 在补丁包中的路径
        public long Size { get; set; } // 文件或补丁大小
        public string? SourceMD5 { get; set; }
        public string? TargetMD5 { get; set; } 
    }

}
