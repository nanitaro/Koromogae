using System.Diagnostics;
using VstCore;
using Xunit;

namespace PluginHost.Tests;

/// <summary>
/// プラグインホスト終了時に、処理中の音声スレッドを残したままプラグインを破棄しないための判断ロジックのテスト。
/// 実際に「1 秒以上止まるプラグイン」を用意するのは難しいので、止まったスレッドを偽物で作って確かめる。
/// </summary>
public class HostShutdownTests
{
    [Fact]
    public void No_audio_thread_counts_as_stopped()
    {
        Assert.True(HostShutdown.WaitForAudioThread(null));
    }

    [Fact]
    public void A_finished_thread_counts_as_stopped()
    {
        var thread = new Thread(() => { });
        thread.Start();

        Assert.True(HostShutdown.WaitForAudioThread(thread, timeoutMs: 5000));
    }

    [Fact]
    public void A_thread_that_stops_within_the_timeout_counts_as_stopped()
    {
        using var release = new ManualResetEventSlim();
        var thread = new Thread(() => release.Wait()) { IsBackground = true };
        thread.Start();
        new Thread(() => { Thread.Sleep(100); release.Set(); }) { IsBackground = true }.Start();

        Assert.True(HostShutdown.WaitForAudioThread(thread, timeoutMs: 5000));
    }

    [Fact]
    public void A_thread_stuck_inside_the_plugin_is_reported_as_not_stopped_after_the_timeout()
    {
        using var never = new ManualResetEventSlim(); // プラグインの中で止まっている状態の代わり
        var stuck = new Thread(() => never.Wait()) { IsBackground = true };
        stuck.Start();

        var sw = Stopwatch.StartNew();
        bool stopped = HostShutdown.WaitForAudioThread(stuck, timeoutMs: 150);
        sw.Stop();

        Assert.False(stopped);
        Assert.InRange(sw.ElapsedMilliseconds, 100, 5000); // 無限には待たず、指定時間だけ待つ
        Assert.True(stuck.IsAlive);

        never.Set(); // テスト後始末
        stuck.Join(2000);
    }

    [Fact]
    public void The_default_timeout_is_one_second()
    {
        Assert.Equal(1000, HostShutdown.AudioThreadTimeoutMs);
    }

    [Theory]
    [InlineData(0, true, 0)]                                      // 正常終了
    [InlineData(3, true, 3)]                                      // プロトコル違反はそのまま
    [InlineData(0, false, HostShutdown.ExitAudioThreadStuck)]   // 止まらなかったことを伝える
    [InlineData(3, false, 3)]                                     // 先にあった理由 (違反) を優先する
    public void Exit_code_reflects_the_reason(int current, bool audioStopped, int expected)
    {
        Assert.Equal(expected, HostShutdown.ResolveExitCode(current, audioStopped));
    }

    [Fact]
    public void The_stuck_exit_code_does_not_collide_with_other_codes()
    {
        // 0 = 正常、2 = 未処理の例外 (Program.cs)、3 = プロトコル違反 (PluginHostServer)
        Assert.DoesNotContain(HostShutdown.ExitAudioThreadStuck, new[] { 0, 1, 2, 3 });
    }

    // ---- 本物のプラグインホスト: 通常の終了経路が従来どおりである (後始末が正しく行われ、4 にならない) ----

    [SkippableFact]
    public void Normal_shutdown_with_a_loaded_plugin_still_exits_with_zero()
    {
        Skip.If(TestEnvironment.Vst2x86 == null, "32bit の VST2 (BitShiftGain32.dll) が見つかりません");

        using var session = new RawHostSession();
        Assert.True(session.Load(TestEnvironment.Vst2x86!));
        session.Send(w => w.Write(6)); // CmdExit

        Assert.Equal(0, session.WaitForExit());
    }

    [SkippableFact]
    public void Disconnecting_with_a_loaded_plugin_exits_with_zero()
    {
        Skip.If(TestEnvironment.Vst3x86 == null, "32bit の VST3 (ConvologyXT.vst3) が見つかりません");

        using var session = new RawHostSession();
        Assert.True(session.Load(TestEnvironment.Vst3x86!));
        session.ClosePipe(); // ホストが突然いなくなった

        Assert.Equal(0, session.WaitForExit());
    }

    [SkippableFact]
    public void Normal_shutdown_with_a_loaded_64bit_plugin_still_exits_with_zero()
    {
        Skip.If(TestEnvironment.Vst2x64 == null, "64bit の VST2 (BitShiftGain64.dll) が見つかりません");

        using var session = new RawHostSession(PluginBitness.X64);
        Assert.True(session.Load(TestEnvironment.Vst2x64!));
        session.Send(w => w.Write(6)); // CmdExit

        Assert.Equal(0, session.WaitForExit());
    }

    [SkippableFact]
    public void Disconnecting_with_a_loaded_64bit_vst3_plugin_exits_with_zero()
    {
        Skip.If(TestEnvironment.Vst3x64 == null, "64bit の VST3 (ConvologyXT_64.vst3) が見つかりません");

        using var session = new RawHostSession(PluginBitness.X64);
        Assert.True(session.Load(TestEnvironment.Vst3x64!));
        session.ClosePipe();

        Assert.Equal(0, session.WaitForExit());
    }
}