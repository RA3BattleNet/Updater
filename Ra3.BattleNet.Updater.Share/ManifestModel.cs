using System.ComponentModel.DataAnnotations;
using System.Xml;

namespace Ra3.BattleNet.Updater.Share
{
    /// <summary>
    /// 表示整个清单文件的根对象
    /// </summary>
    public class ManifestModel
    {
        private bool _isImported = false;
        /// <summary>
        /// 清单文件版本号 (例如 "1.0.0")
        /// </summary>
        private Version _version;

        /// <summary>
        /// 标签信息，包含UUID、生成时间和提交信息
        /// </summary>
        private Tags _tags;

        /// <summary>
        /// 包含的子模块（当前未使用）
        /// </summary>
        private Includes _includes;

        /// <summary>
        /// 文件清单列表
        /// </summary>
        private Manifest _manifest;

        public Version Version
        {
            get => _version;
            private set => _version = value;
        }

        /// <summary>
        /// 标签信息，包含UUID、生成时间和提交信息
        /// </summary>
        public Tags Tags
        {
            get => _tags;
            set => _tags = value;
        }

        /// <summary>
        /// 包含的子模块（当前未使用）
        /// </summary>
        public Includes Includes
        {
            get => _includes;
            set
            {
                if (_isImported)
                    throw new InvalidOperationException("XML导入的实例不允许修改子模块");
                _includes = value;
            }
        }

        /// <summary>
        /// 文件清单列表
        /// </summary>
        public Manifest Manifest
        {
            get => _manifest;
            set
            {
                if (_isImported)
                    throw new InvalidOperationException("XML导入的实例不允许修改清单");
                _manifest = value;
            }
        }
        //public Manifest Manifest
        //{
        //    get => Manifest;
        //    private set {
        //        if (isImported)
        //        {
        //            throw new InvalidOperationException("XML ");
        //        }
        //    }
        //}

        /// <summary>
        /// 提供空白Metadata，用于创建新的ManifestModel对象
        /// </summary>
        public ManifestModel(Version _version, String _commit = "")
        {
            _isImported = false;
            this._version = _version;
            this._tags = new Tags(_commit);
            this._includes = new Includes();
            this._manifest = new Manifest();
        }

        /// <summary>
        /// 提供Metadata的XML节点，将XML序列化，解析ManifestModel对象（暂无合法检查）
        /// </summary>
        /// <param name="MNode">Metadata的XML结点</param>
        public ManifestModel(XmlNode MNode)
        {
            _isImported = true;
            // 解析XML节点
            this._version = new Version(MNode.Attributes["Version"].Value);
            _tags = new Tags(MNode.SelectSingleNode("Tags"));
            _includes = new Includes();
            _manifest = new Manifest(MNode.SelectSingleNode("Manifest"));
        }
    }

    /// <summary>
    /// 标签信息
    /// </summary>
    public class Tags
    {
        /// <summary>
        /// 标准32位数字UUID格式
        /// </summary>
        [Required]
        public Guid UUID { get; set; }

        /// <summary>
        /// 运行计算机上距离1970-01-01 00:00:00 UTC到现在的秒数
        /// </summary>
        [Required]
        public DateTimeOffset GenTime { get; set; }

        /// <summary>
        /// 任意提交信息，不影响程序运行
        /// </summary>
        public string Commit { get; set; }

        /// <summary>
        /// 读取XML节点，解析Tags对象（暂无合法检查）
        /// </summary>
        /// <param name="TagsNode"></param>
        public Tags(XmlNode TagsNode)
        {
            UUID = new Guid((TagsNode["UUID"].InnerText));
            GenTime = DateTimeOffset.FromUnixTimeSeconds(long.Parse(TagsNode["GenTime"].InnerText));
            Commit = TagsNode["Commit"].InnerText;
        }

        /// <summary>
        /// 创建新Tags对象
        /// </summary>
        /// <param name="_commit"></param>
        public Tags(String _commit = "")
        {
            UUID = Guid.NewGuid();
            GenTime = DateTimeOffset.UtcNow;
            Commit = _commit;
        }
    }

    /// <summary>
    /// 包含的子模块（当前未使用）
    /// </summary>
    public class Includes
    {
        // 根据XML，当前Includes为空，未来可能需要扩展
    }

    /// <summary>
    /// 文件清单
    /// </summary>
    public class Manifest
    {
        /// <summary>
        /// 文件列表
        /// </summary>
        public List<ManifestFile> Files { get; set; } = new List<ManifestFile>();

        /// <summary>
        /// 文件夹列表
        /// </summary>
        public List<ManifestFolder> Folders { get; set; } = new List<ManifestFolder>();

