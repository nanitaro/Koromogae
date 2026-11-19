using System.Windows;
using System.Windows.Controls;

namespace Koromogae
{
    /// <summary>メニューバーの「オプション」: 最小化したときの動作 (タスクトレイへ格納するか、Windows 標準の最小化か) を選ぶ。</summary>
    public partial class MainWindow
    {
        void MinimizeToTrayMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item) return;
            _minimizeToTray = item.IsChecked;
            CaptureSettings().Save();   // すぐ保存する (終了を待たない)。失敗は AppLog に記録される
            AppLog.Info($"最小化時にトレイへ格納する設定を変更しました: {_minimizeToTray}");
        }
    }
}
