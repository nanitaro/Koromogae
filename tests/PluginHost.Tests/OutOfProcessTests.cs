using System.Diagnostics;
using VstCore;
using Xunit;

namespace PluginHost.Tests;

/// <summary>
/// 「プラグインは 1 つにつき 1 つの別プロセスにロードされ、1 つが落ちても他に影響しない」ことの確認。
/// ビット数 (32 / 64) を問わず成り立つこと、特に 64bit プラグインが本体と同じプロセスに入らないことを確かめる。
/// </summary>
public class OutOfProcessTests
{
    static RemotePlugin Start(string path)
    {
        var bitness = PeInfo.GetBitness(path);
        return RemotePlugin.Start(TestEnvironment.HostExe(bitness), path, bitness, 48000);
    }

    static bool HasModule(Process process, string fileName)
    {
        foreach (ProcessModule module in process.Modules)
            if (string.Equals(module.ModuleName, fileName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    static void ProcessSomeAudio(IVstEffect fx, float[] left, float[] right)
        => fx.Process(left, right, left.Length);

    // ---- 別プロセスにロードされる ----

    [SkippableFact]
    public void A_64bit_plugin_is_loaded_into_its_own_process_not_this_one()
    {
        Skip.If(TestEnvironment.Vst2x64 == null, "64bit の VST2 (BitShiftGain64.dll) が見つかりません");
        string dll = Path.GetFileName(TestEnvironment.Vst2x64!);
        Assert.True(Environment.Is64BitProcess, "このテストは 64bit プロセスで動かす前提です");

        using var fx = Start(TestEnvironment.Vst2x64!);

        Assert.NotEqual(Environment.ProcessId, fx.ProcessId);
        Assert.False(HasModule(Process.GetCurrentProcess(), dll), "プラグインの DLL がこのプロセスに読み込まれています");
        using var host = Process.GetProcessById(fx.ProcessId);
        Assert.True(HasModule(host, dll), "プラグインの DLL がホストプロセスに読み込まれていません");
        Assert.Equal("VstPluginHost", host.ProcessName);
    }

    [SkippableFact]
    public void A_64bit_vst3_plugin_is_loaded_into_its_own_process_not_this_one()
    {
        Skip.If(TestEnvironment.Vst3x64 == null, "64bit の VST3 (ConvologyXT_64.vst3) が見つかりません");
        string dll = Path.GetFileName(TestEnvironment.Vst3x64!);

        using var fx = Start(TestEnvironment.Vst3x64!);

        Assert.NotEqual(Environment.ProcessId, fx.ProcessId);
        Assert.False(HasModule(Process.GetCurrentProcess(), dll));
        using var host = Process.GetProcessById(fx.ProcessId);
        Assert.True(HasModule(host, dll));
    }

    [SkippableFact]
    public void Each_plugin_gets_its_own_process()
    {
        Skip.If(TestEnvironment.Vst2x64 == null || TestEnvironment.Vst3x64 == null || TestEnvironment.Vst2x86 == null,
            "64bit の VST2 / VST3 と 32bit の VST2 のすべてが必要です");

        using var a = Start(TestEnvironment.Vst2x64!);
        using var b = Start(TestEnvironment.Vst2x64!);   // 同じプラグインを 2 つ
        using var c = Start(TestEnvironment.Vst3x64!);
        using var d = Start(TestEnvironment.Vst2x86!);

        var pids = new[] { a.ProcessId, b.ProcessId, c.ProcessId, d.ProcessId, Environment.ProcessId };
        Assert.Equal(pids.Length, pids.Distinct().Count());
    }

    // ---- 1 つが落ちても他に影響しない ----

    [SkippableTheory]
    [InlineData(PluginBitness.X64)]
    [InlineData(PluginBitness.X86)]
    public void Killing_one_plugins_process_does_not_affect_the_others_or_this_process(PluginBitness bitness)
    {
        string? path = bitness == PluginBitness.X64 ? TestEnvironment.Vst2x64 : TestEnvironment.Vst2x86;
        Skip.If(path == null, "テスト用の VST2 が見つかりません");

        using var victim = Start(path!);
        using var bystander = Start(path!);
        var left = new float[256];
        var right = new float[256];
        bystander.SetParameter(0, 0.5);

        // プラグインのクラッシュ相当: ホストプロセスを強制終了する
        using (var process = Process.GetProcessById(victim.ProcessId)) { process.Kill(); process.WaitForExit(5000); }

        // 終了は即座に検出される (処理のタイムアウト 250ms を待たない)
        var sw = Stopwatch.StartNew();
        Assert.True(SpinWait.SpinUntil(() => victim.IsFaulted, 2000));
        Assert.True(sw.ElapsedMilliseconds < 1000);

        // 落ちたプラグインは音声をそのまま通す。呼び出しが固まらない
        for (int i = 0; i < left.Length; i++) left[i] = right[i] = 0.25f;
        sw.Restart();
        ProcessSomeAudio(victim, left, right);
        Assert.True(sw.ElapsedMilliseconds < 200, $"落ちたプラグインの処理に {sw.ElapsedMilliseconds} ms かかりました");
        Assert.All(left, v => Assert.Equal(0.25f, v));
        Assert.Empty(victim.GetParameters());          // 制御用の呼び出しも例外を出さず空を返す
        Assert.Null(victim.GetState());

        // 巻き込まれていない
        Assert.False(bystander.IsFaulted);
        Assert.Single(bystander.GetParameters());
        for (int i = 0; i < left.Length; i++) left[i] = right[i] = 0.25f;
        ProcessSomeAudio(bystander, left, right);
        Assert.False(bystander.IsFaulted);
        Assert.NotEmpty(Process.GetCurrentProcess().ProcessName);   // このプロセスはもちろん生きている
    }

    [SkippableFact]
    public void A_crash_during_processing_is_detected_without_waiting_for_the_timeout()
    {
        Skip.If(TestEnvironment.Vst2x64 == null, "64bit の VST2 (BitShiftGain64.dll) が見つかりません");

        using var fx = Start(TestEnvironment.Vst2x64!);
        var left = new float[256];
        var right = new float[256];
        ProcessSomeAudio(fx, left, right);   // 一度正常に処理させる (以後のタイムアウトは 250ms)

        using (var process = Process.GetProcessById(fx.ProcessId)) { process.Kill(); process.WaitForExit(5000); }

        var sw = Stopwatch.StartNew();
        ProcessSomeAudio(fx, left, right);   // 終了済みのホストへ処理を依頼する
        sw.Stop();

        Assert.True(fx.IsFaulted);
        Assert.True(sw.ElapsedMilliseconds < 200, $"クラッシュの検出に {sw.ElapsedMilliseconds} ms かかりました (タイムアウト 250ms を待った可能性)");
    }

    [SkippableFact]
    public void Disposing_stops_the_host_process()
    {
        Skip.If(TestEnvironment.Vst2x64 == null, "64bit の VST2 (BitShiftGain64.dll) が見つかりません");

        var fx = Start(TestEnvironment.Vst2x64!);
        int pid = fx.ProcessId;
        using var host = Process.GetProcessById(pid);

        fx.Dispose();

        Assert.True(host.WaitForExit(5000), "Dispose 後もプラグインホストのプロセスが残っています");
    }
}
