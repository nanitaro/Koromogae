using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VstCore;
using Xunit;

namespace PluginHost.Tests;

/// <summary>
/// エディタを閉じるコマンド (CmdCloseEditor)。最小化でトレイへ格納するとき、本体がすべてのエディタを閉じるのに使う。
/// 偽ホストでコマンドの往復を、実プラグイン (あれば) で、ウィンドウが実際に消えることを確かめる。
/// </summary>
public class CloseEditorTests
{
    const string EditorClass = "VstCoreEditorWindow";

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int max);
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    static List<IntPtr> EditorWindows(int pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (p == (uint)pid)
            {
                var sb = new StringBuilder(64);
                GetClassNameW(h, sb, sb.Capacity);
                if (sb.ToString() == EditorClass && IsWindowVisible(h)) list.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static bool WaitFor(Func<bool> condition, int milliseconds = 10_000)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < milliseconds)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    static RemotePlugin StartReal(string path)
    {
        var bitness = PeInfo.GetBitness(path);
        return RemotePlugin.Start(TestEnvironment.HostExe(bitness), path, bitness, 48000);
    }

    // ---- 偽ホスト: コマンドの往復 ----

    [Fact]
    public void Closing_the_editor_round_trips_through_the_host_without_faulting()
    {
        using var client = RemotePlugin.Start(TestEnvironment.FakeHostExe, "ok", PluginBitness.X86, 48000);

        client.CloseEditor();
        client.CloseEditor();                                      // 開いていなくても、何度呼んでも無害

        Assert.False(client.IsFaulted);
        Assert.Equal(new byte[] { 1, 2, 3 }, client.GetState());   // コマンドの応答を読み残さず、同期が保たれている
    }

    // ---- 実プラグイン: ウィンドウが実際に閉じる ----

    [SkippableFact]
    public void Closing_the_editor_closes_the_real_editor_window_of_a_vst3_plugin()
    {
        Skip.If(TestEnvironment.Vst3x64 == null, "VST3 (64bit) のプラグインが手元にありません");
        using var client = StartReal(TestEnvironment.Vst3x64!);
        Skip.IfNot(client.HasEditor, "エディタを持たないプラグイン");

        client.OpenEditor();
        Skip.IfNot(WaitFor(() => EditorWindows(client.ProcessId).Count > 0), "エディタのウィンドウを取得できません (対話的なデスクトップが必要)");

        client.CloseEditor();

        Assert.True(WaitFor(() => EditorWindows(client.ProcessId).Count == 0), "エディタのウィンドウが閉じていません");
        Assert.False(client.IsFaulted);
    }

    [SkippableFact]
    public void The_editor_can_be_opened_again_after_it_was_closed()
    {
        Skip.If(TestEnvironment.Vst3x64 == null, "VST3 (64bit) のプラグインが手元にありません");
        using var client = StartReal(TestEnvironment.Vst3x64!);
        Skip.IfNot(client.HasEditor, "エディタを持たないプラグイン");

        client.OpenEditor();
        Skip.IfNot(WaitFor(() => EditorWindows(client.ProcessId).Count > 0), "エディタのウィンドウを取得できません (対話的なデスクトップが必要)");
        client.CloseEditor();
        Assert.True(WaitFor(() => EditorWindows(client.ProcessId).Count == 0));

        client.OpenEditor();                                       // 最小化から戻したあとに、また開ける

        Assert.True(WaitFor(() => EditorWindows(client.ProcessId).Count == 1), "閉じたあとに、エディタを開き直せません");
    }

    [SkippableFact]
    public void Closing_the_editor_closes_the_real_editor_window_of_a_vst2_plugin()
    {
        Skip.If(TestEnvironment.Vst2x86WithPresets == null, "VST2 (32bit。エディタあり) のプラグインが手元にありません");
        using var client = StartReal(TestEnvironment.Vst2x86WithPresets!);
        Skip.IfNot(client.HasEditor, "エディタを持たないプラグイン");

        client.OpenEditor();
        Skip.IfNot(WaitFor(() => EditorWindows(client.ProcessId).Count > 0), "エディタのウィンドウを取得できません (対話的なデスクトップが必要)");

        client.CloseEditor();

        Assert.True(WaitFor(() => EditorWindows(client.ProcessId).Count == 0), "エディタのウィンドウが閉じていません");
        Assert.False(client.IsFaulted);
    }

    [SkippableFact]
    public void Closing_the_editor_of_a_plugin_without_an_open_editor_is_harmless()
    {
        Skip.If(TestEnvironment.Vst2x64 == null, "VST2 (64bit) のプラグインが手元にありません");
        using var client = StartReal(TestEnvironment.Vst2x64!);

        client.CloseEditor();

        Assert.False(client.IsFaulted);
        Assert.Empty(EditorWindows(client.ProcessId));
    }
}