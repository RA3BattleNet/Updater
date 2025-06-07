using System.Diagnostics;
using static Ra3.BattleNet.Updater.Share.Utilities.PublicMethod;

namespace Ra3.BattleNet.Updater.Share
{
    public class PatchGenerater
    {
        public static bool GeneratePatch(string oldFile, string newFile, string deltaFile)
        {
            var psi = new ProcessStartInfo
            {
                FileName = GetHdiffzPath(),
                Arguments = $"-s -f \"{oldFile}\" \"{newFile}\" \"{deltaFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = Process.Start(psi))
            {
                string output = process.StandardOutput.ReadToEnd();
                Console.WriteLine(output);
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    string error = process.StandardError.ReadToEnd();
                    Console.WriteLine($"出现错误：{Environment.NewLine}{error}");
                }
            }
            return true;
        }
    }
}
