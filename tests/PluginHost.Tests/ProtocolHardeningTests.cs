using System.Diagnostics;
using System.Text;
using VstCore;
using Xunit;

namespace PluginHost.Tests;

/// <summary>
/// 本体とプラグインホストの制御通信の堅牢性: ① 応答待ちに期限があり、Dispose がそれに巻き込まれない
/// ② 1 回の応答で読む文字列の合計に上限がある ③ 不正な数値 (NaN・無限大・範囲外・負のステップ数) を、正常な応答として受け入れない。
/// 異常なホスト (またはプラグイン) が相手でも、本体が固まらない・メモリを使い切らない・不正な値を画面へ流さないための検査。
/// </summary>
public class ProtocolHardeningTests
{
    static RemotePlugin StartFake(string mode)
        => RemotePlugin.Start(TestEnvironment.FakeHostExe, mode, PluginBitness.X86, 48000);

    // ================= ① 応答待ちの期限 =================

    [Fact]
    public void A_host_that_stops_answering_is_cut_off_after_the_call_timeout()
    {
        using var client = StartFake("state_hang");
        client.CallTimeoutMs = 500;
        int pid = client.ProcessId;
        var timer = Stopwatch.StartNew();

        byte[]? state = client.GetState();                  // ホストは応答しない

        Assert.Null(state);
        Assert.True(timer.ElapsedMilliseconds < 8000, $"期限 (0.5 秒) を過ぎても戻りません: {timer.ElapsedMilliseconds} ms");
        Assert.True(client.IsFaulted);                      // 切り離された (以後は素通し)
        Assert.True(WaitForExit(pid, 5000), "応答しないホストが強制終了されていません");
    }

    [Fact]
    public void CloseEditor_waits_for_the_hosts_reply_so_it_does_not_return_at_once_when_the_host_is_stuck()
    {
        // IVstEffect.CloseEditor の契約: プラグインの中の閉じる処理は待たないが、別プロセスのホストへの呼び出しは応答を待つ。
        // 応答しないホストでは、期限まで戻らない。そのため、UI スレッドから呼ぶ側は、バックグラウンドへ出す (MainWindow.CloseAllEditors)
        using var client = StartFake("close_hang");
        client.CallTimeoutMs = 700;
        var timer = Stopwatch.StartNew();

        client.CloseEditor();

        Assert.InRange(timer.ElapsedMilliseconds, 500, 8000);        // 待った (すぐには戻らない) が、期限で切り上げた
        Assert.True(client.IsFaulted);
    }

    [Fact]
    public void Dispose_does_not_wait_for_a_call_that_is_stuck_holding_the_control_lock()
    {
        var client = StartFake("state_hang");
        client.CallTimeoutMs = 120_000;                     // 期限には頼らない (Dispose が自分で抜けられること)
        int pid = client.ProcessId;
        var stuck = Task.Run(() => client.GetState());      // ロックを持ったまま、応答を待ち続ける
        Thread.Sleep(500);
        var timer = Stopwatch.StartNew();

        client.Dispose();                                   // 以前は、同じロックを待って戻らなかった

        Assert.True(timer.ElapsedMilliseconds < 8000, $"Dispose が戻りません: {timer.ElapsedMilliseconds} ms");
        Assert.True(WaitForExit(pid, 5000), "ホストが終了していません");
        Assert.True(stuck.Wait(8000), "止まっていた呼び出しが戻りません");   // ホストの終了で、切断として戻る
    }

    [Fact]
    public void A_call_that_answers_in_time_is_not_cut_off()
    {
        using var client = StartFake("ok");
        client.CallTimeoutMs = 5000;

        Assert.Equal(new byte[] { 1, 2, 3 }, client.GetState());
        Thread.Sleep(300);                                  // 終わった呼び出しの期限が、あとから働かない

        Assert.False(client.IsFaulted);
        Assert.Equal(new byte[] { 1, 2, 3 }, client.GetState());
    }

    // ================= ② 応答の合計サイズ =================

    [Fact]
    public void A_parameter_list_whose_total_size_is_over_the_limit_faults_the_client()
    {
        using var client = StartFake("params_total_over_budget");     // 個数も文字列 1 つの長さも上限内。合計だけが 16 MiB を超える

        var parameters = client.GetParameters();

        Assert.Empty(parameters);
        Assert.True(client.IsFaulted);
    }

    [Fact]
    public void A_program_list_whose_total_size_is_over_the_limit_faults_the_client()
    {
        using var client = StartFake("programs_total_over_budget");

        var programs = client.GetPrograms();

        Assert.Empty(programs.Names);
        Assert.True(client.IsFaulted);
    }

    [Fact]
    public void A_large_but_reasonable_parameter_list_is_accepted()
    {
        using var client = StartFake("params_many_ok");     // 5,000 個・約 0.7 MB

        var parameters = client.GetParameters();

        Assert.Equal(5000, parameters.Length);
        Assert.Equal(new string('p', 100), parameters[0].Name);
        Assert.Equal(1.0, parameters[1].DefaultValue);
        Assert.False(client.IsFaulted);
    }

    [Fact]
    public void The_response_budget_is_checked_before_memory_is_allocated()
    {
        // 長さの見出しだけで中身が無くても、予算を超えるなら、読む前に拒否する (中身を確保してから拒否しない)
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) w.Write7BitEncodedInt(60_000);
        ms.Position = 0;