        public Manifest()
        {
        }

        /// <summary>
        /// 从XML中读取清单文件列表和文件夹列表（暂无合法检查）
        /// </summary>
        /// <param name="MNode"></param>
        public Manifest(XmlNode MNode)
        {
            XmlNodeList? FileNodes = MNode.SelectNodes("File");

            if (FileNodes != null)
            {
                foreach (XmlNode item in FileNodes)
                {
                    var tempuuid = new Guid(item["UUID"].InnerText);
                    if (Files.Any(_ => _.UUID == tempuuid))
                    {
                        Logger.Warning($"Manifest 中存在重复的文件\n");
                        Logger.Debug($"{item.OuterXml}\n");
                        continue;
                    }
                    ManifestFile temp = new ManifestFile(tempuuid,
                        item["FileName"].InnerText,
                        item["MD5"].InnerText,
                        item["Path"].InnerText,
                        item["Version"].InnerText,
                        byte.Parse(item["Type"].InnerText),
                        byte.Parse(item["Mode"].InnerText),
                        item["KindOf"].InnerText);
                    Files.Add(temp);
                }
            }

            // TO DO 暂无支持
            //XmlNodeList? FolderNodes = MNode.SelectNodes("Folder");
            //if (FolderNodes != null)
            //{
            //    foreach (XmlNode item in FolderNodes)
            //    {
            //    }
            //}
        }
    }

    /// <summary>
    /// 清单中的文件项
    /// </summary>
    public class ManifestFile
    {
        /// <summary>
        /// 文件唯一特征符（不随文件哈希）
        /// </summary>
        [Required]
        public Guid UUID { get; set; }

        /// <summary>
        /// 文件名
        /// </summary>
        [Required]
        public string FileName { get; set; }

        /// <summary>
        /// 文件MD5哈希值
        /// </summary>
        [Required]
        public string MD5 { get; set; }

        /// <summary>
        /// 文件相对路径
        /// </summary>
        [Required]
        public string Path { get; set; }

        /// <summary>
        /// 文件版本
        /// </summary>
        [Required]
        public Version Version { get; set; }

        /// <summary>
        /// 文件类型 (0:Bin, 1:Text)
        /// </summary>
        public byte Type { get; set; } = 0;

        /// <summary>
        /// 程序处理模式 (0:Auto, 1:Force, 2:Skip)
        /// </summary>
        public byte Mode { get; set; } = 0;

        /// <summary>
        /// 文件种类标识 (例如 "APPLICATION;PROGRAM;")
        /// </summary>
        public string KindOf { get; set; } = String.Empty;

        /// <summary>
        /// 快速初始化
        /// </summary>
        public ManifestFile(Guid _uuid, string _filename, string _md5, string _path, string _version, byte _type = 0, byte _mode = 0, string _kingof = "")
        {
            if (_uuid == Guid.Empty)
                throw new ArgumentException("UUID 不能为空", nameof(_uuid));

            if (string.IsNullOrWhiteSpace(_filename))
                throw new ArgumentException("文件名不能为空", nameof(_filename));
            if (_filename.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("文件名包含非法字符", nameof(_filename));

            if (string.IsNullOrWhiteSpace(_md5))
                throw new ArgumentException("MD5 不能为空", nameof(_md5));
            if (_md5.Length != 32 || !_md5.All("0123456789abcdefABCDEF".Contains))
                throw new ArgumentException("MD5 格式非法", nameof(_md5));

            if (string.IsNullOrWhiteSpace(_path))
                throw new ArgumentException("路径不能为空", nameof(_path));
            if (_path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
                throw new ArgumentException("路径包含非法字符", nameof(_path));

            UUID = _uuid;
            FileName = _filename;
            MD5 = _md5;
            Path = _path;
            Version = new Version(_version);
            Type = _type;
            Mode = _mode;
            KindOf = _kingof;
        }
    }

    /// <summary>
    /// 清单中的文件夹项
    /// </summary>
    public class ManifestFolder
    {
        /// <summary>
        /// 文件夹名
        /// </summary>
        public string FolderName { get; set; }

        /// <summary>
        /// 相对路径
        /// </summary>
        public string Path { get; set; }

        /// <summary>
        /// 是否递归包括文件夹内部所有文件和文件夹
        /// </summary>
        public bool Recursion { get; set; }

        /// <summary>
        /// 文件类型 (0:Bin, 1:Text)
        /// </summary>
        public byte Type { get; set; }

        /// <summary>
        /// 程序处理模式 (0:Auto, 1:Force, 2:Skip)
        /// </summary>
        public byte Mode { get; set; }
    }

}

