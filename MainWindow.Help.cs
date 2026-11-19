using System.Diagnostics;
using System.IO;
using System.Windows;
using Koromogae.Ui;

namespace Koromogae
{
    /// <summary>メニューバーの「ヘルプ」: マニュアルを開く・バージョン情報の画面・ログ・設定フォルダを開く。</summary>
    public partial class MainWindow
    {
        /// <summary>配布物の HTML のマニュアルを、既定のブラウザで開く。画面の言語のものを優先し、無ければもう一方 (<see cref="ManualLocator"/>)。どちらも無ければ知らせる。</summary>
        void OpenManualMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string? path = ManualLocator.Resolve(AppContext.BaseDirectory, Loc.Culture, File.Exists);
            if (path == null)
            {
                AppLog.Warn($"マニュアルが見つかりません: {AppContext.BaseDirectory}");
                MessageBox.Show(this, Loc.F("Err.ManualNotFound", AppContext.BaseDirectory), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Error($"マニュアルを開けません: {path}", ex);
                MessageBox.Show(this, Loc.F("Err.OpenManual", ex.Message, path), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            new AboutWindow(AppInfo.Collect()) { Owner = this }.ShowDialog();
        }

        void OpenDataFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string folder = AppPaths.DataDirectory;
            try
            {
                Directory.CreateDirectory(folder);   // まだ何も保存していなくても、開けるように
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Error($"フォルダを開けません: {folder}", ex);
                MessageBox.Show(this, Loc.F("Err.OpenFolder", ex.Message, folder), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
