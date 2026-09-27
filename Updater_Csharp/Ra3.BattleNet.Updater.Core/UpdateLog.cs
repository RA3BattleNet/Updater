using System.Text;

namespace Ra3.BattleNet.Updater.Core;

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

    public UpdateLog(string? path, string runId)
    {
        RunId = runId;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Fs.CreateDirectory(dir);
            _writer = new StreamWriter(new FileStream(Fs.P(path), FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }
        catch
        {
            _writer = null; // 日志写不了不影响更新本身
        }
    }

    /// <summary>每文件一行。</summary>
    public void File(
        string uuid, string? oldMd5, string? oldPath, string? newMd5, string? newPath,
        string action, int status, string reason, long bytes, long ms)
    {
        Write($"F\t{RunId}\t{uuid}\t{oldMd5}\t{oldPath}\t{newMd5}\t{newPath}\t{action}\t{status}\t{reason}\t{bytes}\t{ms}");
    }

    /// <summary>每轮一行（收尾）。result 是增量命中率与节省量的唯一现场证据。</summary>
    public void Run(
        string manifestHash, int total, int skip, int move, int patch, int full, int fail,
        long bytes, long ms, string result)
    {
        Write($"R\t{RunId}\t{manifestHash}\t{total}\t{skip}\t{move}\t{patch}\t{full}\t{fail}\t{bytes}\t{ms}\t{result}");
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
