using System.Globalization;
using System.Windows;
using Koromogae.Audio;
using Koromogae.Ui;

namespace Koromogae
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        static string? _externalLanguageEnv;   // 起動時に環境変数で外から渡された言語の指定 (自動テストや動作確認用)。再起動でそのまま引き継ぐ
        static bool _restartRequested;

        /// <summary>起動時の引数 (<c>--minimized</c> / <c>--start</c>)。自動の再起動 (言語の切り替え) では引き継がない。</summary>
        public static StartupArgs Args { get; private set; } = StartupArgs.Parse(null);

        /// <summary>最小化 (トレイ) で起動するか。</summary>
        public static bool StartMinimized => Args.Minimized;

        /// <summary>起動したら、自動で再生を始めるか。</summary>
        public static bool StartPlayback => Args.Start;

        /// <summary>終了のあとで、自分自身を起動し直す (言語を変えたとき)。ウィンドウを閉じる前に呼ぶ。</summary>
        public static void RequestRestart() => _restartRequested = true;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 保存先 (ポータブルモードなら本体のフォルダ) を、子プロセスのプラグインホストにも伝える。
            // ホスト側の HostLog は、この環境変数だけで保存先を決める (ホストは別のフォルダにあり、目印を自分では探せない)
            Environment.SetEnvironmentVariable(AppPaths.DataDirEnvVar, AppPaths.DataDirectory);

            // 二重起動はしない (同じ保存先の設定ファイルを 2 つが書き合わないように)。2 つ目は、最初のウィンドウを前面に出して終わる。
            // 音声も言語も初期化する前に判断する
            if (!AcquireSingleInstance())
            {
                _isSecondInstance = true;
                Shutdown(0);
                return;
            }

            // 画面の言語は、ウィンドウやダイアログを作る前に決める。ホストにも同じ言語を渡す (ホストが返すエラーの文面が、画面と同じ言語になる)
            _externalLanguageEnv = Environment.GetEnvironmentVariable(UiLanguages.EnvVar);
            var language = UiLanguages.Resolve(AppSettings.Current.Language, _externalLanguageEnv, CultureInfo.CurrentUICulture.Name);
            Loc.SetCulture(UiLanguages.ToCulture(language));
            Environment.SetEnvironmentVariable(UiLanguages.EnvVar, UiLanguages.ToCode(language));

            string? error = AudioDevices.Initialize();
            if (error != null)
            {
                MessageBox.Show(Loc.F("Err.PortAudioInit", error), "Koromogae",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
            Args = StartupArgs.Parse(e.Args);
            AppLog.Info(AppInfo.StartupLine());   // どの版・環境で動いたかを、ログから分かるように
            if (Args.Minimized || Args.Start) AppLog.Info($"起動の引数: 最小化={Args.Minimized}, 自動で再生={Args.Start}");
            if (Args.Unknown.Count > 0) AppLog.Warn($"知らない引数を無視しました: {string.Join(" ", Args.Unknown)}");
            AppLog.Info(AppPaths.Location.Describe());
            if (AppPaths.Location.FallbackReason != null) AppLog.Warn(AppPaths.Location.FallbackReason.Replace("\n", " "));
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (!_isSecondInstance) AudioDevices.Shutdown();
            ReleaseSingleInstance();    // 再起動 (Restart) の前に。残すと、新しいプロセスが二重起動とみなされて終了してしまう
            if (_restartRequested) Restart();
            base.OnExit(e);
        }

        // ---- 二重起動の防止 ----

        static Mutex? _instanceMutex;
        static EventWaitHandle? _activateEvent;
        static RegisteredWaitHandle? _activateWait;
        static bool _isSecondInstance;

        /// <summary>
        /// 最初の起動なら true (Mutex を持つ)。すでに起動していれば、そのウィンドウを前面に出すよう知らせて false。
        /// 名前は保存先ごと (<see cref="SingleInstanceName"/>): 別の場所にコピーしたポータブル版は、別々に起動できる。
        /// </summary>
        bool AcquireSingleInstance()
        {
            try
            {
                string dir = AppPaths.DataDirectory;
                // イベントを先に作る (最初のプロセスがまだ作っていなくても、2 つ目の通知が届くように。名前で作る/開くは同じ呼び出し)
                var activate = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceName.ActivateEventName(dir));
                var mutex = new Mutex(false, SingleInstanceName.MutexName(dir));
                bool owned;
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }   // 前のプロセスが異常終了して残ったもの。引き継ぐ

                if (!owned)
                {
                    AppLog.Info("すでに起動しているため、このプロセスは終了し、起動中のウィンドウを前面に出します");
                    AllowSetForegroundWindow(-1);    // ASFW_ANY: 最初のプロセスが前面に出られるようにする (いま入力を受けたのはこちら)
                    activate.Set();
                    activate.Dispose();
                    mutex.Dispose();
                    return false;
                }

                _instanceMutex = mutex;
                _activateEvent = activate;
                _activateWait = ThreadPool.RegisterWaitForSingleObject(activate,
                    (_, _) => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromOutside()), null, Timeout.Infinite, executeOnlyOnce: false);
                return true;
            }
            catch (Exception ex)
            {
                // 名前付きのオブジェクトが作れない環境でも、起動はできるようにする (二重起動の防止だけを諦める)
                AppLog.Warn($"二重起動の防止を準備できません: {ex.Message}");
                return true;
            }
        }

        static void ReleaseSingleInstance()
        {
            _activateWait?.Unregister(null);
            _activateWait = null;
            try { _instanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { }    // 取得したスレッドでない・すでに解放済み
            _instanceMutex?.Dispose();
            _instanceMutex = null;
            _activateEvent?.Dispose();
            _activateEvent = null;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        static extern bool AllowSetForegroundWindow(int processId);

        /// <summary>
        /// 自分自身を起動し直す。子に引き継ぐ環境変数は、外から渡されたものだけにする:
        /// 言語は、アプリ自身が設定した値 (子に渡すと、新しく選んだ言語を上書きしてしまう) を除く。
        /// 保存先は、環境変数で指定されたときだけ引き継ぎ、そうでなければ (標準・ポータブル) 子が同じ規則で決め直す。
        /// </summary>
        static void Restart()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath ?? "Koromogae.exe")
                {
                    UseShellExecute = false,
                    WorkingDirectory = AppContext.BaseDirectory,
                };
                if (_externalLanguageEnv == null) psi.Environment.Remove(UiLanguages.EnvVar);
                else psi.Environment[UiLanguages.EnvVar] = _externalLanguageEnv;
                if (AppPaths.Mode != DataLocationMode.Override) psi.Environment.Remove(AppPaths.DataDirEnvVar);
                System.Diagnostics.Process.Start(psi)?.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Error("再起動できません", ex);
            }
        }
    }
}
