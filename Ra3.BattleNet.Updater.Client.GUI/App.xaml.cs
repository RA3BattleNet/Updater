using Ra3.BattleNet.Updater.Client.PatchIndexApplyer;
using Ra3.BattleNet.Updater.Share.Log;
using System.Windows;

namespace Ra3.BattleNet.Updater.Client.GUI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;
        Logger.IsDebug = args.Contains("--debug");

        string? localRootPath = null, localManifestPath = null,
                newestManifestPath = null, newestPatchesPath = null,
                newestDownloadPath = null, relaunch = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--local-rootPath":      localRootPath = args[++i]; break;
                case "--local-manifestPath":   localManifestPath = args[++i]; break;
                case "--newest-manifestPath":  newestManifestPath = args[++i]; break;
                case "--newest-patchesPath":   newestPatchesPath = args[++i]; break;
                case "--newest-downloadPath":  newestDownloadPath = args[++i]; break;
                case "--relaunch":             relaunch = args[++i]; break;
            }
        }

        if (string.IsNullOrEmpty(localRootPath) ||
            string.IsNullOrEmpty(localManifestPath) ||
            string.IsNullOrEmpty(newestManifestPath) ||
            string.IsNullOrEmpty(newestPatchesPath) ||
            string.IsNullOrEmpty(newestDownloadPath))
        {
            MessageBox.Show("缺少必要参数。\n--local-rootPath\n--local-manifestPath\n--newest-manifestPath\n--newest-patchesPath\n--newest-downloadPath",
                "参数错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        var options = new PatchIndexApplyerEngine.Options(
            localRootPath, localManifestPath,
            newestManifestPath, newestPatchesPath, newestDownloadPath);

        MainWindow = new MainWindow(options, relaunch);
        MainWindow.Show();
    }
}
