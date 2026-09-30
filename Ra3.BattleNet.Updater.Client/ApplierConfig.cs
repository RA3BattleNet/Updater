namespace Ra3.BattleNet.Updater.Client;

/// <summary>
/// **落地阶段（applier）的配置**（AGENT.md §12.5 阶段二）。
///
/// 它回答的问题是"把**已经暂存好的那一版**换上去"，与"去哪儿取远端、取哪一版"无关。
/// 【必须】因此它**在类型层面无法表达清单地址** —— applier 要落地的是阶段一已经规划并暂存好的
/// 那一版，不是"线上最新版"。落地发生在宿主退出之后、无人值守，那条路上没有网络：
///   - 不碰网络 ⇒ 没有"退出时网络抖动导致更新失败"这条路径（实测发生过一次：缓存冷时
///     metadata 解析耗时 43.5 s 且失败过一次）；
///   - 语义更准 ⇒ 落地的是"那一版"，远端此刻变了也不影响本次（下一次更新会话自然会做下去）。
///
/// 【必须】它与 <see cref="UpdateConfig"/> **彼此独立**：不由对方派生，也没有"字段属于关系"的断言。
/// 宿主显式构造它、交给 <c>StagedApplier.BuildApplyCommand</c> 生成命令行。
/// 真正会出事的只有一件事 —— **两边指向的根目录/缓存不是同一处**，所以 applier 在
/// <c>update.log</c> 第一行打印它实际使用的 <c>root</c> / <c>cacheDir</c> / <c>manifestFile</c> 供核对。
/// </summary>
public sealed record ApplierConfig
{
    /// <summary>安装根目录。</summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// 阶段一留在缓存里的**远端清单原文**的路径；默认 <c>{ResolveCacheDir()}/manifest.remote.xml</c>。
    /// applier 用它做两件事：校验暂存内容、以及落地成功后把它**原样**写成本地清单。
    /// 【必须】文件不在就**拒绝落地**（`reason=manifest_unavailable`、本地清单不被改写、暂存内容原样保留），
    /// **绝不联网重取** —— 那会把"落地已暂存的那一版"偷偷变成"落地线上最新版"。
    /// </summary>
    public string? ManifestFile { get; init; }

    /// <summary>本地清单路径。默认 {RootPath}/manifest.xml。</summary>
    public string? LocalManifestPath { get; init; }

    /// <summary>
    /// 缓存目录。默认与阶段一**逐字相同**：&lt;系统临时目录&gt;/updater-cache/&lt;安装根指纹&gt;（§12.3）。
    /// 必须同处：更新锁按缓存目录定位，阶段一留在那里的清单也是 applier 的唯一输入。
    /// </summary>
    public string? CacheDir { get; init; }

    /// <summary>日志路径。默认规则与阶段一一致（AGENT.md §4.8）。</summary>
    public string? LogPath { get; init; }

    /// <summary>
    /// 日志超过该字节数就在下一次运行时轮转成 <c>update.log.1</c>（只留一代）。
    /// 默认 8 MB；0 = 不轮转。见 AGENT.md §4.11。
    /// </summary>
    public long MaxLogBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// 要等的宿主进程号（null = 不等待特定进程）。由 CLI 的 --wait-for-pid 设置；
    /// 与"树内进程名扫描"一起构成静默判据（AGENT.md §12.5）。
    /// </summary>
    public int? WaitForProcessId { get; init; }

    /// <summary>
    /// 宿主进程名（不含 .exe）。与 <see cref="WaitForProcessId"/> 一起校验：
    /// **PID 会被系统复用**，光看 PID 会把"抢到同一个 PID 的无关进程"当成宿主还在，白等到超时。
    /// 由 <c>StagedApplier.BuildApplyCommand</c> 自动填 —— 宿主不用管。
    /// </summary>
    public string? WaitForProcessName { get; init; }

