using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Koromogae;
using Xunit;

namespace App.Tests;

/// <summary>
/// 引数 <c>--minimized</c> で起動すると、ウィンドウを出さずに (トレイへ格納して) 始まる。
/// 画面を待つ既存の起動処理 (AppHarness) は使えないので、プロセスとウィンドウの存在・可視で確かめる。
/// <c>--start</c> (自動で再生を始める) は、音声デバイスを実際に開くので、自動テストにしない (解析は Ui.Tests の StartupArgsTests、動作は実機で確認)。
/// </summary>
public class StartMinimizedTests
{
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);

    /// <summary>このプロセスの最上位のウィンドウを、(題, 可視) で返す。</summary>
    static List<(string Title, bool Visible)> Windows(int pid)
    {
        var list = new List<(string, bool)>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (p == (uint)pid)
            {
                var sb = new StringBuilder(256);
                GetWindowTextW(h, sb, sb.Capacity);
                list.Add((sb.ToString(), IsWindowVisible(h)));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static bool WaitFor(Func<bool> condition, int milliseconds)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }

    /// <summary>引数つきで本体を起動する。設定・ログは一時フォルダ (利用者の設定は読み書きしない)。終了は Dispose。</summary>
    sealed class Launched : IDisposable
    {
        public Process Process { get; }
        public string DataDir { get; }
        public string LogPath => Path.Combine(DataDir, "app.log");
        public string Log => File.Exists(LogPath) ? File.ReadAllText(LogPath, Encoding.UTF8) : "";

        public Launched(params string[] args) : this(null, args) { }

        public Launched(string? settingsJson, string[] args)
        {
            DataDir = Path.Combine(Path.GetTempPath(), "KoromogaeArgsTest_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(DataDir);
            if (settingsJson != null) File.WriteAllText(Path.Combine(DataDir, "settings.json"), settingsJson);
            var psi = new ProcessStartInfo(AppHarness.ExePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(AppHarness.ExePath)! };
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.Environment[AppPaths.DataDirEnvVar] = DataDir;
            psi.Environment[UiLanguages.EnvVar] = "ja";
            Process = Process.Start(psi)!;
        }

        public void Dispose()
        {
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); Process.WaitForExit(5000); } catch { }
            Process.Dispose();
            try { Directory.Delete(DataDir, recursive: true); } catch { }
        }
    }

    // 注意: 起動の一瞬だけ、最小化したウィンドウ (タスクバーのボタン) が現れて、すぐトレイへ格納される (README にもそう書いてある)。
    // このテストが確かめるのは、最終的に非表示になること。一瞬の表示は許容している (画面の外で始めて、表示そのものをなくす方式は、未実装)。
    [SkippableFact]
    public void With_minimized_the_window_is_created_but_not_shown()
    {
        Skip.IfNot(File.Exists(AppHarness.ExePath), $"本体が見つかりません: {AppHarness.ExePath}");
        using var app = new Launched("--minimized");

        // メインのウィンドウが作られて (Window_Loaded まで済んで)、そのあと非表示になる
        bool hidden = WaitFor(() => Windows(app.Process.Id).Any(w => w.Title == "Koromogae") && !Windows(app.Process.Id).Any(w => w.Visible), 20_000);
        Skip.IfNot(app.Log.Contains("起動:"), "本体が起動しませんでした (対話的なデスクトップが必要)");

        Assert.True(hidden, "最小化で起動したのに、ウィンドウが表示されたままです。ウィンドウ: " + string.Join(", ", Windows(app.Process.Id)));
        Thread.Sleep(1500);                                          // 一瞬だけ現れて、すぐ消える。あとから再び現れないこと
        Assert.DoesNotContain(Windows(app.Process.Id), w => w.Visible);
        Assert.False(app.Process.HasExited);                         // 終了ではなく、トレイで動いている
        Assert.Contains("起動の引数: 最小化=True, 自動で再生=False", app.Log);
    }

    [SkippableFact]
    public void With_minimized_the_tray_is_used_even_when_the_tray_setting_is_off()
    {
        // 「最小化時にトレイへ格納する」が off でも、起動時の --minimized はトレイへ (その後の最小化は設定に従う)
        Skip.IfNot(File.Exists(AppHarness.ExePath), $"本体が見つかりません: {AppHarness.ExePath}");
        using var app = new Launched("""{"MinimizeToTray":false}""", new[] { "--minimized" });

        bool hidden = WaitFor(() => Windows(app.Process.Id).Any(w => w.Title == "Koromogae") && !Windows(app.Process.Id).Any(w => w.Visible), 20_000);
        Skip.IfNot(app.Log.Contains("起動:"), "本体が起動しませんでした (対話的なデスクトップが必要)");

        Assert.True(hidden, "設定が off でも、--minimized ではトレイへ格納されるはずです。ウィンドウ: " + string.Join(", ", Windows(app.Process.Id)));
        Thread.Sleep(1500);
        Assert.DoesNotContain(Windows(app.Process.Id), w => w.Visible);
        Assert.False(app.Process.HasExited);
    }

    [SkippableFact]
    public void Without_arguments_the_window_is_shown()
    {
        Skip.IfNot(File.Exists(AppHarness.ExePath), $"本体が見つかりません: {AppHarness.ExePath}");
        using var app = new Launched();

        bool shown = WaitFor(() => Windows(app.Process.Id).Any(w => w.Title == "Koromogae" && w.Visible), 20_000);
        Skip.IfNot(app.Log.Contains("起動:"), "本体が起動しませんでした (対話的なデスクトップが必要)");

        Assert.True(shown);
        Assert.DoesNotContain("起動の引数", app.Log);                  // 引数が無ければ、引数のログは出ない
    }

    [SkippableFact]
    public void An_unknown_argument_is_ignored_with_a_warning_and_the_window_is_shown()
    {
        Skip.IfNot(File.Exists(AppHarness.ExePath), $"本体が見つかりません: {AppHarness.ExePath}");
        using var app = new Launched("--foo");

        bool shown = WaitFor(() => Windows(app.Process.Id).Any(w => w.Title == "Koromogae" && w.Visible), 20_000);
        Skip.IfNot(app.Log.Contains("起動:"), "本体が起動しませんでした (対話的なデスクトップが必要)");

        Assert.True(shown);                                          // 知らない引数で、起動は妨げない
        Assert.Contains("知らない引数を無視しました: --foo", app.Log);
    }
}