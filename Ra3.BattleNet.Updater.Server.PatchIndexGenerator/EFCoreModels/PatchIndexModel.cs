using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ra3.BattleNet.Updater.Server.PatchIndexGenerator.EFCoreModels
{
    public class PatchIndexModel
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        // 文件唯一标识 (GUID)
        [Required]
        [MaxLength(16)] // 存储为16字节BLOB
        public byte[] FileGuid { get; set; }

        // 旧版文件内容哈希 (MD5)
        [Required]
        [MaxLength(16)] // 16字节BLOB
        public byte[] OldContentHash { get; set; }
        
        // 新版文件内容哈希 (MD5)
        [Required]
        [MaxLength(16)] // 16字节BLOB
        public byte[] NewContentHash { get; set; }

        // Patch文件名
        [Required]
        [MaxLength(16)] // 16字节BLOB
        public byte[] PatchName { get; set; }

        [Required]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public DateTime AddedDate { get; set; }

        [Required]
        public byte MajorVersion { get; set; }

        [Required]
        public byte MinorVersion { get; set; }

        [Required]
        public byte BuildVersion { get; set; }
    }
}
