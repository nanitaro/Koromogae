using Koromogae.Audio;
using Xunit;

namespace Audio.Tests;

/// <summary>サンプル形式の相互変換 (開いた形式の表示・デバイスを開くときの候補・録音のビット深度)。</summary>
public class SampleFormatsTests
{
    // ---- 実際に開いた形式 → 画面の指定 ----

    [Theory]
    [InlineData(SampleFormat.Float32, FormatChoice.Float32)]
    [InlineData(SampleFormat.Int32, FormatChoice.Int32)]
    [InlineData(SampleFormat.Int24, FormatChoice.Int24)]
    [InlineData(SampleFormat.Int16, FormatChoice.Int16)]
    public void An_opened_format_maps_to_the_same_named_choice(SampleFormat format, FormatChoice expected)
    {
        Assert.Equal(expected, SampleFormats.ToChoice(format));
    }

    [Fact]
    public void An_undefined_format_has_no_choice()
    {
        Assert.Null(SampleFormats.ToChoice((SampleFormat)99));
    }

    [Fact]
    public void Every_sample_format_round_trips_through_its_choice()
    {
        // 形式を足して、変換を足し忘れると、ここで落ちる
        foreach (var format in Enum.GetValues<SampleFormat>())
        {
            var choice = SampleFormats.ToChoice(format);
            Assert.True(choice.HasValue, $"{format} に対応する指定がありません");
            Assert.Equal(new[] { format }, SampleFormats.Candidates(choice.Value));
        }
    }

    // ---- デバイスを開くときの候補 ----

    [Fact]
    public void Auto_tries_float32_then_int32_then_int24_then_int16()
    {
        Assert.Equal(new[] { SampleFormat.Float32, SampleFormat.Int32, SampleFormat.Int24, SampleFormat.Int16 },
            SampleFormats.Candidates(FormatChoice.Auto));
    }

    [Fact]
    public void Auto_covers_every_sample_format()
    {
        Assert.Equal(Enum.GetValues<SampleFormat>().OrderBy(f => f), SampleFormats.Candidates(FormatChoice.Auto).OrderBy(f => f));
    }

    [Theory]
    [InlineData(FormatChoice.Float32, SampleFormat.Float32)]
    [InlineData(FormatChoice.Int32, SampleFormat.Int32)]
    [InlineData(FormatChoice.Int24, SampleFormat.Int24)]
    [InlineData(FormatChoice.Int16, SampleFormat.Int16)]
    public void A_specified_format_is_the_only_candidate_and_never_falls_back_to_auto(FormatChoice choice, SampleFormat expected)
    {
        Assert.Equal(new[] { expected }, SampleFormats.Candidates(choice));
    }

    [Fact]
    public void The_auto_list_is_a_copy_so_a_caller_cannot_change_the_order_for_everyone()
    {
        var first = SampleFormats.Candidates(FormatChoice.Auto);
        first[0] = SampleFormat.Int16;

        Assert.Equal(SampleFormat.Float32, SampleFormats.Candidates(FormatChoice.Auto)[0]);
    }

    // ---- 録音のビット深度 → 書き出しの形式 ----

    [Theory]
    [InlineData(RecordDepth.Float32, SampleFormat.Float32)]
    [InlineData(RecordDepth.Int24, SampleFormat.Int24)]
    [InlineData(RecordDepth.Int16, SampleFormat.Int16)]
    public void A_record_depth_maps_to_the_matching_write_format(RecordDepth depth, SampleFormat expected)
    {
        Assert.Equal(expected, SampleFormats.ToSampleFormat(depth));
    }

    [Fact]
    public void Every_record_depth_has_a_write_format_that_the_recorder_can_use()
    {
        // WAV は Int32 を書けない (WavWriter が拒否する)。録音のビット深度から Int32 にはならないこと
        foreach (var depth in Enum.GetValues<RecordDepth>())
            Assert.NotEqual(SampleFormat.Int32, SampleFormats.ToSampleFormat(depth));
    }
}