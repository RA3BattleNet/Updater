using Ra3.BattleNet.Updater.Share;
using System.Diagnostics;
using System.Xml;

namespace Ra3.BattleNet.Updater.Server
{
    internal class Program
    {
        /// <summary>
        /// stdin输入：旧版本manifest.xml
        /// 功能：解析旧版本manifest和新版本对比（更新版本需要其他程序），对相关文件进行处理、标记、汇总，输出新的manifest.xml，并将整体打包
        /// 输出：打包的patch（包含新manifest.xml，解析规则，和所有对应的文件）
        /// </summary>
        /// <param name="args"></param>
        static int Main(string[] args)
        {
            //Logger.Path = "./temp.logger.txt";
            //Logger.IsDebug = true;

            Logger.Info("Updater Server init\n");
            Logger.Info("XML From Stdin:\n");

            #region stdin读取manifest.xml
            XmlDocument inputStdinXml = new XmlDocument();
#if DEBUG
            inputStdinXml.Load(@"./example.old.xml");
#else
            string xmlInput = Console.In.ReadToEnd(); // Windows下Ctrl+Z Enter
            try
            {
                inputStdinXml.LoadXml(xmlInput);
            }
            catch (Exception e)
            {
                Logger.Fail($"XML 解析失败: {e.Message}\n");
                return -1;
            }
#endif
            Logger.Success("XML Loaded\n");
            #endregion

            // 解析旧版本manifest和新版本对比（更新版本需要其他程序），对相关文件进行处理、标记、汇总，输出新的manifest.xml，并将整体打包
            #region 解析manifest.xml
            XmlNode? MetadataNode = inputStdinXml.SelectSingleNode("/Metadata");
            if (MetadataNode == null)
            {
                Logger.Fail("XML 解析失败: Metadata 节点不存在\n");
                return -2;
            }

            #endregion
            Logger.Debug($"客户端版本manifest.xml读取信息：\n");
            ManifestModel old_manifest = new ManifestModel(MetadataNode);
            old_manifest.Manifest.Files.ForEach(_ =>
            {
                Logger.Debug($"UUID: {_.UUID.ToString()}\n");
                Logger.Debug($"FileName: {_.FileName}\n");
                Logger.Debug($"MD5: {_.MD5}\n");
                Logger.Debug($"Path: {_.Path}\n");
                Logger.Debug($"Version: {_.Version}\n");
                Logger.Debug($"Type: {_.Type}\n");
                Logger.Debug($"Mode: {_.Mode}\n");
                Logger.Debug($"KindOf: {_.KindOf}\n");
                Logger.Debug($"\n");
            });
            Logger.Info($"客户端：v{old_manifest.Version}\tGTime {old_manifest.Tags.GenTime.DateTime.ToLocalTime()}\n");
            Logger.Info($"版本Commit：{old_manifest.Tags.Commit}");

            ManifestModel new_manifest = new ManifestModel(new Version(1,0,1),"此为手动创建的新版本");
            foreach (ManifestFile item in old_manifest.Manifest.Files)
            {
                var FileUUID = item.UUID;
                var FileVersion = new Version(item.Version.Major, item.Version.Minor, item.Version.Build + 1);
                var FileName = item.FileName;
                var FilePath = item.Path;
                var FileMD5 = PublicMethod.GetMD5(Path.Combine(FilePath, FileName));
                var FileType = item.Type;
                var FileMode = item.Mode;
                var FileKindOf = item.KindOf;
            }
            //ManifestFile b = old_manifest.Manifest.Files.First(_ => _.FileName == "Lyi.dll");
            // 逻辑有误，需要同时能够获取新旧版本文件，单纯stdin获取单个xml不够，此处需要重新设计
            return 0;
        }
    }
}