    /// <summary>
    /// 宿主进程的启动时刻（<c>Process.StartTime.Ticks</c>）。它才是**唯一**的实例标识：
    /// 同 PID + 同名字仍可能是"用户又启动了一次同名程序"（例如又双击了一次启动器），
    /// 启动时刻对不上就一定不是同一个进程。同样由 BuildApplyCommand 自动填。
    /// </summary>
    public long? WaitForProcessStartTicks { get; init; }

    /// <summary>
    /// 落地**成功之后**把宿主拉起来（§12.5）。默认关 —— 这是行为改变，必须由宿主显式开。
    /// 用 <c>StagedApplier.BuildApplyCommand</c> 时，只要把这个开关打开，
    /// 宿主自己的可执行文件与**原始参数原文**会被自动填好，宿主侧不需要别的代码。
    /// </summary>
    public bool RestartAfterApply { get; init; }

    /// <summary>要拉起的可执行文件；<c>BuildApplyCommand</c> 会自动填成当前进程（即宿主自己）。</summary>
    public string? RestartExecutable { get; init; }

    /// <summary>
    /// 原样传给宿主的参数 —— 是宿主**原始命令行里去掉 exe 那一段的原文**，所以引号不会被我们重新解释
    /// （自己拼引号是这类功能最常见的翻车点）。
    /// </summary>
    public string? RestartArguments { get; init; }

    /// <summary>宿主的工作目录；不给就沿用 applier 的当前目录。</summary>
    public string? RestartWorkingDirectory { get; init; }

    /// <summary>拉起前的等待，默认 1 秒（给系统收尾和文件句柄释放留一点余量）。</summary>
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>applier 等待"树静默"的总时限（§12.5）：超时即整轮不落地，什么都不动。</summary>
    public TimeSpan ApplierQuiescenceTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>applier 的静默轮询间隔。默认 2 秒（实测：按名字扫描约 4.5ms/次）。</summary>
    public TimeSpan ApplierPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public string ResolveLocalManifestPath() => LocalManifestPath ?? Path.Combine(RootPath, "manifest.xml");

    public string ResolveCacheDir() => InstallPaths.CacheDir(CacheDir, RootPath);

    public string ResolveLogPath() => InstallPaths.LogPath(LogPath, CacheDir, RootPath);

    /// <summary>阶段一留在缓存里的远端清单原文（默认路径与阶段一那个文件名**必须一致**）。</summary>
    public string ResolveManifestFile() => ManifestFile ?? Path.Combine(ResolveCacheDir(), "manifest.remote.xml");

