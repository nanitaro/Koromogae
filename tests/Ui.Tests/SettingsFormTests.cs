using System.Reflection;
using Koromogae;
using Koromogae.Audio;
using Koromogae.Ui;
using Xunit;

namespace Ui.Tests;

/// <summary>画面の設定項目 ⇄ 設定ファイル (AppSettings) の対応と、ディザ方式の有効・無効。</summary>
public class SettingsFormTests
{
    /// <summary>画面の設定項目でないもの (プラグインは別の仕組みで保存する)。</summary>
    static readonly string[] NotFormItems = { nameof(AppSettings.Plugins) };

    static IEnumerable<PropertyInfo> SettingsProperties()
        => typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true } && !NotFormItems.Contains(p.Name));

    /// <summary>既定値と違う値を、型に応じて作る (値の妥当性は問わない。対応を見るだけ)。</summary>
    static object DifferentFrom(PropertyInfo p, object? current)
    {
        var type = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        if (type.IsEnum) return Enum.GetValues(type).Cast<object>().First(v => !v.Equals(current));
        if (type == typeof(int)) return (int)(current ?? 0) + 7;
        if (type == typeof(bool)) return !(bool)(current ?? false);
        if (type == typeof(string)) return "x-" + p.Name;
        throw new NotSupportedException($"{p.Name} ({type.Name}): 型に応じた値をこのテストに足してください");
    }

    // ---- 対応 ----

    [Fact]
    public void Every_settings_item_survives_the_round_trip_through_the_form()
    {
        // AppSettings に項目を足して、SettingsForm に足し忘れると、その項目が既定値に戻ってここで落ちる
        var original = new AppSettings();
        foreach (var p in SettingsProperties()) p.SetValue(original, DifferentFrom(p, p.GetValue(original)));

        var restored = new AppSettings();
        SettingsForm.From(original).ApplyTo(restored);

        foreach (var p in SettingsProperties())
            Assert.True(Equals(p.GetValue(original), p.GetValue(restored)), $"{p.Name} が画面の値を経由すると変わります (SettingsForm に足し忘れ?)");
    }

    [Fact]
    public void The_round_trip_test_really_covers_every_item()
    {
        // 上のテストが空振りしないこと: 項目数が減っていない・全項目が既定値と違う値を持てる
        var names = SettingsProperties().Select(p => p.Name).ToList();

        Assert.True(names.Count >= 9, string.Join(", ", names));
        Assert.Contains(nameof(AppSettings.DitherMode), names);
        Assert.Contains(nameof(AppSettings.CushionExtraMs), names);
        Assert.Contains(nameof(AppSettings.InputFormat), names);
        Assert.Contains(nameof(AppSettings.OutputFormat), names);
    }

    [Fact]
    public void The_form_carries_each_value_to_the_right_place()
    {
        // 入れ替わり (入力と出力など) を検出するため、似た項目に別の値を入れる
        var s = new AppSettings
        {
            Api = ApiKind.DirectSound,
            InputDevice = "録音A",
            OutputDevice = "再生B",
            SampleRate = 96000,
            LatencyMs = 50,
            CushionExtraMs = 30,
            InputFormat = FormatChoice.Int16,
            OutputFormat = FormatChoice.Int24,
            DitherMode = DitherMode.HpTpdf,
        };

        var form = SettingsForm.From(s);

        Assert.Equal(ApiKind.DirectSound, form.Api);
        Assert.Equal("録音A", form.InputDevice);
        Assert.Equal("再生B", form.OutputDevice);
        Assert.Equal(96000, form.SampleRate);
        Assert.Equal(50, form.LatencyMs);
        Assert.Equal(30, form.CushionExtraMs);
        Assert.Equal(FormatChoice.Int16, form.InputFormat);
        Assert.Equal(FormatChoice.Int24, form.OutputFormat);
        Assert.Equal(DitherMode.HpTpdf, form.DitherMode);
    }

    [Fact]
    public void Applying_the_form_writes_each_value_to_the_right_item()
    {
        // 入力と出力の入れ替わりなどを検出するため、似た項目に別の値を入れる
        var form = new SettingsForm(ApiKind.Mme, "録音A", "再生B", 96000, 50, 30,
            FormatChoice.Int16, FormatChoice.Int24, DitherMode.HpTpdf);
        var s = new AppSettings();

        form.ApplyTo(s);

        Assert.Equal(ApiKind.Mme, s.Api);
        Assert.Equal("録音A", s.InputDevice);
        Assert.Equal("再生B", s.OutputDevice);
        Assert.Equal(96000, s.SampleRate);
        Assert.Equal(50, s.LatencyMs);
        Assert.Equal(30, s.CushionExtraMs);
        Assert.Equal(FormatChoice.Int16, s.InputFormat);
        Assert.Equal(FormatChoice.Int24, s.OutputFormat);
        Assert.Equal(DitherMode.HpTpdf, s.DitherMode);
    }

    [Fact]
    public void Applying_the_form_leaves_the_plugins_alone()
    {
        var target = new AppSettings();
        target.Plugins.Add(new PluginSetting { Path = @"C:\vst\a.dll", Enabled = false, State = "AAAA", FailCount = 2 });

        SettingsForm.From(new AppSettings { SampleRate = 96000 }).ApplyTo(target);

        Assert.Equal(96000, target.SampleRate);
        var plugin = Assert.Single(target.Plugins);
        Assert.Equal(@"C:\vst\a.dll", plugin.Path);
        Assert.Equal("AAAA", plugin.State);
        Assert.Equal(2, plugin.FailCount);
    }

    [Fact]
    public void A_default_settings_object_round_trips_to_itself()
    {
        var restored = new AppSettings { SampleRate = 192000 };   // 既定でない値を、上書きで確かめる
        SettingsForm.From(new AppSettings()).ApplyTo(restored);

        Assert.Equal(AppSettings.DefaultSampleRate, restored.SampleRate);
        Assert.Equal(AppSettings.DefaultLatencyMs, restored.LatencyMs);
        Assert.Equal(DitherMode.Tpdf, restored.DitherMode);
    }

    // ---- ディザ方式の有効・無効 ----

    [Theory]
    [InlineData(FormatChoice.Auto, true)]      // 開くまで形式が分からない
    [InlineData(FormatChoice.Float32, false)]
    [InlineData(FormatChoice.Int32, false)]    // 32bit 整数は丸めのみ
    [InlineData(FormatChoice.Int24, true)]
    [InlineData(FormatChoice.Int16, true)]
    public void Dither_can_be_chosen_only_for_formats_where_it_works(FormatChoice format, bool expected)
    {
        Assert.Equal(expected, SettingsForm.DitherApplies(format));
        // 録音が Float32 (ディザが働かない) のときは、再生の形式だけで決まる
        Assert.Equal(expected, SettingsForm.DitherEnabled(running: false, format, RecordDepth.Float32));
    }

    [Theory]
    [InlineData(FormatChoice.Auto)]
    [InlineData(FormatChoice.Float32)]
    [InlineData(FormatChoice.Int32)]
    [InlineData(FormatChoice.Int24)]
    [InlineData(FormatChoice.Int16)]
    public void Dither_cannot_be_changed_while_running(FormatChoice format)
    {
        foreach (var depth in Enum.GetValues<RecordDepth>())
            Assert.False(SettingsForm.DitherEnabled(running: true, format, depth));
    }

    [Theory]
    [InlineData(FormatChoice.Float32, RecordDepth.Float32, false)]   // 再生も録音もディザが働かない: 無効
    [InlineData(FormatChoice.Int32, RecordDepth.Float32, false)]
    [InlineData(FormatChoice.Float32, RecordDepth.Int24, true)]      // 録音で働く: 有効 (録音は動作を始めた後に押すので、事前に選べる必要がある)
    [InlineData(FormatChoice.Int32, RecordDepth.Int16, true)]
    [InlineData(FormatChoice.Int16, RecordDepth.Float32, true)]      // 再生で働く: 有効
    [InlineData(FormatChoice.Int24, RecordDepth.Int24, true)]
    [InlineData(FormatChoice.Auto, RecordDepth.Float32, true)]       // 自動は開くまで不明: 有効
    [InlineData(FormatChoice.Auto, RecordDepth.Int16, true)]
    public void Dither_is_disabled_only_when_neither_playback_nor_recording_uses_it(FormatChoice playback, RecordDepth record, bool expected)
    {
        Assert.Equal(expected, SettingsForm.DitherEnabled(running: false, playback, record));
    }

    [Theory]
    [InlineData(RecordDepth.Int24, true)]
    [InlineData(RecordDepth.Int16, true)]
    [InlineData(RecordDepth.Float32, false)]
    public void Recording_dither_applies_to_Int24_and_Int16_only(RecordDepth depth, bool expected)
    {
        Assert.Equal(expected, SettingsForm.RecordDitherApplies(depth));
    }

    // ---- エンジンへ渡す設定 ----

    [Fact]
    public void ToEngineOptions_copies_every_field_without_swapping_input_and_output()
    {
        // 入力と出力のサンプル形式は同じ型で隣り合うので、別の値を入れて、取り違えていないことを確かめる
        var form = new SettingsForm(ApiKind.DirectSound, "録音A", "再生B", 96000, 50, 30,
            FormatChoice.Int16, FormatChoice.Int24, DitherMode.Shaped);

        var options = form.ToEngineOptions();

        Assert.Equal(ApiKind.DirectSound, options.Api);
        Assert.Equal(96000, options.SampleRate);
        Assert.Equal(50, options.LatencyMs);
        Assert.Equal(30, options.CushionExtraMs);
        Assert.Equal(FormatChoice.Int16, options.InputFormat);
        Assert.Equal(FormatChoice.Int24, options.OutputFormat);
        Assert.Equal(DitherMode.Shaped, options.DitherMode);
    }

    [Fact]
    public void ToEngineOptions_of_the_defaults_matches_the_engine_options_defaults()
    {
        var fromSettings = SettingsForm.From(new AppSettings()).ToEngineOptions();
        var defaults = new EngineOptions(AppSettings.DefaultApi, AppSettings.DefaultSampleRate, AppSettings.DefaultLatencyMs);

        Assert.Equal(defaults, fromSettings);     // 既定値が二重に書かれて、食い違っていないこと
    }

    // ---- 録音の形式 (WAV / FLAC) ----

    [Theory]
    [InlineData(RecordFormat.Wav, RecordDepth.Float32, true)]
    [InlineData(RecordFormat.Wav, RecordDepth.Int24, true)]
    [InlineData(RecordFormat.Wav, RecordDepth.Int16, true)]
    [InlineData(RecordFormat.Flac, RecordDepth.Float32, false)]     // FLAC は float を保存できない
    [InlineData(RecordFormat.Flac, RecordDepth.Int24, true)]
    [InlineData(RecordFormat.Flac, RecordDepth.Int16, true)]
    public void Float32_is_the_only_depth_flac_cannot_store(RecordFormat format, RecordDepth depth, bool allowed)
    {
        Assert.Equal(allowed, RecordFormatRules.AllowsDepth(format, depth));
    }

    [Theory]
    [InlineData(RecordFormat.Flac, RecordDepth.Float32, RecordDepth.Int24)]      // FLAC に切り替えたら、Float32 は Int24 になる
    [InlineData(RecordFormat.Flac, RecordDepth.Int16, RecordDepth.Int16)]
    [InlineData(RecordFormat.Flac, RecordDepth.Int24, RecordDepth.Int24)]
    [InlineData(RecordFormat.Wav, RecordDepth.Float32, RecordDepth.Float32)]
    [InlineData(RecordFormat.Wav, RecordDepth.Int16, RecordDepth.Int16)]
    public void Switching_to_flac_turns_float32_into_int24_and_keeps_everything_else(RecordFormat format, RecordDepth depth, RecordDepth expected)
    {
        Assert.Equal(expected, RecordFormatRules.CoerceDepth(format, depth));
    }

    [Fact]
    public void Dither_stays_selectable_with_flac_because_flac_is_always_integer()
    {
        // FLAC は整数だけなので、録音でディザが必ず働く。再生が Float32 でも、方式を選べる
        Assert.True(SettingsForm.DitherEnabled(running: false, FormatChoice.Float32, RecordFormatRules.CoerceDepth(RecordFormat.Flac, RecordDepth.Float32)));
    }

    // ---- 録音の保存先の入力 ----

    const string Default = @"C:\Users\someone\Music\Koromogae";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\Users\someone\Music\Koromogae")]
    [InlineData(@"C:\Users\someone\Music\Koromogae\")]
    [InlineData(@"c:\users\SOMEONE\music\koromogae")]
    [InlineData("  \"C:\\Users\\someone\\Music\\Koromogae\"  ")]
    public void A_blank_or_default_folder_is_stored_as_null_so_it_follows_the_default(string? text)
    {
        Assert.Null(SettingsForm.NormalizeRecordFolder(text, Default));
    }

    [Theory]
    [InlineData(@"D:\Recordings", @"D:\Recordings")]
    [InlineData(@"  D:\Recordings  ", @"D:\Recordings")]
    [InlineData("\"D:\\My Recordings\"", @"D:\My Recordings")]       // エクスプローラーの「パスのコピー」は引用符つき
    [InlineData(@"\\server\share\rec", @"\\server\share\rec")]
    public void Another_folder_is_kept_after_trimming_pasted_spaces_and_quotes(string text, string expected)
    {
        Assert.Equal(expected, SettingsForm.NormalizeRecordFolder(text, Default));
    }

    [Fact]
    public void Every_format_choice_has_a_defined_dither_rule()
    {
        // FormatChoice に選択肢を足したとき、ディザの有効・無効の判断を忘れないように、全値を通す
        foreach (var f in Enum.GetValues<FormatChoice>())
            _ = SettingsForm.DitherApplies(f);

        Assert.Equal(5, Enum.GetValues<FormatChoice>().Length);   // 増えたら、DitherApplies の判断とこの数を見直す
    }
}
