using VstCore;
using Xunit;

namespace PluginHost.Tests;

/// <summary>
/// 実際のプラグインを、本物のプラグインホストプロセス経由で動かし、パラメータ・状態・プリセット・音声処理が使えることを確かめる。
/// 32bit / 64bit の両方を確認する。プラグインが見つからない環境ではスキップされる。
/// </summary>
public class HostRegressionTests
{
    static RemotePlugin StartReal(string path)
    {
        var bitness = PeInfo.GetBitness(path);
        return RemotePlugin.Start(TestEnvironment.HostExe(bitness), path, bitness, 48000);
    }

    static float Peak(IVstEffect effect, float amplitude)
    {
        var left = new float[512];
        var right = new float[512];
        float peak = 0;
        for (int block = 0; block < 10; block++)
        {
            for (int i = 0; i < left.Length; i++) left[i] = right[i] = amplitude * MathF.Sin(i * 0.1f);
            effect.Process(left, right, left.Length);
        }
        foreach (float v in left) peak = MathF.Max(peak, MathF.Abs(v));
        return peak;
    }

    static void AssertBitShiftGainWorks(string path, PluginBitness expected)
    {
        using var fx = StartReal(path);
        Assert.Equal(expected, fx.Bitness);

        var parameters = fx.GetParameters();
        Assert.Single(parameters);
        Assert.Equal("BitShift", parameters[0].Name);

        // 0.5 = 0 bit (変化なし)、1.0 = +16 bit (大幅に増幅)
        fx.SetParameter(0, 0.5);
        Assert.InRange(Peak(fx, 0.1f), 0.09f, 0.11f);
        fx.SetParameter(0, 1.0);
        Assert.True(Peak(fx, 0.1f) > 1000f);

        var state = fx.GetState();
        Assert.NotNull(state);
        fx.SetState(state!);

        Assert.False(fx.IsFaulted);
    }

    [SkippableFact]
    public void Vst2_32bit_parameters_state_and_processing_work()
    {
        Skip.If(TestEnvironment.Vst2x86 == null, "32bit の VST2 (BitShiftGain32.dll) が見つかりません");
        AssertBitShiftGainWorks(TestEnvironment.Vst2x86!, PluginBitness.X86);
    }

    [SkippableFact]
    public void Vst2_64bit_parameters_state_and_processing_work()
    {
        Skip.If(TestEnvironment.Vst2x64 == null, "64bit の VST2 (BitShiftGain64.dll) が見つかりません");
        AssertBitShiftGainWorks(TestEnvironment.Vst2x64!, PluginBitness.X64);
    }

    [SkippableFact]
    public void Vst2_programs_work()
    {
        Skip.If(TestEnvironment.Vst2x86WithPresets == null, "プリセットを持つ 32bit の VST2 (Final Master.dll) が見つかりません");
        using var fx = StartReal(TestEnvironment.Vst2x86WithPresets!);

        var programs = fx.GetPrograms();
        Assert.True(programs.Names.Length >= 2);

        string Snapshot() => string.Join(",", fx.GetParameters().Take(8).Select(p => fx.GetParameterValue(p.Index).Display));
        string before = Snapshot();
        fx.SetProgram(3);
        Assert.Equal(3, fx.GetPrograms().Current);
        Assert.NotEqual(before, Snapshot());

        Assert.False(fx.IsFaulted);
    }

    static void AssertVst3Works(string path, PluginBitness expected)
    {
        using var fx = StartReal(path);
        Assert.Equal(expected, fx.Bitness);

        var parameters = fx.GetParameters();
        Assert.True(parameters.Length > 1);

        fx.SetParameter(1, 0.7);
        Assert.Equal(0.7, fx.GetParameterValue(1).Normalized, 6);

        var state = fx.GetState();
        Assert.NotNull(state);
        Assert.NotEmpty(state!);
        fx.SetState(state!);

        Assert.False(fx.IsFaulted);
    }

    [SkippableFact]
    public void Vst3_32bit_parameters_and_state_work()
    {
        Skip.If(TestEnvironment.Vst3x86 == null, "32bit の VST3 (ConvologyXT.vst3) が見つかりません");
        AssertVst3Works(TestEnvironment.Vst3x86!, PluginBitness.X86);
    }

    [SkippableFact]
    public void Vst3_64bit_parameters_and_state_work()
    {
        Skip.If(TestEnvironment.Vst3x64 == null, "64bit の VST3 (ConvologyXT_64.vst3) が見つかりません");
        AssertVst3Works(TestEnvironment.Vst3x64!, PluginBitness.X64);
    }
}