    /// <summary>
    /// 从命令行参数构造配置 —— **库内唯一一份实现**。
    ///
    /// 为什么要放在库里：参数是库里**生成**的（<see cref="StagedApplier.BuildApplyCommand"/>），
    /// 而解析它的一直是各处**手写**的另一半（CLI 里一份、宿主自己 exe 里可能还有一份）。
    /// 只要有人给 applier 加一个参数、改了生成端忘了改解析端，落地就会在**宿主已经退出之后**
    /// 报一个"未知参数"而无人知晓。生成与解析同源就没有这类漂移。
    ///
    /// 【必须】两种策略（<see cref="UnknownArgPolicy"/>）：
    ///   - <see cref="UnknownArgPolicy.Ignore"/>：**给机器用的 applier** —— 现场那个 exe 可能还是旧的
    ///     （它很少更新、又必须能被更新），它必须能吃下**新生成器**加的参数；
    ///   - <see cref="UnknownArgPolicy.Strict"/>：**给人用的 CLI** —— 白名单之外一律拒，
    ///     防止"把更新会话的参数递给落地阶段"这种误用被静默吞掉。
    ///
    /// 【必须】必需参数（<c>--root</c>）在两种策略下都要报错 —— 宽松只管"多出来的参数"。
    /// </summary>
    public static ApplierConfig FromArgs(IEnumerable<string> args, UnknownArgPolicy policy)
    {
        var argv = args as string[] ?? args.ToArray();

        string? root = null, manifestFile = null, localManifest = null, cacheDir = null, log = null;
        int? waitForPid = null;
        string? waitForName = null;
        long? waitForStart = null;
        string? restartExe = null, restartArgs = null, restartCwd = null;
        var restartDelaySeconds = 1;
        var quiescenceSeconds = 600;
        var pollSeconds = 2;

        for (var i = 0; i < argv.Length; i++)
        {
            switch (argv[i])
            {
                case "--root": root = Next(argv, ref i); break;
                case "--manifest-file": manifestFile = Next(argv, ref i); break;
                case "--local-manifest": localManifest = Next(argv, ref i); break;
                case "--cache-dir": cacheDir = Next(argv, ref i); break;
                case "--log": log = Next(argv, ref i); break;
                case "--wait-for-pid": waitForPid = int.Parse(Next(argv, ref i)); break;
                case "--wait-for-name": waitForName = Next(argv, ref i); break;
                case "--wait-for-start": waitForStart = long.Parse(Next(argv, ref i)); break;
                case "--restart": restartExe = Next(argv, ref i); break;
                case "--restart-args": restartArgs = Next(argv, ref i); break;
                case "--restart-cwd": restartCwd = Next(argv, ref i); break;
                case "--restart-delay": restartDelaySeconds = int.Parse(Next(argv, ref i)); break;
                case "--quiescence-timeout": quiescenceSeconds = int.Parse(Next(argv, ref i)); break;
                case "--poll-seconds": pollSeconds = int.Parse(Next(argv, ref i)); break;
                // 生成器（BuildApplyCommand）会带上它：它是"这次是落地调用"的标记。
                // 对专用 applier exe 来说冗余（它本来就只干这个），但它属于这条命令的契约，必须认。
                case "--apply": break;
                default:
                    if (policy == UnknownArgPolicy.Strict)
                        throw new ArgumentException(
                            $"未知参数：{argv[i]}（落地阶段不认它；更新会话（阶段一）的参数请用 Client.CLI 的非 --apply 模式）");
                    break;   // Ignore：向前兼容 —— 旧 applier 必须能忽略新生成器加的参数
            }
        }

        if (string.IsNullOrEmpty(root)) throw new ArgumentException("缺少 --root");

        return new ApplierConfig
        {
            RootPath = Path.GetFullPath(root),
            ManifestFile = manifestFile is null ? null : Path.GetFullPath(manifestFile),
            LocalManifestPath = localManifest is null ? null : Path.GetFullPath(localManifest),
            CacheDir = cacheDir is null ? null : Path.GetFullPath(cacheDir),
            LogPath = log is null ? null : Path.GetFullPath(log),
            WaitForProcessId = waitForPid,
            WaitForProcessName = waitForName,
            WaitForProcessStartTicks = waitForStart,
            // 命令行里出现 --restart 就是"要重启"，不必再要一个开关
            RestartAfterApply = restartExe is not null,
            RestartExecutable = restartExe,
            RestartArguments = restartArgs,
            RestartWorkingDirectory = restartCwd,
            RestartDelay = TimeSpan.FromSeconds(Math.Max(0, restartDelaySeconds)),
            ApplierQuiescenceTimeout = TimeSpan.FromSeconds(Math.Max(1, quiescenceSeconds)),
            ApplierPollInterval = TimeSpan.FromSeconds(Math.Max(1, pollSeconds)),
        };
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} 缺少取值");
        return args[++i];
    }
}

/// <summary>
/// 解析 applier 参数时遇到"不认识的参数"怎么办（AGENT.md §12.5）。
/// 两个入口两种策略，但**只有一份解析实现**（<see cref="ApplierConfig.FromArgs"/>）。
/// </summary>
public enum UnknownArgPolicy
{
    /// <summary>报错（**给人用的 CLI**）：白名单之外一律拒，防误用、防把两个模式的参数混着递。</summary>
    Strict,

    /// <summary>
    /// 忽略（**给机器用的 applier**）：不认识就不处理、不报错。
    /// 这不是偷懒：现场那个 applier exe 可能是旧的（它很少被更新），而参数是**新版库**生成的，
    /// 它必须能吃下自己不认识的新参数 —— 否则旧 applier 会让"更新"在无人值守时静默失败。
    /// </summary>
    Ignore,
}