using Koromogae.Ui;
using Xunit;

namespace Ui.Tests;

/// <summary>プラグイン一覧の 1 行の文言 (名前・VST2/VST3・32bit/64bit・ホスト停止)。</summary>
public class PluginLabelTests
{
    [Theory]
    [InlineData(false, true, "Final Master  [VST2 / 32bit]")]
    [InlineData(false, false, "Final Master  [VST2 / 64bit]")]
    [InlineData(true, true, "Final Master  [VST3 / 32bit]")]
    [InlineData(true, false, "Final Master  [VST3 / 64bit]")]
    public void Shows_the_plugin_kind_and_the_bitness(bool isVst3, bool is32bit, string expected)
    {
        Assert.Equal(expected, PluginLabel.Format("Final Master", isVst3, is32bit, hostStopped: false));
    }

    [Fact]
    public void A_stopped_host_is_marked_at_the_end()
    {
        Assert.Equal("Signalizer  [VST2 / 64bit]  ※プラグインホスト停止",
            PluginLabel.Format("Signalizer", isVst3: false, is32bit: false, hostStopped: true));
    }

    [Fact]
    public void The_name_is_shown_as_it_is()
    {
        // 名前に括弧や記号があっても、そのまま出す (種類とビット数は、名前の後ろの [ ] だけ)
        Assert.Equal("EQ [mono] (v2)  [VST3 / 64bit]", PluginLabel.Format("EQ [mono] (v2)", true, false, false));
    }
}