using System.Text;

namespace Ra3.BattleNet.Updater.Client;

/// <summary>每条日志的状态码（封闭集合，只增不改；AGENT.md §4.11）。</summary>
internal static class LogStatus
{
    public const int Ok = 0;
    public const int NotFound = 1;
    public const int RetryExceeded = 2;
    public const int VerifyFailed = 3;
    public const int IoError = 4;
    public const int NeedsFullPackage = 5;
    public const int Other = 9;
}

/// <summary>
/// 更新日志。格式**稳定优先于齐全**：字段顺序与分隔符一经确定不得更改，只能往行尾追加。
/// 分隔符必须是 TAB —— 文件名里普遍含 '-'。
/// </summary>
internal sealed class UpdateLog : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly Lock _gate = new();

    public string RunId { get; }

    public UpdateLog(string? path, string runId, long maxBytes = 0)
    {
        RunId = runId;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Fs.CreateDirectory(dir);

            // §4.11：按大小轮转。只留一代历史（update.log.1），
            // 目的是不让客户端安装目录里的日志无限长大 —— 分析脚本读当前那份即可。
            if (maxBytes > 0 && Fs.Exists(path) && Fs.Length(path) >= maxBytes)
                Fs.Move(path, path + ".1", overwrite: true);

            _writer = new StreamWriter(new FileStream(Fs.P(path), FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
            {
                AutoFlush = true,
                // §4.11：换行必须是 LF。StreamWriter 默认跟 Environment.NewLine 走，
                // 在 Windows 上就成了 CRLF —— 分析脚本每行会多出一个不可见的 \r。
                NewLine = "\n",
            };
        }
        catch
        {
            _writer = null; // 日志写不了不影响更新本身
        }
    }

    /// <summary>
    /// 每轮运行**开始**一行。存在的理由：一次进程里可能跑两轮（取消 + 重跑），
    /// 它们的 F/R 行混在同一个文件里；分析脚本必须按 run_id 分组才不会把账算错。
    /// 只往行尾/新增行型追加，不改动既有列（§4.11）。
    /// </summary>
    public void RunStart(string utcIso) => Write($"S\t{RunId}\t{utcIso}");

    /// <summary>每文件一行。</summary>
    public void File(
        string uuid, string? oldMd5, string? oldPath, string? newMd5, string? newPath,
        string action, int status, string reason, long bytes, long ms, long payload = 0)
    {
        // payload 是**追加在行尾**的新列（§4.11 只允许往行尾追加）：
        // bytes   = 内容字节（解压后写盘的量）；
        // payload = 本次为这个文件从响应正文里读到的字节（续传/重试各段都算）。
        //           它**不是**真实网线字节 —— 口径见 HttpFetcher.PayloadBytes 与 OPEN_ISSUES I-2/M-1。
        // 开了传输压缩之后两者会差 2 倍以上，比"省了多少带宽"必须看后者。
        Write($"F\t{RunId}\t{uuid}\t{oldMd5}\t{oldPath}\t{newMd5}\t{newPath}\t{action}\t{status}\t{reason}\t{bytes}\t{ms}\t{payload}");
    }

    /// <summary>每轮一行（收尾）。result 是增量命中率与节省量的唯一现场证据。</summary>
    public void Run(
        string manifestHash, int total, int skip, int move, int patch, int full, int fail,
        long bytes, long ms, string result, long requests = 0, long payload = 0, long wire = 0)
    {
        // requests / payload / wire 都是**追加**在行尾的新列（AGENT.md §4.11 只允许往行尾追加）：
        //   payload = 从响应正文里读到的字节（口径见 HttpFetcher.PayloadBytes，**不是**网线字节）；
        //   wire    = 本进程真正发出 + 收到的字节（连接层计数，含 TLS/HTTP 头/压缩后的正文）——
        //             做带宽验收（§2.1 F8）看这一列，别用 payload。
        Write($"R\t{RunId}\t{manifestHash}\t{total}\t{skip}\t{move}\t{patch}\t{full}\t{fail}\t{bytes}\t{ms}\t{result}\t{requests}\t{payload}\t{wire}");
    }

    private void Write(string line)
    {
        if (_writer is null) return;
        try
        {
            lock (_gate) _writer.WriteLine(line);
        }
        catch
        {
            // 同上：日志失败不影响更新
        }
    }

    public void Dispose() => _writer?.Dispose();
}
