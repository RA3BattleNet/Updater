using Ra3.BattleNet.Updater.Share.Log;
using static Ra3.BattleNet.Updater.Share.Utilities.PublicMethod;
using Ra3.BattleNet.Updater.Share.Models;
using System.Diagnostics;
using System.Linq;
namespace Ra3.BattleNet.Updater.XmlGenerator
{
    internal class CommandLineOptions
    {
        public string? OldXmlPath { get; set; }
        public string TargetDir { get; set; }
        public string? NewXmlOutPutPath { get; set; }
        public List<string> ExcludeDirs { get; set; } = new();
        public void Parse(string[] args)
        {
            if (args.Contains("--help"))
            {
                ShowUsage();
                Environment.Exit(0);
            }
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--old-xmlpath":
                            OldXmlPath = args[++i];
                            break;
                        case "--target-dir":
                            TargetDir = args[++i];
                            break;
                        case "--new-xmloutputpath":
                            NewXmlOutPutPath = args[++i];
                            break;
                        case "--exclude-dirs":
                            ExcludeDirs = args[++i]
                                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(d => d.Trim().TrimStart('/', '\\'))
                                .ToList();
                            break;
                    }
                }

                // 验证必要参数
                if (string.IsNullOrEmpty(TargetDir))
                {
                    Logger.Fail("缺少必要参数\n");
                    ShowUsage();
                    Environment.Exit(-1);
                }
            }
            catch (Exception ex)
            {
                Logger.Fail($"参数无法解析{Environment.NewLine}Msg:{ex.Message}{Environment.NewLine}");
                ShowUsage();
                Environment.Exit(-2);
            }

        }
        public static void ShowUsage()
        {
            Console.WriteLine("用法:XmlGenerator.exe --target-dir <目录> --new-xmloutputpath <路径> [选项]");
            Console.WriteLine("");
            Console.WriteLine();
            Console.WriteLine("选项:");
            Console.WriteLine("  --old-xmlpath <路径>       指定旧的xml文件路径（可选）");
            Console.WriteLine("  --target-dir <目录>       必需，指定目标目录");
            Console.WriteLine("  --new-xmloutputpath <路径> 指定新生成的xml输出路径");
            Console.WriteLine("  --exclude-dirs <目录列表>  逗号分隔，这些顶层目录下的文件标记为 Skip 模式");
            Console.WriteLine("  --help                    显示帮助信息");
            Console.WriteLine("  --debug                   开启DEBUG调试");
            Console.WriteLine();
        }

    }


    internal class Program
    {
        private static string GetRelativePath(string basePath, string targetPath)
        {
            if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString()))
                basePath += Path.DirectorySeparatorChar;
            if (!targetPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
                targetPath += Path.DirectorySeparatorChar;

            var baseUri = new Uri(basePath);
            var targetUri = new Uri(targetPath);
            var relativeUri = baseUri.MakeRelativeUri(targetUri);

            string relativePath = Uri.UnescapeDataString(relativeUri.ToString())
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            return relativePath.Length == 0 ? Path.DirectorySeparatorChar.ToString() : Path.DirectorySeparatorChar + relativePath;
        }

        static void Main(string[] args)
        {
            if (args.Contains("--help"))
            {
                CommandLineOptions.ShowUsage();
                return;
            }
#if DEBUG
            Logger.IsDebug = true;
#else
            Logger.IsDebug = false;
            if (args.Contains("--debug"))
                Logger.IsDebug = true;
#endif
            CommandLineOptions options = new CommandLineOptions();
            options.Parse(args);
            Debug.Assert(!String.IsNullOrEmpty(options.NewXmlOutPutPath));
            Debug.Assert(!String.IsNullOrEmpty(options.TargetDir));

            ManifestModel? oldManifest = String.IsNullOrEmpty(options.OldXmlPath) ? null : new ManifestModel(options.OldXmlPath);
            ManifestModel newManifest = new ManifestModel(new Version(1, 0, 0), $"自动生成-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");

            string[] allfiles = Directory.GetFiles(options.TargetDir, "*.*", SearchOption.AllDirectories);

            string BasePath = Path.GetFullPath(options.TargetDir);
            foreach (string filepath in allfiles)
            {
                string FullPath = Path.GetFullPath(filepath);
                Logger.Info($"处理 {FullPath}{Environment.NewLine}");

                string FileName = Path.GetFileName(FullPath);
                // .project.nuget.cache

                string RelativePath = GetRelativePath(BasePath, Path.GetDirectoryName(FullPath));
                // \Ra3.BattleNet.Updater.Server\obj

                var file = new ManifestFile(Guid.NewGuid(),
                    FileName,
                    BitConverter.ToString(GetMD5(FullPath)).Replace("-", "").ToLower(),
                    RelativePath,
                    (new Version(1, 0, 0)).ToString());

                var topDir = RelativePath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                topDir = topDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (options.ExcludeDirs.Any(d => topDir.Equals(d, StringComparison.OrdinalIgnoreCase)))
                {
                    file.Mode = FileModeEnum.Skip;
                }

                newManifest.Manifest.Files.Add(file);
            }

            if (oldManifest != null)
                foreach (ManifestFile item in oldManifest.Manifest.Files)
                {
                    #region 文件名和路径相同但文件可能发生修改
                    ManifestFile? target = newManifest.Manifest.Files.FirstOrDefault(f =>
                    f.FileName == item.FileName &&
                    f.Path == item.Path);
                    if (target != null)
                    {
                        // 继承
                        Logger.Info($"存在旧文件 {item.Path} {item.FileName}{Environment.NewLine}");
                        target.UUID = item.UUID;
                        target.Version = new Version(item.Version.Major, item.Version.Minor, item.Version.Build + 1);
                        target.Type = item.Type;
                        target.Mode = item.Mode;
                        target.KindOf = item.KindOf;
                        continue;
                    }
                    #endregion

                    #region 文件相同但是文件名或路径发生修改
                    ManifestFile? target2 = newManifest.Manifest.Files.FirstOrDefault(f => f.MD5 == item.MD5);
                    if (target2 != null)
                    {
                        // 继承
                        Logger.Info($"存在文件移动 {item.Path} {item.FileName} => {target2.Path} {target2.FileName}{Environment.NewLine}");
                        target2.UUID = item.UUID;
                        target2.Version = new Version(item.Version.Major, item.Version.Minor, item.Version.Build + 1);
                        target2.Type = item.Type;
                        target2.Mode = item.Mode;
                        target2.KindOf = item.KindOf;
                        continue;
                    }
                    #endregion

                    #region 认为新版本无此文件
                    Logger.Info($"存在例外文件：{item.Path} {item.FileName}{Environment.NewLine}");
                    #endregion

                }
            
            newManifest.SaveToXml(options.NewXmlOutPutPath);

        }

    }
}
