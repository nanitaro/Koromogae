using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Koromogae.Audio;

namespace Koromogae
{
    /// <summary>
    /// メイン画面。役割ごとに partial で分けている:
    /// このファイル = 起動・終了・デバイス・開始/停止、MainWindow.Settings.cs = 設定項目と AppSettings の対応、
    /// MainWindow.Diagnostics.cs = メーター・遅延・診断の表示、MainWindow.Plugins.cs = プラグインのチェーン、
    /// MainWindow.Recording.cs = 録音、MainWindow.Help.cs = メニューバーの「ヘルプ」。
    /// </summary>
    public partial class MainWindow : Window
    {
        readonly AudioEngine _engine = new();
        readonly AppSettings _settings = AppSettings.Current;
        // まだロードされていない(または今回ロードに失敗した)プラグインの設定。終了時にそのまま保存し直して、状態を失わないようにする
        readonly List<PluginSetting> _unloaded = new();
        readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(80) };

        bool _initialized;

        public MainWindow()
        {
            InitializeComponent();
            // 画面が小さい環境 (1366x768 など) では、既定の高さが画面に収まらないので、作業領域に収める
            var work = SystemParameters.WorkArea;
            MinHeight = Math.Min(MinHeight, work.Height);
            Height = Math.Min(Height, work.Height);
            PluginList.ItemsSource = _engine.Chain.Slots;
            _timer.Tick += Timer_Tick;
        }

        async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            InitSettingsCombos();
            ApplySettings(_settings);
            UpdateDitherEnabled(running: false);
            RefreshDevices(_settings.InputDevice, _settings.OutputDevice);
            _initialized = true;
            _timer.Start();

            // 引数 --minimized: 最小化 (= トレイへ格納) で始める。ウィンドウは一瞬だけ最小化として現れ、すぐ消える。
            // 以降の警告は、ウィンドウが見えていないので、メッセージボックスではなくバルーン通知になる (NotifyUser)
            // 「最小化時にトレイへ格納する」の設定が off でも、起動時だけはトレイへ (その後の最小化は設定に従う)
            if (App.StartMinimized) MinimizeToTrayAtStartup();

            if (_settings.LoadMessage != null)
            {
                StatusText.Text = _settings.LoadMessage;
                NotifyUser(_settings.LoadMessage, MessageBoxImage.Warning);
            }

            // ポータブルモードを望んだが、本体のフォルダに書き込めず、標準の保存先に戻したとき (設定を黙って失わないように、1 回だけ知らせる)
            if (AppPaths.FallbackMessage != null)
                NotifyUser(AppPaths.FallbackMessage, MessageBoxImage.Warning);

            await RestorePluginsAsync();

            // 引数 --start: 保存された設定のまま、自動で再生を始める。プラグインの復元が終わってから (チェーンに入った状態で始めるため)。
            // 始められなければ、ToggleRunning が通知する (最小化中はバルーン)。アプリは起動したままで、手動で直せる
            if (App.StartPlayback && !_engine.IsRunning)
            {
                AppLog.Info("引数 --start により、再生を自動で開始します");
                ToggleRunning();
                UpdateTrayText();
            }
        }

        void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _timer.Stop();
            CaptureSettings().Save(); // 失敗は AppLog に記録される
            _engine.Dispose();
            DisposeTray();
        }

        // ---- デバイス ----

        void RefreshDevices(string? preferredInput = null, string? preferredOutput = null)
        {
            preferredInput ??= (InputCombo.SelectedItem as AudioDevice)?.Name;
            preferredOutput ??= (OutputCombo.SelectedItem as AudioDevice)?.Name;

            var (inputs, outputs) = AudioDevices.Enumerate(SelectedApi);
            InputCombo.ItemsSource = inputs;
            OutputCombo.ItemsSource = outputs;
            InputCombo.SelectedItem = inputs.FirstOrDefault(d => d.Name == preferredInput)
                                      ?? inputs.FirstOrDefault(d => d.IsDefaultInput) ?? inputs.FirstOrDefault();
            OutputCombo.SelectedItem = outputs.FirstOrDefault(d => d.Name == preferredOutput)
                                       ?? outputs.FirstOrDefault(d => d.IsDefaultOutput) ?? outputs.FirstOrDefault();

            if (inputs.Count == 0 && outputs.Count == 0)
                StatusText.Text = Loc.F("Status.NoDevices", AudioDevices.Label(SelectedApi));
        }

        void ApiCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initialized && !_engine.IsRunning) RefreshDevices();
        }

        void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (_engine.IsRunning) return;
            AudioDevices.Refresh();   // PortAudio は再初期化しないとデバイス一覧を更新しない
            RefreshDevices();
        }

        // ---- 開始/停止 ----

        void StartButton_Click(object sender, RoutedEventArgs e) => ToggleRunning();

        /// <summary>再生を始める/止める。画面の「開始/停止」ボタンと、トレイのメニューが共有する (設定は画面の部品の値)。</summary>
        void ToggleRunning()
        {
            if (_engine.IsRunning)
            {
                if (_engine.IsRecording) StopRecordingUi();    // 先に録音を終えて、結果を表示する
                _engine.Stop();
                SetRunningUi(false);
                StatusText.Text = Loc.T("Play.Status.Stopped");
                UpdateLatencyText();
                return;
            }

            if (InputCombo.SelectedItem is not AudioDevice input || OutputCombo.SelectedItem is not AudioDevice output)
            {
                AppLog.Warn("再生を開始できません: 録音デバイスか再生デバイスが選ばれていません");   // 最小化中はバルーンだけなので、あとから原因を追えるように残す
                NotifyUser(Loc.T("Msg.SelectDevices"), MessageBoxImage.Warning);
                return;
            }

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                _engine.Start(input, output, ReadForm().ToEngineOptions());
                SetRunningUi(true);
                StatusText.Text = _engine.Description;
            }
            catch (Exception ex)
            {
                AppLog.Error($"再生を開始できません: {input.Name} → {output.Name}", ex);   // 最小化中・自動の開始 (--start) では、通知だけでは原因が残らない
                SetRunningUi(false);
                StatusText.Text = Loc.T("Status.StartFailed");
                NotifyUser(ex.Message, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        void SetRunningUi(bool running)
        {
            StartButton.Content = running ? Loc.T("Play.Stop") : Loc.T("Play.Start");
            foreach (var control in LockedWhileRunning) control.IsEnabled = !running;
            RecordButton.IsEnabled = running;
            if (!running) RecordStatusText.Text = _engine.RecordingStatus == null ? Loc.T("Rec.Status.Stopped") : RecordStatusText.Text;
            else UpdateRecordReadyText();
            UpdateDitherEnabled(running);
        }
    }
}
