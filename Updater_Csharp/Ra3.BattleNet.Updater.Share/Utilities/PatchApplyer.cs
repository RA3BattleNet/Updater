using Ra3.BattleNet.Updater.Share.Log;
using System.Diagnostics;
using static Ra3.BattleNet.Updater.Share.Utilities.PublicMethod;

namespace Ra3.BattleNet.Updater.Share.Utilities
{
    public class PatchApplyer
    {
        /// <summary>
        /// 用于应用补丁，生成新文件
        /// </summary>
        /// <param name="oldFile">旧文件路径</param>
        /// <param name="diffFile">补丁文件路径</param>
        /// <param name="outNewPath">新文件路径</param>
        /// <returns>是否成功执行</returns>
        public static bool ApplyPatch(string oldFile, string diffFile, string outNewPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = GetHpatchPath(),
                Arguments = $"-s -f \"{oldFile}\" \"{diffFile}\" \"{outNewPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process? process = Process.Start(psi))
            {
                string output = process.StandardOutput.ReadToEnd();
                Console.WriteLine(output);
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    string error = process.StandardError.ReadToEnd();
                    Logger.Fail($"应用补丁出现错误：{Environment.NewLine}{error}");
                    return false;
                }
            }
            return true;
        }
    }
}
