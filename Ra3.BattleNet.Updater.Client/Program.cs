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
        private static bool IsDebug = true;
        private enum _loggerT { None = -2, Note = -1, Info = 0, Success = 1, Fail = 2, DEBUG = 3, Warning = 4, Alert = 5, ANS = 99 }
        /// <summary>
        /// /// 日志输出器
        /// </summary>
        /// <param name="type">记录种类</param>
        /// <param name="inf">记录信息</param>
        private static void Logger(_loggerT type, string inf)
        {
            switch (type)
            {
                case _loggerT.Info:
                    if (IsDebug)
                    {
                        ConsoleColor InfoBackColor = Console.BackgroundColor, InfoForeColor = ConsoleColor.DarkGreen;
                        Console.BackgroundColor = InfoBackColor;
                        Console.ForegroundColor = InfoForeColor;
                        Console.Write($"[INFO] {inf}");
                        Console.ResetColor();
                    }
                    break;
                case _loggerT.Note:
                    if (IsDebug)
                    {
                        ConsoleColor NoteBackColor = Console.BackgroundColor, NoteForeColor = ConsoleColor.Cyan;
                        Console.BackgroundColor = NoteBackColor;
                        Console.ForegroundColor = NoteForeColor;
                        Console.Write($"[NOTE] {inf}");
                        Console.ResetColor();
                    }
                    break;
                case _loggerT.Success:
                    ConsoleColor SuccessBackColor = Console.BackgroundColor, SuccessForeColor = ConsoleColor.Magenta;
                    Console.BackgroundColor = SuccessBackColor;
                    Console.ForegroundColor = SuccessForeColor;
                    Console.Write($"[+] {inf}");
                    Console.ResetColor();
                    break;
                case _loggerT.Fail:
                    ConsoleColor FailBackColor = Console.BackgroundColor, FailForeColor = ConsoleColor.Red;
                    Console.BackgroundColor = FailBackColor;
                    Console.ForegroundColor = FailForeColor;
                    Console.Write($"[-] {inf}");
                    Console.ResetColor();
                    break;
                case _loggerT.DEBUG:
                    if (IsDebug)
                    {
                        ConsoleColor DEBUGBackColor = Console.BackgroundColor, DEBUGForeColor = ConsoleColor.DarkGray;
                        Console.BackgroundColor = DEBUGBackColor;
                        Console.ForegroundColor = DEBUGForeColor;
                        Console.Write($"[DEBUG]: {inf}");
                        Console.ResetColor();
                    }
                    break;
                case _loggerT.Warning:
                    if (IsDebug)
                    {
                        ConsoleColor WarningBackColor = Console.BackgroundColor, WarningForeColor = ConsoleColor.DarkBlue;
                        Console.BackgroundColor = WarningBackColor;
                        Console.ForegroundColor = WarningForeColor;
                        Console.Write($"[WARNING] {inf}");
                        Console.ResetColor();
                    }
                    break;
                case _loggerT.Alert:
                    if (IsDebug)
                    {
                        ConsoleColor AlertBackColor = Console.BackgroundColor, AlertForeColor = ConsoleColor.Yellow;
                        Console.BackgroundColor = AlertBackColor;
                        Console.ForegroundColor = AlertForeColor;
                        Console.Write($"[AlERT] {inf}");
                        Console.ResetColor();
                    }
                    break;
                case _loggerT.ANS:
                    if (IsDebug)
                    {
                        ConsoleColor ANSBackColor = ConsoleColor.White, ANSForeColor = ConsoleColor.Black;
                        Console.BackgroundColor = ANSBackColor;
                        Console.ForegroundColor = ANSForeColor;
                        Console.Write($"[ANSWER] {inf}");
                        Console.ResetColor();
                    }
                    break;
                default:
                    Console.BackgroundColor = Console.BackgroundColor;
                    Console.ForegroundColor = Console.ForegroundColor;
                    Console.Write($"{inf}");
                    Console.ResetColor();
                    break;
            }
        }

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
            Logger(_loggerT.Success, "XML 解析成功\n");
            #endregion
            Logger(_loggerT.DEBUG, $"{inputStdinXml.SelectSingleNode("/Metadata/Manifest/File")?["FileName"]?.InnerText}{Environment.NewLine}");
            Logger(_loggerT.DEBUG, $"{inputStdinXml.SelectSingleNode("/Metadata/Manifest/File")?["RelativePath"]?.InnerText}{Environment.NewLine}");

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
