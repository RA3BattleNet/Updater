using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Ra3.BattleNet.Updater.Share
{
    public static class PublicMethod
    {
        private static string? hdiffz_path = null;
        private static string? hpatch_path = null;

        /// <summary>
        /// 获取hdiffz路径
        /// </summary>
        /// <returns></returns>
        public static string GetHdiffzPath()
        {
            if (hdiffz_path == null)
            {
                hdiffz_path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "hdiffpatch_bin",
                    RuntimeInformation.RuntimeIdentifier, // RID : https://github.com/dotnet/runtime/blob/main/src/libraries/Microsoft.NETCore.Platforms/src/PortableRuntimeIdentifierGraph.json
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"hdiffz.exe" : "hdiffz");
            }
            return hdiffz_path;
        }

        /// <summary>
        /// 获取hpatch路径
        /// </summary>
        /// <returns></returns>
        public static string GetHpatchPath()
        {
            if (hpatch_path == null)
            {
                hpatch_path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "hdiffpatch_bin",
                    RuntimeInformation.RuntimeIdentifier, // RID : https://github.com/dotnet/runtime/blob/main/src/libraries/Microsoft.NETCore.Platforms/src/PortableRuntimeIdentifierGraph.json
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"hpatchz.exe" : "hpatchz");
            }
            return hpatch_path;
        }

        public static string GetMD5(string path)
        {
            using (var md5 = MD5.Create())
            using (var stream = File.OpenRead(path))
            {
                byte[] hashBytes = md5.ComputeHash(stream);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
            }
        }
    }
}
