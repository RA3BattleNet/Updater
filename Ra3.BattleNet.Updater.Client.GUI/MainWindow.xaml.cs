using Ra3.BattleNet.Updater.Client.PatchIndexApplyer;
using Ra3.BattleNet.Updater.Share.Log;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Ra3.BattleNet.Updater.Client.GUI;

public partial class MainWindow : Window
{
    private readonly PatchIndexApplyerEngine.Options _options;
    private readonly string? _relaunch;

    public MainWindow(PatchIndexApplyerEngine.Options options, string? relaunch)
    {
        InitializeComponent();
        _options = options;
        _relaunch = relaunch;
        Loaded += OnLoaded;
    }

    private void Log(string msg)
    {
        Dispatcher.Invoke(() =>
        {
            TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            TxtLog.ScrollToEnd();
        });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var engine = new PatchIndexApplyerEngine(_options,
            new Progress<PatchIndexApplyerEngine.ProgressReport>(OnProgress));

        bool ok;
        try
        {
            ok = await Task.Run(() => engine.RunAsync());
        }
        catch (Exception ex)
        {
            Log($"错误: {ex.Message}");
            ok = false;
        }

        Dispatcher.Invoke(() =>
        {
            TxtStatus.Text = ok ? "更新完成" : "更新失败";
            ProgressBarUpdate.Value = ok ? 100 : 0;
        });
        Log($"结果: {(ok ? "成功" : "失败")}");

        if (!string.IsNullOrEmpty(_relaunch) && File.Exists(_relaunch))
        {
            Log($"拉起: {_relaunch}");
            Process.Start(new ProcessStartInfo
            {
                FileName = _relaunch,
                WorkingDirectory = _options.LocalRootPath,
                UseShellExecute = true
            });
        }

        await Task.Delay(ok ? 1500 : 3000);
        Dispatcher.Invoke(() => Close());
    }

    private void OnProgress(PatchIndexApplyerEngine.ProgressReport r)
    {
        Dispatcher.Invoke(() =>
        {
            var pct = r.Total > 0 ? (double)r.Current / r.Total * 100 : 0;
            ProgressBarUpdate.Value = pct;
            TxtStatus.Text = $"[{r.Current}/{r.Total}]";
            TxtFile.Text = string.IsNullOrEmpty(r.FileName) ? r.Status : $"{r.FileName}: {r.Status}";
            if (!string.IsNullOrEmpty(r.FileName))
                Log($"{r.FileName}: {r.Status}");
        });
    }
}
