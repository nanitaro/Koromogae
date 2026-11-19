using VstCore;
using Xunit;

namespace PluginHost.Tests;

/// <summary>
/// 偽プラグインホストに不正な応答を返させて、ホスト側 (RemotePlugin) が例外を漏らさず、そのプラグインだけを停止扱いにすることを確かめる。
/// 偽プラグインホストの動作モードは Load のパス引数で渡す (FakeHost/Program.cs)。
/// </summary>
public class RemotePluginValidationTests
{
    static RemotePlugin StartFake(string mode)
        => RemotePlugin.Start(TestEnvironment.FakeHostExe, mode, PluginBitness.X86, 48000);

    // ---- 正常な応答は受け入れられる (偽プラグインホストが単に壊れているだけではないことの確認) ----

    [Fact]
    public void Valid_replies_are_accepted()
    {
        using var client = StartFake("ok");

        var parameters = client.GetParameters();
        Assert.Equal(new[] { "Gain", "Mode" }, parameters.Select(p => p.Name));
        Assert.Equal("dB", parameters[0].Unit);
        Assert.Equal(3, parameters[1].StepCount);

        var value = client.GetParameterValue(0);
        Assert.Equal(0.25, value.Normalized);
        Assert.Equal("-6.0 dB", value.Display);

        Assert.Equal(new byte[] { 1, 2, 3 }, client.GetState());

        var programs = client.GetPrograms();
        Assert.Equal(new[] { "First", "Second" }, programs.Names);
        Assert.Equal(1, programs.Current);

        Assert.False(client.IsFaulted);
    }

    // ---- プラグインが申告する遅延 (画面に出す実遅延の合算に使う) ----

    [Fact]
    public void Plugin_latency_is_read_when_the_plugin_is_loaded()
    {
        using var client = StartFake("ok");

        Assert.Equal(128, client.LatencySamples);
        Assert.False(client.IsFaulted);
    }

    [Fact]
    public void Plugin_latency_is_refreshed_by_prepare()
    {
        using var client = StartFake("ok");
        client.Prepare(96000, 4096);

        Assert.Equal(128, client.LatencySamples);
        Assert.False(client.IsFaulted);
    }

    [Theory]
    [InlineData("latency_negative")]
    [InlineData("latency_huge")]
    [InlineData("latency_over_limit")]
    public void Invalid_plugin_latency_faults_the_client(string mode)
    {
        using var client = StartFake(mode);

        Assert.True(client.IsFaulted);
        Assert.Equal(0, client.LatencySamples);
    }
    [Fact]
    public void State_minus_one_means_no_state_and_is_not_an_error()
    {
        using var client = StartFake("state_none");

        Assert.Null(client.GetState());
        Assert.False(client.IsFaulted);
    }

    // ---- 不正な応答: 例外を漏らさず、停止扱いになる ----

    [Theory]
    [InlineData("params_huge")]
    [InlineData("params_over_limit")]
    [InlineData("params_negative")]
    [InlineData("string_huge")]
    public void Invalid_parameter_list_faults_the_client(string mode)
    {
        using var client = StartFake(mode);

        var result = client.GetParameters();

        Assert.Empty(result);
        Assert.True(client.IsFaulted);
    }

    [Theory]
    [InlineData("state_huge")]
    [InlineData("state_over_limit")]
    [InlineData("state_negative")]
    public void Invalid_state_length_faults_the_client(string mode)
    {
        using var client = StartFake(mode);

        Assert.Null(client.GetState());
        Assert.True(client.IsFaulted);
    }

    [Theory]
    [InlineData("programs_huge")]
    [InlineData("programs_current_out_of_range")]
    public void Invalid_program_list_faults_the_client(string mode)
    {
        using var client = StartFake(mode);

        Assert.Empty(client.GetPrograms().Names);
        Assert.True(client.IsFaulted);
    }

    [Fact]
    public void Invalid_display_string_faults_the_client()
    {
        using var client = StartFake("string_huge");

        var value = client.GetParameterValue(0);

        Assert.Null(value.Display);
        Assert.True(client.IsFaulted);
    }

    [Fact]
    public void A_faulted_client_ignores_further_calls()
    {
        using var client = StartFake("params_huge");
        client.GetParameters();
        Assert.True(client.IsFaulted);

        // 同期の取れなくなったストリームには、もう何も送らない
        Assert.Empty(client.GetParameters());
        Assert.Null(client.GetState());
        client.SetParameter(0, 0.5);
        client.SetProgram(1);
        client.OpenEditor();

        // Process は入力をそのまま通す
        var left = new float[] { 0.1f, 0.2f };
        var right = new float[] { 0.3f, 0.4f };
        client.Process(left, right, 2);
        Assert.Equal(new[] { 0.1f, 0.2f }, left);
        Assert.Equal(new[] { 0.3f, 0.4f }, right);
    }
}
