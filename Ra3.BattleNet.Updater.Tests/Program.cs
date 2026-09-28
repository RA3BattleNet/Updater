using System.Reflection;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// 反射跑器：发现并执行本程序集里的 xUnit [Fact] 方法。
/// 存在的唯一理由是环境限制（VSTest 的 testhost 在受限沙箱内无法打开父进程句柄）。
/// 断言仍来自 xunit 包，测试写法不变；正常机器上 dotnet test 依旧可用。
/// 用法：dotnet run -- [过滤子串]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var filter = args.Length > 0 ? args[0] : null;
        var assembly = typeof(Program).Assembly;

        var cases = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t != typeof(Program))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttributes().Any(a => a.GetType().Name == "FactAttribute"))
                .Select(m => (Type: t, Method: m)))
            .Where(c => filter is null
                || $"{c.Type.Name}.{c.Method.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Type.Name).ThenBy(c => c.Method.Name)
            .ToList();

        if (cases.Count == 0)
        {
            Console.WriteLine($"没有匹配的测试（过滤：{filter ?? "(无)"}）");
            return 1;
        }

        var passed = 0;
        var failed = 0;

        foreach (var (type, method) in cases)
        {
            var name = $"{type.Name}.{method.Name}";
            try
            {
                var instance = Activator.CreateInstance(type);
                var result = method.Invoke(instance, null);
                if (result is Task task) task.GetAwaiter().GetResult();

                Console.WriteLine($"PASS  {name}");
                passed++;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException! : ex;
                Console.WriteLine($"FAIL  {name}");
                Console.WriteLine("      " + inner.Message.Replace("\n", "\n      ", StringComparison.Ordinal));
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