        var ex = Assert.Throws<HostProtocolException>(() =>
            HostProtocol.ReadString(new BinaryReader(ms), "テスト", new ResponseBudget(1000)));
        Assert.Contains("上限", ex.Message);
    }

    [Fact]
    public void The_budget_counts_every_string_and_refuses_the_one_that_does_not_fit()
    {
        var budget = new ResponseBudget(100);

        budget.Take(40, "a");
        budget.Take(60, "b");
        Assert.Equal(0, budget.Remaining);
        Assert.Throws<HostProtocolException>(() => budget.Take(1, "c"));

        var sender = new ResponseBudget(100);
        Assert.True(sender.TryTake(70));
        Assert.False(sender.TryTake(31));                   // 収まらないものは、何も使わない
        Assert.True(sender.TryTake(30));
    }

    [Fact]
    public void Clipped_strings_always_fit_the_receivers_limit_even_with_three_byte_characters()
    {
        string wide = new('あ', HostProtocol.MaxStringBytes * 2);     // UTF-8 で 1 文字 3 バイト

        string clipped = HostProtocol.Clip(wide);

        Assert.True(Encoding.UTF8.GetByteCount(clipped) <= HostProtocol.MaxStringBytes);
        Assert.Equal("", HostProtocol.Clip(null));
        Assert.Equal("abc", HostProtocol.Clip("abc"));
    }

    // ================= ③ 数値の検証 =================

    [Theory]
    [InlineData("param_default_nan")]
    [InlineData("param_default_infinity")]
    [InlineData("param_default_out_of_range")]
    [InlineData("param_default_negative")]
    [InlineData("param_steps_negative")]
    [InlineData("param_steps_over_limit")]
    public void An_invalid_parameter_number_faults_the_client(string mode)
    {
        using var client = StartFake(mode);

        var parameters = client.GetParameters();

        Assert.Empty(parameters);                           // 不正な値を、画面へ流さない
        Assert.True(client.IsFaulted);
    }

    [Theory]
    [InlineData("value_nan")]
    [InlineData("value_infinity")]
    [InlineData("value_out_of_range")]
    [InlineData("value_negative")]
    public void An_invalid_parameter_value_faults_the_client(string mode)
    {
        using var client = StartFake(mode);

        var value = client.GetParameterValue(0);

        Assert.Equal(default, value);
        Assert.True(client.IsFaulted);
    }

    [Fact]
    public void The_boundary_values_zero_and_one_are_accepted()
    {
        Assert.Equal(0.0, HostProtocol.CheckUnit(0.0, "x"));
        Assert.Equal(1.0, HostProtocol.CheckUnit(1.0, "x"));
        Assert.Throws<HostProtocolException>(() => HostProtocol.CheckUnit(double.NaN, "x"));
        Assert.Throws<HostProtocolException>(() => HostProtocol.CheckUnit(double.NegativeInfinity, "x"));
        Assert.Throws<HostProtocolException>(() => HostProtocol.CheckUnit(1.0000001, "x"));
        Assert.Throws<HostProtocolException>(() => HostProtocol.CheckUnit(-0.0000001, "x"));
    }

    [Fact]
    public void The_host_rounds_what_a_plugin_reports_into_range_before_sending()
    {
        // 実プラグインが返す、わずかなはみ出し (1.0000001 など) で、動いているプラグインを止めないための丸め
        Assert.Equal(1.0, HostProtocol.SanitizeUnit(1.0000001));
        Assert.Equal(0.0, HostProtocol.SanitizeUnit(-0.5));
        Assert.Equal(0.0, HostProtocol.SanitizeUnit(double.NaN));
        Assert.Equal(0.0, HostProtocol.SanitizeUnit(double.PositiveInfinity));
        Assert.Equal(0.25, HostProtocol.SanitizeUnit(0.25));

        Assert.Equal(0, HostProtocol.SanitizeStepCount(-1));
        Assert.Equal(HostProtocol.MaxStepCount, HostProtocol.SanitizeStepCount(int.MaxValue));
        Assert.Equal(127, HostProtocol.SanitizeStepCount(127));
    }

    // ================= 実プラグインで、誤って停止させないこと =================

    [SkippableFact]
    public void Real_plugins_report_values_that_pass_the_stricter_checks()
    {
        Skip.If(TestEnvironment.Vst2x64 == null && TestEnvironment.Vst3x64 == null, "手元に 64bit のプラグインがありません");
        foreach (string? path in new[] { TestEnvironment.Vst2x64, TestEnvironment.Vst3x64 })
        {
            if (path == null) continue;
            var bitness = PeInfo.GetBitness(path);
            using var client = RemotePlugin.Start(TestEnvironment.HostExe(bitness), path, bitness, 48000);

            var parameters = client.GetParameters();
            foreach (var p in parameters.Take(50))
            {
                var value = client.GetParameterValue(p.Index);
                Assert.InRange(value.Normalized, 0.0, 1.0);
                Assert.InRange(p.DefaultValue, 0.0, 1.0);
                Assert.True(p.StepCount >= 0);
            }

            Assert.False(client.IsFaulted, $"検証が厳しくなって、実プラグインが停止扱いになりました: {path}");
        }
    }

    // ---- ヘルパー ----

    static bool WaitForExit(int pid, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds)
        {
            try { using var p = Process.GetProcessById(pid); if (p.HasExited) return true; }
            catch (ArgumentException) { return true; }       // もう無い
            Thread.Sleep(50);
        }
        return false;
    }
}