using System.IO;
using System.Windows.Automation;
using Xunit;

namespace App.Tests;

/// <summary>
/// 最小化するとタスクトレイ (通知領域) へ格納する。ウィンドウは非表示になり (タスクバーのボタンも消える)、プロセスは動き続ける。
/// 最小化と同時に、エディタ (ここでは汎用のパラメータ画面) は閉じる。
/// トレイのアイコンの右クリックと元に戻す操作は、通知領域の UI オートメーションが環境に左右されるので、自動テストにしない。
/// 判断 (メニューの文言と有効・無効) は Ui.Tests の TrayMenuStateTests が確かめる。
/// </summary>
public class TrayTests
{
    static bool WaitFor(Func<bool> condition, int milliseconds = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }

    [SkippableFact]
    public void Minimizing_hides_the_window_and_keeps_the_app_running_in_the_tray()
    {
        using var app = AppHarness.StartOrSkip("""{"MinimizeToTray":true}""");
        Assert.True(app.IsWindowShown);

        app.Minimize();

        Assert.True(WaitFor(() => !app.IsWindowShown), "最小化しても、ウィンドウが非表示になりません (トレイへ格納されていません)");
        Assert.True(app.IsProcessRunning);      // 終了ではなく、トレイへ格納
    }

    [SkippableFact]
    public void Minimizing_closes_the_parameter_window_that_was_open()
    {
        string plugin = @"C:\Program Files\Steinberg\VSTPlugins\airwindows\BitShiftGain64.dll";    // エディタを持たない (汎用のパラメータ画面で操作する)
        Skip.IfNot(File.Exists(plugin), "手元に Airwindows の BitShiftGain (64bit) がありません");
        string settings = System.Text.Json.JsonSerializer.Serialize(new { MinimizeToTray = true, Plugins = new[] { new { Path = plugin, Enabled = true } } });
        using var app = AppHarness.StartOrSkip(settings);

        // 起動時に復元されたプラグインを選んで、「パラメータ...」を開く
        Assert.True(WaitFor(() => app.PluginListItemCount() == 1, 20000), "プラグインが復元されません");
        app.SelectFirstPlugin();
        app.ClickByText("パラメータ...");
        const string title = "BitShiftGain - パラメータ";
        Assert.NotNull(app.WaitForWindow(title));

        app.Minimize();

        Assert.True(WaitFor(() => !app.IsWindowShown));
        // 最小化と同時に閉じる (何を開いていたかは覚えない)。親が隠れると子も見えなくなるので、「隠れた」ではなく、ウィンドウそのものが無いことを確かめる
        Assert.True(WaitFor(() => !app.HasWindowTitled(title)), "パラメータ画面が、閉じずに残っています");
    }
}