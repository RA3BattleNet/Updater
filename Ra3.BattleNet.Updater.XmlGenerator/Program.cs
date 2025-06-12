
using Ra3.BattleNet.Updater.Share.Log;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
namespace Ra3.BattleNet.Updater.XmlGenerator
{
    internal static class CommandLineOptions
    {
        public static string? OldXmlPath { get; set; }
        public static string? TargetDirOrFile { get; set; }
        public static string? NewXmlOutPutPath { get; set; }
        public static void Parse(string[] args)
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
                        case "--target":
                            TargetDirOrFile = args[++i];
                            break;
                        case "--new-xmloutputpath":
                            NewXmlOutPutPath = args[++i];
                            break;
                    }
                }

                // 验证必要参数
                if (string.IsNullOrEmpty(OldXmlPath)
                    || string.IsNullOrEmpty(TargetDirOrFile)
                    || string.IsNullOrEmpty(NewXmlOutPutPath))
                {
                    Logger.Fail("缺少必要参数\n");
                    ShowUsage();
                    Environment.Exit(-1);
                }
            }
            catch (Exception ex)
            {
                Logger.Fail($"参数无法解析{Environment.NewLine}Msg:{ex.Message}");
                ShowUsage();
                Environment.Exit(-2);
            }

        }
        public static void ShowUsage()
        {

        }

    }
    internal class Program
    {
        static void Main(string[] args)
        {
        }
    }
}
