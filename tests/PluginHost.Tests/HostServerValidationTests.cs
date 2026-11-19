using VstCore;
using Xunit;

namespace PluginHost.Tests;

/// <summary>
/// 本物のプラグインホスト (サーバー側) に、生のパイプから不正な値を送って、検証が働くことを確かめる。
/// 32bit / 64bit のどちらのホストでも同じ動作になることを確認する。
/// 検証は引数を使う前に行われるので、プラグインをロードしなくても確かめられる。
/// </summary>
public class HostServerValidationTests
{
    // ワイヤ上のコマンド番号 (HostProtocol.Cmd*)。本体側が変わったら、ここが落ちて気付ける。
    const int CmdLoad = 1, CmdPrepare = 2, CmdSetState = 5, CmdExit = 6, CmdGetParameterValue = 8, CmdSetParameter = 9, CmdSetProgram = 11;

    const int ViolationExitCode = 3;

    static readonly Dictionary<string, Action<BinaryWriter>> Violations = new()
    {
        ["unknown_command"] = w => w.Write(999),
        ["unknown_command_negative"] = w => w.Write(-1),
        ["set_state_length_negative"] = w => { w.Write(CmdSetState); w.Write(-1); },
        ["set_state_length_int_max"] = w => { w.Write(CmdSetState); w.Write(int.MaxValue); },
        ["set_state_length_over_limit"] = w => { w.Write(CmdSetState); w.Write(64 * 1024 * 1024 + 1); },
        ["load_sample_rate_zero"] = w => { w.Write(CmdLoad); w.Write("x.dll"); w.Write(0); },
        ["load_sample_rate_too_high"] = w => { w.Write(CmdLoad); w.Write("x.dll"); w.Write(10_000_000); },
        ["load_path_length_int_max"] = w => { w.Write(CmdLoad); w.Write7BitEncodedInt(int.MaxValue); },
        ["load_path_length_over_limit"] = w => { w.Write(CmdLoad); w.Write7BitEncodedInt(64 * 1024 + 1); },
        ["prepare_block_size_zero"] = w => { w.Write(CmdPrepare); w.Write(48000); w.Write(0); },
        ["prepare_block_size_too_large"] = w => { w.Write(CmdPrepare); w.Write(48000); w.Write(4097); },
        ["get_parameter_value_index_negative"] = w => { w.Write(CmdGetParameterValue); w.Write(-1); },
        ["set_parameter_index_int_max"] = w => { w.Write(CmdSetParameter); w.Write(int.MaxValue); w.Write(0.5); },
        ["set_parameter_nan"] = w => { w.Write(CmdSetParameter); w.Write(0); w.Write(double.NaN); },
        ["set_parameter_infinity"] = w => { w.Write(CmdSetParameter); w.Write(0); w.Write(double.PositiveInfinity); },
        ["set_program_index_negative"] = w => { w.Write(CmdSetProgram); w.Write(-5); },
    };

    static readonly PluginBitness[] HostBitnesses = { PluginBitness.X86, PluginBitness.X64 };

    public static TheoryData<string, PluginBitness> AllViolations()
    {
        var data = new TheoryData<string, PluginBitness>();
        foreach (var bits in HostBitnesses)
            foreach (string name in Violations.Keys)
                data.Add(name, bits);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllViolations))]
    public void Invalid_input_terminates_the_host_with_exit_code_3(string name, PluginBitness hostBitness)
    {
        using var session = new RawHostSession(hostBitness);
        session.Send(Violations[name]);

        Assert.Equal(ViolationExitCode, session.WaitForExit());
    }

    [Theory]
    [InlineData("set_state_length_int_max", PluginBitness.X86)]
    [InlineData("set_state_length_over_limit", PluginBitness.X86)]
    [InlineData("load_path_length_int_max", PluginBitness.X86)]
    [InlineData("set_state_length_int_max", PluginBitness.X64)]
    [InlineData("set_state_length_over_limit", PluginBitness.X64)]
    [InlineData("load_path_length_int_max", PluginBitness.X64)]
    public void Oversized_lengths_are_rejected_before_allocating(string name, PluginBitness hostBitness)
    {
        using var session = new RawHostSession(hostBitness);
        session.Send(Violations[name]);
        session.WaitForExit();

        // 空のホストは 25〜70MB 程度。2GB 級の確保を試みていれば、これを大きく超える
        Assert.InRange(session.PeakWorkingSet, 1, 250L * 1024 * 1024);
    }

    [Theory]
    [InlineData(PluginBitness.X86)]
    [InlineData(PluginBitness.X64)]
    public void Peer_disconnecting_mid_message_is_a_normal_exit(PluginBitness hostBitness)
    {
        using var session = new RawHostSession(hostBitness);
        session.Send(w => { w.Write(CmdSetState); w.Write(1000); w.Write(new byte[10]); });
        session.ClosePipe();

        Assert.Equal(0, session.WaitForExit());
    }

    [Theory]
    [InlineData(PluginBitness.X86)]
    [InlineData(PluginBitness.X64)]
    public void Exit_command_is_a_normal_exit(PluginBitness hostBitness)
    {
        using var session = new RawHostSession(hostBitness);
        session.Send(w => w.Write(CmdExit));

        Assert.Equal(0, session.WaitForExit());
    }

    [SkippableFact]
    public void Loading_twice_is_rejected()
    {
        Skip.If(TestEnvironment.Vst2x86 == null, "32bit の VST2 (BitShiftGain32.dll) が見つかりません");

        using var session = new RawHostSession();
        Assert.True(session.Load(TestEnvironment.Vst2x86!));
        session.Send(w => { w.Write(CmdLoad); w.Write(TestEnvironment.Vst2x86!); w.Write(48000); });

        Assert.Equal(ViolationExitCode, session.WaitForExit());
    }

    [SkippableFact]
    public void Loading_twice_is_rejected_by_the_64bit_host()
    {
        Skip.If(TestEnvironment.Vst2x64 == null, "64bit の VST2 (BitShiftGain64.dll) が見つかりません");

        using var session = new RawHostSession(PluginBitness.X64);
        Assert.True(session.Load(TestEnvironment.Vst2x64!));
        session.Send(w => { w.Write(CmdLoad); w.Write(TestEnvironment.Vst2x64!); w.Write(48000); });

        Assert.Equal(ViolationExitCode, session.WaitForExit());
    }
}
