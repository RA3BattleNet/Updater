﻿using System.Diagnostics;
using System.Runtime.InteropServices;
using static Updater.PublicMethod;

namespace Updater
{
    public class PatchGenerater
    {
        public static void GenerateP(string oldFile, string newFile, string deltaFile)
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
        }
    }
}
