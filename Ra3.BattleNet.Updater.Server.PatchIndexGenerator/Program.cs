using Ra3.BattleNet.Updater.Server.PatchIndexGenerator.EFCoreModels;
using Ra3.BattleNet.Updater.Share;
using Ra3.BattleNet.Updater.Share.Log;
using Ra3.BattleNet.Updater.Share.Models;
using Ra3.BattleNet.Updater.Share.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using static Ra3.BattleNet.Updater.Server.API;

namespace Ra3.BattleNet.Updater.Server.PatchIndexGenerator
{
    internal class CommandLineOptions
    {
        public string? ManifestPath { get; set; }
        public string? ManifestRootPath { get; set; } // 用于指定清单文件的根目录
        public string? OutputPath { get; set; }
    }

    internal static class CommandLineParser
    {
        public static CommandLineOptions Parse(string[] args)
        {
            CommandLineOptions options = new CommandLineOptions();

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--manifest":
                            options.ManifestPath = args[++i];
                            break;
                        case "--manifest-root":
                            options.ManifestRootPath = args[++i];
                            break;
                        case "--output":
                            options.OutputPath = args[++i];
                            break;
                    }
                }

                // 验证必要参数
                if (string.IsNullOrEmpty(options.ManifestPath))
                {
                    Logger.Fail("缺少必要参数\n");
                    ShowUsage();
                    Environment.Exit(-1);
                }

                options.OutputPath ??= Directory.GetCurrentDirectory();

