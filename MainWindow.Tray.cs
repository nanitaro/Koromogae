using System.Windows;
using Koromogae.Ui;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Koromogae
{
    /// <summary>
    /// 最小化したら、タスクトレイ (通知領域) へ格納する。同時に、エディタのウィンドウはすべて閉じる (何を開いていたかは覚えない)。
    /// トレイのアイコンの右クリックで、元に戻す・再生の開始/停止・録音の開始/停止・終了ができる。
    /// 設定 (オーディオ・プラグイン・録音) は画面の部品がそのまま持っているので、トレイからの操作も、最小化する前の設定で動く。
    /// </summary>
    public partial class MainWindow
    {
        WinForms.NotifyIcon? _tray;
        WinForms.ToolStripMenuItem? _trayRestore, _trayPlay, _trayRecord, _trayExit;

        /// <summary>NotifyIcon のツールチップの上限 (63 文字)。</summary>
        const int TrayTipMaxLength = 63;

        /// <summary>バルーン通知の本文の上限 (255 文字)。</summary>
        const int BalloonMaxLength = 250;

        WindowState _stateBeforeMinimize = WindowState.Normal;   // 最小化する前の状態 (通常 / 最大化)。トレイから戻すときに使う
        bool _forceTrayOnce;                                     // 起動時の --minimized: 設定にかかわらず、最初の最小化だけトレイへ

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState != WindowState.Minimized)
            {
                _stateBeforeMinimize = WindowState;
                return;
            }

            bool toTray = _minimizeToTray || _forceTrayOnce;
            _forceTrayOnce = false;
            if (toTray) MinimizeToTray();    // 設定が off なら、Windows 標準の最小化 (タスクバーに残る。エディタも開いたまま)
        }

        /// <summary>起動時の <c>--minimized</c>: 「最小化時にトレイへ格納する」の設定にかかわらず、トレイへ格納する。</summary>
        void MinimizeToTrayAtStartup()
        {
            _forceTrayOnce = true;
            WindowState = WindowState.Minimized;    // OnStateChanged が、トレイへ格納して _forceTrayOnce を戻す
        }

        /// <summary>
        /// 別のプロセス (二重起動しようとした 2 つ目) からの呼び出し。トレイや最小化から戻し、前面に出す。
        /// </summary>
        public void ShowFromOutside()
        {
            if (!IsVisible || WindowState == WindowState.Minimized) RestoreFromTray();
            else BringToFront();
        }

        /// <summary>エディタをすべて閉じて、ウィンドウを隠し、トレイにアイコンを出す。</summary>
        void MinimizeToTray()
        {
            CloseAllEditors();
            EnsureTray();
            UpdateTrayText();
            _tray!.Visible = true;
            Hide();    // タスクバーのボタンも消える
        }

        /// <summary>
        /// トレイから元に戻す (メインウィンドウだけ。エディタは開き直さない)。最小化する前の状態 (通常 / 最大化) に戻る。
        /// <b>先に Show して、そのあと元の状態にする</b>。
        /// <c>Hide()</c> 中のウィンドウは、<see cref="Window.WindowState"/> を変えても反映されず (実測)、先に Normal にしてから Show すると、
        /// 最小化のまま (画面の外の位置で) 現れて、元に戻らない。Show で現れた最小化のウィンドウを、続けて戻す。
        /// それでも最小化のままなら (WPF の状態と実際のウィンドウがずれたとき)、Win32 の SW_RESTORE で確実に戻す。
        /// </summary>
        void RestoreFromTray()
        {
            if (_tray != null) _tray.Visible = false;
            Show();
            WindowState = _stateBeforeMinimize;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
            if (WindowState == WindowState.Minimized) WindowState = _stateBeforeMinimize;   // WPF の側の値もそろえる
            BringToFront();
        }

        /// <summary>
        /// 前面に出す。他のアプリが前面にいても前に出るよう、一瞬だけ Topmost にしてすぐ戻す
        /// (<see cref="Window.Activate"/> だけでは、前面ロックでタスクバーが点滅するだけのことがある)。固定はしない。
        /// </summary>
        void BringToFront()
        {
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        const int SwRestore = 9;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        static extern bool IsIconic(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        /// <summary>
        /// プラグインのエディタ (プラグインホストが持つウィンドウ) と、汎用のパラメータ画面を、すべて閉じる。
        /// <see cref="IVstEffect.CloseEditor"/> は、ホストが応答するまで戻らないことがある (応答しないホストなら、呼び出しの期限まで)。
        /// そのため UI スレッドでは呼ばず、バックグラウンドに出す (応答しないプラグインがあっても、最小化の画面が固まらない)。
        /// </summary>
        void CloseAllEditors()
        {
            var effects = _engine.Chain.Slots.Select(s => s.Effect).ToList();
            foreach (var window in _parameterWindows.Values.ToList()) window.Close();
            if (effects.Count == 0) return;

            _ = Task.Run(() =>
            {
                foreach (var effect in effects)
                {
                    try
                    {
                        if (!effect.IsFaulted) effect.CloseEditor();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"エディタを閉じられません: {effect.Name} ({ex.Message})");
                    }
                }
            });
        }

        void EnsureTray()
        {
            if (_tray != null) return;

            _trayRestore = new WinForms.ToolStripMenuItem { Font = new Drawing.Font(WinForms.SystemInformation.MenuFont, Drawing.FontStyle.Bold) };   // 既定の項目 (ダブルクリックと同じ)
            _trayPlay = new WinForms.ToolStripMenuItem();
            _trayRecord = new WinForms.ToolStripMenuItem();
            _trayExit = new WinForms.ToolStripMenuItem();
            _trayRestore.Click += (_, _) => RestoreFromTray();
            _trayPlay.Click += (_, _) => { ToggleRunning(); UpdateTrayText(); };
            _trayRecord.Click += (_, _) => { ToggleRecording(); UpdateTrayText(); };
            _trayExit.Click += (_, _) => Close();    // ふつうにウィンドウを閉じるのと同じ (録音を終え、設定を保存し、エンジンを破棄する)

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.AddRange(new WinForms.ToolStripItem[] { _trayRestore, new WinForms.ToolStripSeparator(), _trayPlay, _trayRecord, new WinForms.ToolStripSeparator(), _trayExit });
            menu.Opening += (_, _) => UpdateTrayMenu();

            _tray = new WinForms.NotifyIcon
            {
                Icon = LoadTrayIcon(),
                ContextMenuStrip = menu,
                Visible = false,
            };
            _tray.MouseClick += (_, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left) RestoreFromTray();
            };
        }

        static Drawing.Icon LoadTrayIcon()
        {
            try
            {
                string? path = Environment.ProcessPath;
                if (path != null && Drawing.Icon.ExtractAssociatedIcon(path) is { } icon) return icon;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"トレイのアイコンを読めません: {ex.Message}");
            }
            return Drawing.SystemIcons.Application;
        }

        /// <summary>メニューを開くたびに、再生と録音の状態から、文言と有効・無効を決める (判断は <see cref="TrayMenuState"/>)。</summary>
        void UpdateTrayMenu()
        {
            var state = TrayMenuState.For(_engine.IsRunning, _engine.IsRecording);
            _trayRestore!.Text = Loc.T(TrayMenuState.RestoreKey);
            _trayRestore.Enabled = TrayMenuState.RestoreEnabled;
            _trayPlay!.Text = Loc.T(state.PlayKey);
            _trayRecord!.Text = Loc.T(state.RecordKey);
            _trayRecord.Enabled = state.RecordEnabled;     // 再生中でなければグレーアウト
            _trayExit!.Text = Loc.T(TrayMenuState.ExitKey);
            _trayExit.Enabled = TrayMenuState.ExitEnabled;
        }

        void UpdateTrayText()
        {
            if (_tray == null) return;
            var state = TrayMenuState.For(_engine.IsRunning, _engine.IsRecording);
            string text = $"{Title} - {Loc.T(state.TipKey)}";
            _tray.Text = text.Length <= TrayTipMaxLength ? text : text[..TrayTipMaxLength];
        }

        /// <summary>
        /// 利用者に知らせる。ウィンドウが見えていれば、いつものメッセージボックス。
        /// 最小化中 (非表示) は、非表示のウィンドウを所有者にしたメッセージボックスが出てしまうので、バルーン通知にする。
        /// </summary>
        void NotifyUser(string message, MessageBoxImage icon)
        {
            if (IsVisible || _tray is not { Visible: true })
            {
                MessageBox.Show(this, message, Title, MessageBoxButton.OK, icon);
                return;
            }
            var balloonIcon = icon == MessageBoxImage.Error ? WinForms.ToolTipIcon.Error : WinForms.ToolTipIcon.Warning;
            string text = message.Length <= BalloonMaxLength ? message : message[..BalloonMaxLength] + "…";
            _tray.ShowBalloonTip(8000, Title, text, balloonIcon);
        }

        /// <summary>トレイのアイコンを片付ける (残すと、終了後もアイコンが残る)。</summary>
        void DisposeTray()
        {
            if (_tray == null) return;
            _tray.Visible = false;
            _tray.Dispose();
            _tray.ContextMenuStrip?.Dispose();
            _tray = null;
        }
    }
}
