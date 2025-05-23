using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Ra3.BattleNet.Updater
{
    internal class Program
    {
        static int Main(string[] args)
        {
            #region stdin读取manifest.xml
            XmlDocument inputStdinXml = new XmlDocument();
#if DEBUG
            inputStdinXml.Load(@"./temp.1.5.2.0.xml");
#else
            string xmlInput = Console.In.ReadToEnd();
            try
            {
                inputStdinXml.LoadXml(xmlInput);
            }
            catch (Exception e)
            {
                Logger(_loggerT.Fail, $"XML 解析失败: {e.Message}\n");
                return -1;
            }
#endif

            //Logger(_loggerT.Success, "XML 解析成功\n");
            #endregion
            //Logger(_loggerT.DEBUG, $"{inputStdinXml.SelectSingleNode("/Metadata/Manifest/File")?["FileName"]?.InnerText}{Environment.NewLine}");
            //Logger(_loggerT.DEBUG, $"{inputStdinXml.SelectSingleNode("/Metadata/Manifest/File")?["RelativePath"]?.InnerText}{Environment.NewLine}");

            // 获取文件输入XML

            //string v1 = @"./EnhancerCorona.old.dll";
            //string v2 = @"./EnhancerCorona.dll";
            //string v1_2 = @"./EnhancerCorona.new.dll";
            //string the_patch = @"./hdiffz.patch";

            //GenerateP(v1, v2, the_patch);//生成patch
            //ApplyP(v1, the_patch, v1_2);//应用patch

            //foreach (var item in new[] { v1, v2, v1_2 })
            //{
            //    Console.WriteLine(item);
            //    Console.WriteLine($"SIZE:\t{new FileInfo(item).Length}");
            //    Console.WriteLine($"MD5:\t{PublicMethod.GetMD5(item)}");
            //}
            //Console.WriteLine("Patch/Original");
            //Console.WriteLine($"{(int)(new FileInfo(the_patch).Length) / 1024}KB/{(int)(new FileInfo(v2).Length / 1024)}KB");
            //Console.WriteLine($"{((decimal)(new FileInfo(the_patch).Length) / (decimal)(new FileInfo(v2).Length) * 100).ToString("F2")}%");
            Console.ReadLine();
            return 0;
        }
    }
}
