using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows.Automation;
using Koromogae;
using Xunit;

namespace App.Tests;

/// <summary>
/// 本体 (Koromogae.exe) を別プロセスで起動し、UI オートメーションで操作する。
/// 設定・ログの保存先は一時フォルダに変えて起動する (環境変数 KOROMOGAE_DATA_DIR)ので、利用者の設定は読み書きしない。
/// 「開始」は押さない (音声デバイスを使わない)。
/// </summary>
public sealed class AppHarness : IDisposable
{
    public static string ExePath { get; } = typeof(AppHarness).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "AppExe").Value!;

    /// <summary>設定・ログが保存されるはずのフォルダ。通常は一時フォルダ (環境変数で指定)。ポータブルのテストでは、コピーした本体のフォルダ。</summary>
    public string DataDir { get; }

    /// <summary>標準の保存先 (%APPDATA%\Koromogae の %APPDATA% を、一時フォルダに差し替えたもの)。ポータブルのテストで、ここに何も書かれないことを確かめる。</summary>
    public string? StandardDir { get; }

    readonly string _cleanupDir;
    public string SettingsPath => Path.Combine(DataDir, "settings.json");
    public string LogPath => Path.Combine(DataDir, "app.log");

    readonly Process _process;
    readonly AutomationElement _root;

    AppHarness(string dataDir, Process process, AutomationElement root, string? standardDir = null, string? cleanupDir = null)
    {
        DataDir = dataDir;
        StandardDir = standardDir;
        _cleanupDir = cleanupDir ?? dataDir;
        _process = process;
        _root = root;
        _hwnd = process.MainWindowHandle;   // 隠れると取れなくなるので、最初に控える (UI オートメーションは起動の直後に失敗することがあるので使わない)
    }

    readonly IntPtr _hwnd;
    ProcessStartInfo? _launchInfo;

    /// <summary>
    /// 同じ保存先 (同じ環境変数) で、もう 1 つ本体を起動する (二重起動の確認用)。終了は呼び出し側が待つ。
    /// </summary>
    public Process StartAnother(params string[] args)
    {
        var template = _launchInfo ?? throw new InvalidOperationException("起動の情報がありません");
        var psi = new ProcessStartInfo(template.FileName) { UseShellExecute = false, WorkingDirectory = template.WorkingDirectory };
        foreach (string a in args) psi.ArgumentList.Add(a);
        foreach (var pair in template.Environment) psi.Environment[pair.Key] = pair.Value;
        return Process.Start(psi)!;
    }

    /// <summary>起動して、設定が画面に反映される (サウンド API が選ばれる) まで待つ。デスクトップが使えないなどで起動できなければ、テストをスキップする。</summary>
    public static AppHarness StartOrSkip(string? settingsJson = null, string language = "ja")
    {
        Skip.IfNot(File.Exists(ExePath), $"本体が見つかりません: {ExePath}");

        string dir = Path.Combine(Path.GetTempPath(), "KoromogaeUiTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        if (settingsJson != null) File.WriteAllText(Path.Combine(dir, "settings.json"), settingsJson);

        var psi = new ProcessStartInfo(ExePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ExePath)! };
        psi.Environment[AppPaths.DataDirEnvVar] = dir;
        psi.Environment[UiLanguages.EnvVar] = language;
        return LaunchOrSkip(psi, dir, standardDir: null, cleanupDir: dir);
    }

    /// <summary>
    /// ポータブルモードの確認用に起動する。ビルドの出力フォルダを一時フォルダへコピーし (出力を汚さない)、コピー先に目印のファイルを置いて、そこから起動する。
    /// 標準の保存先の起点 (%APPDATA% の部分) は、別の一時フォルダに差し替える (KOROMOGAE_APPDATA_DIR) ので、
    /// ポータブルが効かなくても、利用者の本物の設定には届かない。
    /// </summary>
    /// <param name="markerName">コピー先に作る目印のファイル名 (空なら作らない)。</param>
    /// <param name="withDataDirOverride">true なら、環境変数 KOROMOGAE_DATA_DIR も渡す (その保存先が、目印より優先されることの確認用)。</param>
    /// <param name="beforeLaunch">起動の前に、コピー先のフォルダ (本体のフォルダ) を受け取って、ファイルを足す・消す処理 (例: マニュアルを消した配布物の確認)。</param>
    /// <param name="language">画面の言語 (<c>ja</c> / <c>en</c>)。省略すると日本語。</param>
    public static AppHarness StartPortableOrSkip(string markerName, bool withDataDirOverride = false,
        Action<string>? beforeLaunch = null, string? language = null)
    {
        Skip.IfNot(File.Exists(ExePath), $"本体が見つかりません: {ExePath}");

        string root = Path.Combine(Path.GetTempPath(), "KoromogaePortable_" + Guid.NewGuid().ToString("N")[..8]);
        string appDir = Path.Combine(root, "app");
        string fakeAppData = Path.Combine(root, "appdata");
        string overrideDir = Path.Combine(root, "override");
        Directory.CreateDirectory(fakeAppData);
        CopyDirectory(Path.GetDirectoryName(ExePath)!, appDir);
        if (!string.IsNullOrEmpty(markerName)) File.WriteAllBytes(Path.Combine(appDir, markerName), Array.Empty<byte>());
        if (withDataDirOverride) Directory.CreateDirectory(overrideDir);
        beforeLaunch?.Invoke(appDir);

        var psi = new ProcessStartInfo(Path.Combine(appDir, Path.GetFileName(ExePath))) { UseShellExecute = false, WorkingDirectory = appDir };
        psi.Environment.Remove(AppPaths.DataDirEnvVar);
        psi.Environment[AppPaths.AppDataEnvVar] = fakeAppData;
        if (withDataDirOverride) psi.Environment[AppPaths.DataDirEnvVar] = overrideDir;
        if (language != null) psi.Environment[UiLanguages.EnvVar] = language;
        return LaunchOrSkip(psi, withDataDirOverride ? overrideDir : appDir, Path.Combine(fakeAppData, "Koromogae"), root);
    }

    static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (string dir in Directory.GetDirectories(source)) CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    /// <summary>起動して、設定が画面に反映される (サウンド API が選ばれる) まで待つ。画面を取得できなければ、テストをスキップする。</summary>
    static AppHarness LaunchOrSkip(ProcessStartInfo psi, string dataDir, string? standardDir, string cleanupDir)
    {
        // 画面の言語は日本語に固定する (どの言語の Windows で動かしても、同じ結果になるように)。英語を確かめるテストは、StartOrSkip(language: "en") で指定する
        if (!psi.Environment.ContainsKey(UiLanguages.EnvVar)) psi.Environment[UiLanguages.EnvVar] = "ja";
        var process = Process.Start(psi)!;

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                process.Refresh();
                if (process.HasExited) break;
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    try
                    {
                        var root = AutomationElement.FromHandle(process.MainWindowHandle);
                        var api = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "ApiCombo"));
                        if (api != null && SelectedName(api) != null) return new AppHarness(dataDir, process, root, standardDir, cleanupDir) { _launchInfo = psi };
                    }
                    catch (System.Runtime.InteropServices.COMException) { /* 起動の直後や、他のアプリが重いとき、UI オートメーションが一時的に失敗する。待ってやり直す */ }
                    catch (ElementNotAvailableException) { }
                }
                Thread.Sleep(200);
            }
        }
        catch
        {
            Cleanup(process, cleanupDir);
            throw;
        }

        Cleanup(process, cleanupDir);
        Skip.If(true, "本体の画面を取得できませんでした (対話的なデスクトップセッションが必要です)");
        throw new InvalidOperationException();   // Skip.If が投げるので到達しない
    }

    // ---- 部品の取得 ----

    public AutomationElement Find(string automationId)
        => _root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
           ?? throw new InvalidOperationException($"部品が見つかりません: {automationId}");

    // ---- メッセージボックス (ネイティブの #32770。ボタンと文は、UI オートメーション上は Pane として見える) ----

    /// <summary>メッセージボックスを探す (主ウィンドウの子として現れる)。出なければ null。</summary>
    public AutomationElement? TryFindMessageBox(double seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        do
        {
            foreach (AutomationElement child in _root.FindAll(TreeScope.Children, Condition.TrueCondition))
                if (child.Current.ClassName == "#32770") return child;
            Thread.Sleep(150);
        }
        while (DateTime.UtcNow < deadline);
        return null;
    }

    /// <summary>メッセージボックスの本文 (改行区切り)。</summary>
    public static string MessageBoxText(AutomationElement box)
        => string.Join("\n", box.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Static"))
            .Cast<AutomationElement>().Select(e => e.Current.Name).Where(n => n.Length > 0));

    /// <summary>メッセージボックスのボタンの名前 (OS の言語で決まる。例: はい(Y))。</summary>
    public static IReadOnlyList<string> MessageBoxButtons(AutomationElement box)
        => box.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Button"))
            .Cast<AutomationElement>().Select(e => e.Current.Name).ToList();

    /// <summary>メッセージボックスのボタンを、名前が <paramref name="prefixes"/> のどれかで始まるものを押す (BM_CLICK)。</summary>
    public static void ClickMessageBoxButton(AutomationElement box, params string[] prefixes)
    {
        var button = box.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Button"))
            .Cast<AutomationElement>().First(e => prefixes.Any(p => e.Current.Name.StartsWith(p, StringComparison.Ordinal)));
        PostMessage((IntPtr)button.Current.NativeWindowHandle, 0x00F5, IntPtr.Zero, IntPtr.Zero);   // BM_CLICK
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>名前 (表示されている文字) で探す。ラベル (TextBlock) 用。</summary>
    public AutomationElement FindByText(string text)
        => _root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, text))
           ?? throw new InvalidOperationException($"文字が見つかりません: {text}");

    /// <summary>グループボックスを、見出しの文字で探す。</summary>
    public AutomationElement FindGroup(string header)
        => _root.FindFirst(TreeScope.Descendants, new AndCondition(
               new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group),
               new PropertyCondition(AutomationElement.NameProperty, header)))
           ?? throw new InvalidOperationException($"グループが見つかりません: {header}");

    /// <summary>ウィンドウの大きさを変える (物理ピクセル)。最小の大きさより小さくしても、最小の大きさで止まる。</summary>
    public void Resize(double width, double height)
        => ((TransformPattern)_root.GetCurrentPattern(TransformPattern.Pattern)).Resize(width, height);

    /// <summary>縦にスクロールできる (中身が表示領域に収まっていない) か。</summary>
    public bool IsVerticallyScrollable(string id)
        => ((ScrollPattern)Find(id).GetCurrentPattern(ScrollPattern.Pattern)).Current.VerticallyScrollable;

    /// <summary>一番下までスクロールする。</summary>
    public void ScrollToBottom(string id)
        => ((ScrollPattern)Find(id).GetCurrentPattern(ScrollPattern.Pattern)).SetScrollPercent(ScrollPattern.NoScroll, 100);

    public bool IsEnabled(string id) => Find(id).Current.IsEnabled;
    public string HelpText(string id) => Find(id).Current.HelpText;
    public System.Windows.Rect Bounds(string id) => Find(id).Current.BoundingRectangle;
    public string WindowTitle => _root.Current.Name;
    public System.Windows.Rect WindowBounds => _root.Current.BoundingRectangle;

    /// <summary>チェックボックスの状態。</summary>
    public bool IsChecked(string id)
        => ((TogglePattern)Find(id).GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;

    /// <summary>チェックボックスを、指定の状態にする (違っていれば切り替える)。</summary>
    public void SetChecked(string id, bool value)
    {
        if (IsChecked(id) != value) ((TogglePattern)Find(id).GetCurrentPattern(TogglePattern.Pattern)).Toggle();
    }

    /// <summary>コンボボックスで選ばれている項目の表示名。</summary>
    public string Selected(string comboId) => SelectedName(Find(comboId)) ?? "";

    static string? SelectedName(AutomationElement combo)
    {
        if (!combo.TryGetCurrentPattern(SelectionPattern.Pattern, out var pattern)) return null;
        var selection = ((SelectionPattern)pattern).Current.GetSelection();
        return selection.Length == 0 ? null : selection[0].Current.Name;
    }

    /// <summary>コンボボックスの選択肢の表示名 (開いて読み、閉じる)。</summary>
    public IReadOnlyList<string> Items(string comboId)
    {
        var combo = Find(comboId);
        var expand = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expand.Expand();
        try
        {
            var items = combo.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            return items.Cast<AutomationElement>().Select(i => i.Current.Name).ToList();
        }
        finally
        {
            expand.Collapse();
        }
    }

    /// <summary>コンボボックスの項目が選べる状態か (開いて読み、閉じる)。</summary>
    public bool IsItemEnabled(string comboId, string itemName)
    {
        var combo = Find(comboId);
        var expand = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expand.Expand();
        try
        {
            var item = combo.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>().FirstOrDefault(i => i.Current.Name == itemName)
                ?? throw new InvalidOperationException($"{comboId} に「{itemName}」がありません");
            return item.Current.IsEnabled;
        }
        finally
        {
            expand.Collapse();
        }
    }

    /// <summary>プラグイン一覧の項目の数。</summary>
    public int PluginListItemCount()
        => Find("PluginList").FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count;

    /// <summary>プラグイン一覧の先頭を選ぶ。</summary>
    public void SelectFirstPlugin()
    {
        var item = Find("PluginList").FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().First();
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
    }

    /// <summary>名前 (表示されている文字) で探したボタンを押す。</summary>
    public void ClickByText(string text)
        => ((InvokePattern)FindByText(text).GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    /// <summary>ボタンを押す。</summary>
    public void Click(string id)
        => ((InvokePattern)Find(id).GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    /// <summary>「圧縮オプション...」のダイアログ (別のウィンドウ) が開くのを待って返す。</summary>
    public AutomationElement WaitForWindow(string title, double seconds = 8)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            // 持ち主のウィンドウの子として見える場合と、デスクトップ直下に見える場合がある
            var cond = new AndCondition(
                new PropertyCondition(AutomationElement.NameProperty, title),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
            var found = _root.FindFirst(TreeScope.Descendants, cond) ?? AutomationElement.RootElement.FindFirst(TreeScope.Children, cond);
            if (found != null) return found;
            Thread.Sleep(100);
        }
        throw new InvalidOperationException($"ウィンドウが開きません: {title}");
    }

    /// <summary>ダイアログが閉じるのを待つ (ボタンの Invoke は非同期なので、閉じる前に次の操作をしないため)。</summary>
    public void WaitForWindowClosed(string title, double seconds = 8)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var cond = new AndCondition(
                new PropertyCondition(AutomationElement.NameProperty, title),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
            if (_root.FindFirst(TreeScope.Descendants, cond) == null && AutomationElement.RootElement.FindFirst(TreeScope.Children, cond) == null) return;
            Thread.Sleep(100);
        }
        throw new InvalidOperationException($"ウィンドウが閉じません: {title}");
    }

    /// <summary>ダイアログの中のコンボボックスで選ばれている項目の表示名。</summary>
    public static string SelectedIn(AutomationElement dialog, string automationId)
    {
        var combo = dialog.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        return SelectedName(combo) ?? "";
    }

    /// <summary>ダイアログの中のコンボボックスの項目を選ぶ。</summary>
    public static void SelectIn(AutomationElement dialog, string automationId, string itemName)
    {
        var combo = dialog.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        var expand = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expand.Expand();
        var item = combo.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().First(i => i.Current.Name == itemName);
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        expand.Collapse();
    }

    public static void ClickIn(AutomationElement dialog, string automationId)
        => ((InvokePattern)dialog.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
            .GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    /// <summary>コンボボックスの項目を、表示名で選ぶ。</summary>
    public void Select(string comboId, string itemName)
    {
        // 開いた一覧は、ほかのウィンドウがアクティブになると閉じる (テストを並列に走らせていて、別のアプリが起動したときなど)。
        // 項目が見つからないときは、一覧を開き直して、数回やり直す
        bool selected = false;
        for (int attempt = 0; attempt < 4 && !selected; attempt++)
        {
            var combo = Find(comboId);
            var expand = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            try
            {
                expand.Expand();
                AutomationElement? item = null;
                var until = DateTime.UtcNow.AddSeconds(2);
                do
                {
                    item = combo.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                        .Cast<AutomationElement>().FirstOrDefault(i => i.Current.Name == itemName);
                    if (item == null) Thread.Sleep(100);
                }
                while (item == null && DateTime.UtcNow < until);

                if (item != null)
                {
                    ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                    selected = true;
                }
            }
            catch (ElementNotAvailableException) { /* 一覧が閉じた。やり直す */ }
            finally
            {
                try { expand.Collapse(); } catch (ElementNotAvailableException) { }
            }
        }
        if (!selected) throw new InvalidOperationException($"{comboId} に「{itemName}」がありません");
        // 選択の反映は画面のスレッドで非同期に行われる。他のテストと同時に走って重いときでも、反映されてから次の操作に進む
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && Selected(comboId) != itemName) Thread.Sleep(20);
    }

    // ---- 最小化 (トレイへ格納) ----

    /// <summary>メインウィンドウのハンドル。</summary>
    public IntPtr WindowHandle => _hwnd;

    /// <summary>ウィンドウを最小化する (UI オートメーションの WindowPattern。利用者が最小化ボタンを押したのと同じ経路)。</summary>
    public void Minimize()
        => ((WindowPattern)_root.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Minimized);

    /// <summary>
    /// 利用者がタイトルバーの最小化ボタンを押したのと同じ経路 (WM_SYSCOMMAND / SC_MINIMIZE)。
    /// UI オートメーションの <see cref="Minimize"/> は WPF の WindowState を直接変えるだけで、この経路とは違う。
    /// </summary>
    public void MinimizeLikeUser() => PostMessage(_hwnd, 0x0112, (IntPtr)0xF020, IntPtr.Zero);

    /// <summary>ウィンドウを最大化する。</summary>
    public void Maximize()
        => ((WindowPattern)_root.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Maximized);

    /// <summary>ウィンドウが最小化 (アイコン化) されているか。トレイへ格納して隠れているときも、最小化のまま。</summary>
    public bool IsMinimized => IsIconic(_hwnd);

    /// <summary>ウィンドウが最大化されているか。</summary>
    public bool IsMaximized => IsZoomed(_hwnd);

    /// <summary>条件が満たされるまで待つ (満たされたら true)。</summary>
    public static bool WaitFor(Func<bool> condition, int milliseconds = 10_000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);

    /// <summary>ウィンドウが画面に表示されているか (最小化してトレイへ格納すると、非表示になる)。</summary>
    public bool IsWindowShown => IsWindowVisible(WindowHandle);

    /// <summary>プロセスがまだ動いているか。</summary>
    public bool IsProcessRunning
    {
        get
        {
            _process.Refresh();
            return !_process.HasExited;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int max);

    /// <summary>このプロセスの最上位のウィンドウ (非表示を含む) に、この題のものがあるか。
    /// 隠れているだけのウィンドウは UI オートメーションからは見えなくなるので、「閉じた」ことは、ウィンドウそのものが無いことで確かめる。</summary>
    public bool HasWindowTitled(string title)
    {
        bool found = false;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == (uint)_process.Id)
            {
                var sb = new System.Text.StringBuilder(256);
                GetWindowTextW(h, sb, sb.Capacity);
                if (sb.ToString() == title) found = true;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ---- 終了 ----

    /// <summary>ウィンドウを閉じて、終了するまで待つ (終了時に設定が保存される)。終了コードを返す。</summary>
    public int Close()
    {
        _process.CloseMainWindow();
        if (!_process.WaitForExit(15_000))
        {
            _process.Kill(entireProcessTree: true);
            throw new TimeoutException("閉じても終了しませんでした");
        }
        return _process.ExitCode;
    }

    /// <summary>終了後に保存された設定ファイルを読む。</summary>
    public JsonElement SavedSettings()
        => JsonDocument.Parse(File.ReadAllText(SettingsPath)).RootElement.Clone();

    public void Dispose() => Cleanup(_process, _cleanupDir);

    static void Cleanup(Process process, string dir)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch { /* すでに終了している */ }
        process.Dispose();
        try { Directory.Delete(dir, recursive: true); } catch { /* 一時フォルダなので残っても害はない */ }
    }
}

/// <summary>テキストボックスの読み書き (ValuePattern)。</summary>
internal static class UiaExtensions
{
    public static string GetValue(this AutomationElement e)
        => ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

    public static void SetValue(this AutomationElement e, string value)
        => ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
}