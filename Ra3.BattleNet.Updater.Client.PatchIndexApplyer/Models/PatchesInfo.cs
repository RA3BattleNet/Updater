using Newtonsoft.Json;

namespace Ra3.BattleNet.Updater.Client.PatchIndexApplyer.Models
{
    public class PatchInfo
    {
        [JsonProperty("UUID")]
        [JsonRequired]
        public required Guid UUID { get; set; }

        [JsonProperty("PatchName")]
        [JsonRequired]
        public required Guid PatchName { get; set; }

        [JsonProperty("OldMD5")]
        [JsonRequired]
        public required string OldMD5 { get; set; }

        [JsonProperty("NewMD5")]
        [JsonRequired]
        public required string NewMD5 { get; set; }
    }

    // 修正后的补丁索引类
    public class PatchIndex
    {
        private readonly List<PatchInfo> _allPatches;

        private readonly Dictionary<string, PatchInfo> _patchByKey;

        public PatchIndex(string jsonContent)
        {
            // 反序列化JSON
            _allPatches = JsonConvert.DeserializeObject<List<PatchInfo>>(jsonContent);

            // 构建复合键索引
            _patchByKey = new Dictionary<string, PatchInfo>();
            foreach (var patch in _allPatches)
            {
                string key = $"{patch.UUID.ToString("N")}|{patch.OldMD5}|{patch.NewMD5}";
                _patchByKey[key] = patch;
            }
        }

        /// <summary>
        /// 根据文件UUID、旧MD5和新MD5查找适用的补丁
        /// </summary>
        /// <param name="uuid">文件UUID</param>
        /// <param name="oldMd5">旧文件MD5</param>
        /// <param name="newMd5">新文件MD5</param>
        /// <returns>找到的补丁信息，如果找不到则返回null</returns>
        public Guid? FindPatch(Guid uuid, string oldMd5, string newMd5)
        {
            string key = $"{uuid.ToString("N")}|{oldMd5}|{newMd5}";
            return _patchByKey.TryGetValue(key, out var patch) ? patch.PatchName : null;
        }

        /// <summary>
        /// 获取所有补丁记录
        /// </summary>
        public IReadOnlyList<PatchInfo> GetAllPatches()
        {
            return _allPatches.AsReadOnly();
        }
    }
}