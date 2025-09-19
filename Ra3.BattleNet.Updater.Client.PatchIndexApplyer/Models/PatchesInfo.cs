using Newtonsoft.Json;

namespace Ra3.BattleNet.Updater.Client.PatchIndexApplyer.Models
{
    // Patch信息类
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
        public required Guid OldMD5 { get; set; }

        [JsonProperty("NewMD5")]
        [JsonRequired]
        public required Guid NewMD5 { get; set; }
    }

    // 补丁索引类，提供多种搜索方式
    public class PatchIndex
    {
        private readonly List<PatchInfo> _allPatches;
        private readonly Dictionary<Guid, PatchInfo> _byUuid;
        private readonly Dictionary<Guid, PatchInfo> _byPatchName;
        private readonly Dictionary<Guid, PatchInfo> _byOldMd5;
        private readonly Dictionary<Guid, PatchInfo> _byNewMd5;

        public PatchIndex(string jsonContent)
        {
            // 反序列化JSON
            _allPatches = JsonConvert.DeserializeObject<List<PatchInfo>>(jsonContent);

            // 构建索引
            _byUuid = new Dictionary<Guid, PatchInfo>();
            _byPatchName = new Dictionary<Guid, PatchInfo>();
            _byOldMd5 = new Dictionary<Guid, PatchInfo>();
            _byNewMd5 = new Dictionary<Guid, PatchInfo>();

            foreach (var patch in _allPatches)
            {
                _byUuid[patch.UUID] = patch;
                _byPatchName[patch.PatchName] = patch;
                _byOldMd5[patch.OldMD5] = patch;
                _byNewMd5[patch.NewMD5] = patch;
            }
        }

        // 通过UUID查找补丁
        public PatchInfo FindByUuid(Guid uuid)
        {
            return _byUuid.TryGetValue(uuid, out var patch) ? patch : null;
        }

        // 通过补丁名称查找
        public PatchInfo FindByPatchName(Guid patchName)
        {
            return _byPatchName.TryGetValue(patchName, out var patch) ? patch : null;
        }

        // 通过旧文件MD5查找
        public PatchInfo FindByOldMd5(Guid oldMd5)
        {
            return _byOldMd5.TryGetValue(oldMd5, out var patch) ? patch : null;
        }

        // 通过新文件MD5查找
        public PatchInfo FindByNewMd5(Guid newMd5)
        {
            return _byNewMd5.TryGetValue(newMd5, out var patch) ? patch : null;
        }

        // 获取所有补丁
        public IReadOnlyList<PatchInfo> GetAllPatches()
        {
            return _allPatches.AsReadOnly();
        }
    }

}