                return options;
            }
            catch (Exception ex)
            {
                Logger.Fail($"参数无法解析{Environment.NewLine}Msg:{ex.Message}");
                ShowUsage();
                Environment.Exit(-2);
            }
            return options;
        }

        public static void ShowUsage()
        {
            Console.Write("使用方式:\n");
            Console.Write("--manifest [路径] 最新版本清单文件\n");
            Console.Write("--manifest-root [路径] 最新版本清单文件的根目录\n");
            Console.Write("--output [路径]   patchesindex.html最终输出目录 (可选，默认当前目录)\n");
            Console.Write("--debug  输出更多日志\n");
        }
    }

    internal class Program
    {
        static void Main(string[] args)
        {
#if DEBUG
            Logger.IsDebug = true;
#else
            Logger.IsDebug = false;
            Logger.IsDebug = args.Contains("--debug");
#endif
            Logger.Info("Updater Server init\n");
            Logger.Debug($"args: {string.Join(" ", args)}{Environment.NewLine}");

            // 解析
            CommandLineOptions options = CommandLineParser.Parse(args);

            Debug.Assert(options.ManifestPath != null);
            Debug.Assert(options.OutputPath != null);

            Logger.Info($"加载最新 manifest: {options.ManifestPath}\n");
            ManifestModel manifest = new ManifestModel(options.ManifestPath);

            using var db = new UpdaterContext();

            Logger.Debug($"数据库路径：{db.DbPath}\n");

            var oriCount = db.PatchIndexes.Count();

            string filesDir = Path.Combine(options.OutputPath, "files");
            string patchesDir = Path.Combine(options.OutputPath, "patches");
            Directory.CreateDirectory(filesDir);
            Directory.CreateDirectory(patchesDir);

            foreach (var file in manifest.Manifest.Files)
            {
                var fileGuidBytes = file.UUID.ToByteArray();
                var newHash = Convert.FromHexString(file.MD5);
                string newMd5 = file.MD5.ToLowerInvariant();

                // 复制新文件到 files 目录下（以MD5为文件名）
                string srcPath = Path.Combine(
                    options.ManifestRootPath ?? Path.GetDirectoryName(options.ManifestPath) ?? "",
                    file.Path.TrimStart('/', '\\'),
                    file.FileName);
                string destPath = Path.Combine(filesDir, newMd5);

                try
                {
                    if (File.Exists(srcPath))
                    {
                        File.Copy(srcPath, destPath, true);
                        //Logger.Info($"已复制文件: {srcPath} -> {destPath}\n");
                    }
                    else
                    {
                        Logger.Warning($"源文件不存在: {srcPath}\n");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Fail($"复制文件失败: {srcPath} -> {destPath}\n{ex.Message}\n");
                }

                // 第一次添加，无文件UUID出现过，则添加（新旧HASH都为此文件HASH）
                // 不是第一次添加，但是也没有找到这个最新版的，就添加并生成邻近的至多3个版本的patch到程序同目录的files目录下(新旧HASH分别为新旧文件MD5，patchName为生成补丁名，邻近三个版本不要依赖版本号，依赖添加时间判断远近)
                // 不是第一次添加，表格中已经有了此最新版了（p.NewContentHash == Convert.FromHexString(file.MD5)），log并跳过

                // 查询所有历史版本（同UUID，按添加时间倒序）
                
                var history = db.PatchIndexes
                    .Where(p => p.FileGuid == fileGuidBytes)
                    .OrderByDescending(p => p.AddedDate)
                    .ToList();

                bool hasCurrentVersion = history.Any(p => p.NewContentHash.SequenceEqual(newHash));

                if (!history.Any())
                {
                    // 第一次发现此文件，此时新旧HASH相同，且patchName为Guid.Empty,即（"00000000-0000-0000-0000-000000000000"）
                    db.PatchIndexes.Add(new PatchIndexModel
                    {
                        FileGuid = fileGuidBytes,
                        OldContentHash = newHash,
                        NewContentHash = newHash,
                        PatchName = Guid.Empty.ToByteArray(),
                        MajorVersion = (byte)file.Version.Major,
                        MinorVersion = (byte)file.Version.Minor,
                        BuildVersion = (byte)file.Version.Build,
                        AddedDate = DateTimeOffset.UtcNow.DateTime,
                    });
                    //Logger.Success($"首次添加文件 {file.FileName} ({file.UUID})，已写入数据库。\n");
                }
                else if (!hasCurrentVersion)
                {
                    // 找到history中v1的MD5
                    PatchIndexModel? V1_V1_History = db.PatchIndexes.Where(p => p.FileGuid==fileGuidBytes)
                        .FirstOrDefault(p => p.OldContentHash == p.NewContentHash);
                    Debug.Assert(V1_V1_History != null, $"未找到文件{file.FileName}在数据库中的初始版本记录");
                    // 找到history中v1到vx版本的记录，以找到时间降序的线性历史记录
                    var V1_Vx_History = db.PatchIndexes.Where(p => p.FileGuid == fileGuidBytes)
                        .Where(p=>p.OldContentHash==V1_V1_History.OldContentHash)
                        .OrderByDescending(p => p.AddedDate);
                    // 不是第一次添加，但没有找到最新版的记录
                    // 按添加时间最近的至多3个做patch cache
                    List<PatchIndexModel> nearest = V1_Vx_History.ToList().Take(3).ToList();
                    Debug.Assert(nearest.Count > 0, $"未找到文件 {file.FileName} ({file.UUID}) 在数据库中的初始版本记录");

                    foreach (var old in nearest)
                    {
                        // 生成补丁文件名
                        Guid PatchNameGuid = Guid.NewGuid();
                        
                        string oldMd5 = BitConverter.ToString(old.NewContentHash).Replace("-", "").ToLowerInvariant();
                        string patchName = PatchNameGuid.ToString("N");
                        string patchPath = Path.Combine(patchesDir, patchName);

                        // 历史文件路径和新文件路径都以MD5为文件名
                        string oldFilePath = Path.Combine(filesDir, oldMd5);
                        string newFilePath = destPath;

                        if (!File.Exists(oldFilePath))
                        {
                            Logger.Fail($"历史文件不存在，无法生成补丁: {oldFilePath}\n");
                            continue;
                        }
                        if (!File.Exists(newFilePath))
                        {
                            Logger.Fail($"新文件不存在，无法生成补丁: {newFilePath}\n");
                            continue;
                        }

                        Logger.Info($"生成补丁中：{oldFilePath}-{newFilePath}=>{patchPath}");
                        bool patchOk = PatchGenerater.GeneratePatch(oldFilePath, newFilePath, patchPath);

                        if (patchOk)
                        {
                            Logger.Success($"已为 {file.FileName} ({file.UUID}) 生成补丁: {patchPath}\n");
                        }
                        else
                        {
                            Logger.Fail($"未能为 {file.FileName} ({file.UUID}) 生成补丁: {patchPath}\n");
                        }

                        db.PatchIndexes.Add(new PatchIndexModel
                        {
                            FileGuid = fileGuidBytes,
                            OldContentHash = old.NewContentHash ,
                            NewContentHash = newHash,
                            PatchName = PatchNameGuid.ToByteArray(),
                            MajorVersion = (byte)file.Version.Major,
                            MinorVersion = (byte)file.Version.Minor,
                            BuildVersion = (byte)file.Version.Build,
                            AddedDate = DateTimeOffset.UtcNow.DateTime,
                        });
                        Logger.Success($"新版本文件 {file.FileName} ({file.UUID})，已写入数据库。\n");

                    }
                }
                else
                {
                    Logger.Debug($"已存在文件 {file.FileName} ({file.UUID}) 的该版本，跳过写入。\n");
                }
            }
            db.SaveChanges();
            Logger.Success($"已写入 {db.PatchIndexes.Count() - oriCount} 条记录到数据库\n");

            // 生成 patches.json，只保留每个UUID最近10条补丁记录
            List<PatchIndexModel> allPatches = db.PatchIndexes
                .Where(p => !p.OldContentHash.SequenceEqual(p.NewContentHash)) // 只导出补丁记录
                .OrderByDescending(p => p.AddedDate)
                .ToList();

            // 按UUID分组，每组取最近10条
            var grouped = allPatches
                .GroupBy(p => new Guid(p.FileGuid))
                .SelectMany(g => g.Take(10))
                .Select(p => new
                {
                    UUID = new Guid(p.FileGuid).ToString("N"),
                    PatchName = new Guid(p.PatchName).ToString("N"),
                    OldMD5 = BitConverter.ToString(p.OldContentHash).Replace("-", "").ToLowerInvariant(),
                    NewMD5 = BitConverter.ToString(p.NewContentHash).Replace("-", "").ToLowerInvariant(),
                    //Date = p.AddedDate.ToString("yyyy-MM-dd HH:mm:ss"),
                    //MajorVersion = p.MajorVersion,
                    //MinorVersion = p.MinorVersion,
                    //BuildVersion = p.BuildVersion
                })
                .ToList();

            string patchesJsonPath = Path.Combine(options.OutputPath, "patches.json");
            string json = System.Text.Json.JsonSerializer.Serialize(grouped, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(patchesJsonPath, json, Encoding.UTF8);

            Logger.Success($"patches.json 已生成: {patchesJsonPath}\n");
        }
    }
}
