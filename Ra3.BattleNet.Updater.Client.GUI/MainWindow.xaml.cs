using Ra3.BattleNet.Updater.Share.Log;
using Ra3.BattleNet.Updater.Share.Models;
using static Ra3.BattleNet.Updater.Client.API;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;


namespace Ra3.BattleNet.Updater.Client.GUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
#if DEBUG
            Logger.IsDebug = true;
            CheckBox_Debug.IsChecked = true;
#endif
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            string PatchPath = TextBox_PatchPath.Text;
            string ClientPath = TextBox_ClientPath.Text;
            string? LogPath = TextBox_LogFilePath.Text;

            #region patchpath
            if (!Directory.Exists(PatchPath))
            {
                MessageBox.Show($"补丁包路径有误，请检查补丁包路径：{Environment.NewLine}{PatchPath}");
                return;
            }
            #endregion

            #region clientpath
            if (!Directory.Exists(ClientPath))
            {
                MessageBox.Show($"待更新路径有误，请检查更新路径：{Environment.NewLine}{ClientPath}");
                return;
            }
            #endregion

            #region logpath
            bool IsLogNeed = true;
            if (string.IsNullOrEmpty(LogPath))
            {
                LogPath = null;
                IsLogNeed = false;
            }
            else if (!string.IsNullOrEmpty(LogPath))
            {
                try
                {
                    string? logDirectory = Path.GetDirectoryName(LogPath);
                    if (!string.IsNullOrEmpty(logDirectory) && !Directory.Exists(logDirectory))
                        Directory.CreateDirectory(logDirectory);

                    if (!Path.IsPathRooted(LogPath))
                    {
                        MessageBox.Show($"日志路径需要绝对路径：{Environment.NewLine}{LogPath}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"日志路径无效或无法访问：{Environment.NewLine}{LogPath}{Environment.NewLine}错误：{ex.Message}");
                    return;
                }
            }
            if (IsLogNeed)
            {
                Logger.Path = LogPath;
                Logger.IsDebug = CheckBox_Debug.IsChecked ?? false;
                Logger.Info($"增量更新客户端启动{Environment.NewLine}");
                Logger.RefreshFlag();
                if (!Logger.WriteFileflag)
                {
                    MessageBox.Show($"日志文件写入失败，请检查路径是否正确或权限是否足够");
                    return;
                }
            }
            #endregion

            Logger.Info($"PatchPath:{PatchPath}");
            Logger.Info($"ClientPath:{ClientPath}");
            if (IsLogNeed)
                Logger.Info($"LogPath:{LogPath}");
            else
                Logger.Note($"No Log Flag Set");

            PatchManifest patchManifest = LoadPatchManifest(PatchPath);
            if (patchManifest == null)
            {
                MessageBox.Show($"无法读取补丁操作文件，无法更新");
                Logger.Alert($"无法读取补丁操作文件{PatchPath}，无法更新");
                return;
            }

            bool success = ApplyPatchOperations(patchManifest, ClientPath, PatchPath);
            if (success)
                MessageBox.Show($"更新完成");
            else
            {
                if (IsLogNeed)
                    MessageBox.Show($"更新失败，可查看日志获取更多详细信息");
                else
                    MessageBox.Show($"更新失败");
            }


        }
    }
}