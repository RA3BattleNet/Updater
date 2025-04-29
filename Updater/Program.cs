using static Updater.PatchGenerater;
using static Updater.PatchApplyer;
using System.Security.Cryptography;
using Updater;

namespace Ra3.BattleNet.Updater
{
    internal class Program
    {

        static void Main(string[] args)
        {
            string v1 = @"./EnhancerCorona.old.dll";
            string v2 = @"./EnhancerCorona.dll";
            string v1_2 = @"./EnhancerCorona.new.dll";
            string the_patch = @"./hdiffz.patch";

            GenerateP(v1, v2, the_patch);//生成patch
            ApplyP(v1, the_patch, v1_2);//应用patch

            foreach (var item in new[] { v1, v2, v1_2 })
            {
                Console.WriteLine(item);
                Console.WriteLine($"SIZE:\t{new FileInfo(item).Length}");
                Console.WriteLine($"MD5:\t{PublicMethod.GetMD5(item)}");
            }
            Console.WriteLine("Patch/Original");
            Console.WriteLine($"{(int)(new FileInfo(the_patch).Length) / 1024}KB/{(int)(new FileInfo(v2).Length / 1024)}KB");
            Console.WriteLine($"{((decimal)(new FileInfo(the_patch).Length) / (decimal)(new FileInfo(v2).Length) * 100).ToString("F2")}%");
            Console.ReadLine();
        }
    } 
}
