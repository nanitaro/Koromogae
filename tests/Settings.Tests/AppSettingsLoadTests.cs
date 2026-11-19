using System.Text.Json;
using Koromogae;
using Koromogae.Audio;
using Xunit;

namespace Settings.Tests;

[Collection("AppLog")] // AppLog.LogPath は静的なので、このコレクション内は直列で実行する
public class AppSettingsLoadTests
{
    static AppSettings LoadJson(TempDir dir, string json)
    {
        File.WriteAllText(dir.File("settings.json"), json);
        return AppSettings.Load(dir.File("settings.json"));
    }

    // ---- 正常系 ----

    [Fact]
    public void Missing_file_gives_defaults_without_a_message()
    {
        using var dir = new TempDir();

        var settings = AppSettings.Load(dir.File("settings.json"));

        Assert.Null(settings.LoadMessage);
        Assert.Equal(AppSettings.DefaultSampleRate, settings.SampleRate);
        Assert.Empty(settings.Plugins);
    }

    [Fact]
    public void Valid_settings_round_trip_unchanged()
    {
        using var dir = new TempDir();
        var original = new AppSettings
        {
            Api = ApiKind.KernelStreaming,
            InputDevice = "マイク",
            OutputDevice = "スピーカー",
            SampleRate = 96000,
            LatencyMs = 50,
            Plugins =
            {
                new PluginSetting { Path = @"C:\vst\a.dll", Enabled = false, State = "AQID" },
                new PluginSetting { Path = @"C:\vst\b.vst3", Enabled = true },
            },
        };
        Assert.True(original.Save(dir.File("settings.json")));

        var loaded = AppSettings.Load(dir.File("settings.json"));

        Assert.Null(loaded.LoadMessage);
        Assert.Equal(ApiKind.KernelStreaming, loaded.Api);
        Assert.Equal("マイク", loaded.InputDevice);
        Assert.Equal(96000, loaded.SampleRate);
        Assert.Equal(50, loaded.LatencyMs);
        Assert.Equal(2, loaded.Plugins.Count);
        Assert.False(loaded.Plugins[0].Enabled);
        Assert.Equal("AQID", loaded.Plugins[0].State);
    }

    // ---- 壊れたファイル: 退避して既定値で起動する ----

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"SampleRate\": \"fast\"}")]
    public void Corrupt_file_is_moved_to_bak_and_defaults_are_returned(string content)
    {
        using var dir = new TempDir();

        var settings = LoadJson(dir, content);

        Assert.NotNull(settings.LoadMessage);
        Assert.Contains(".bak", settings.LoadMessage);
        Assert.False(File.Exists(dir.File("settings.json")));
        Assert.Equal(content, File.ReadAllText(dir.File("settings.json.bak"))); // 元の内容がそのまま残る
        Assert.Equal(AppSettings.DefaultSampleRate, settings.SampleRate);
        Assert.Empty(settings.Plugins);
        Assert.Contains("設定ファイルを読み込めません", TestSupport.ReadLog());
    }

    [Fact]
    public void Oversized_file_is_quarantined_without_being_read()
    {
        using var dir = new TempDir();
        using (var fs = new FileStream(dir.File("settings.json"), FileMode.Create))
            fs.SetLength(AppSettings.MaxFileBytes + 1); // スパースファイル。実際には読まれない

        var settings = AppSettings.Load(dir.File("settings.json"));

        Assert.NotNull(settings.LoadMessage);
        Assert.Contains("大きすぎます", settings.LoadMessage);
        Assert.True(File.Exists(dir.File("settings.json.bak")));
        Assert.False(File.Exists(dir.File("settings.json")));
    }

    [Fact]
    public void A_second_corrupt_file_replaces_the_previous_bak()
    {
        using var dir = new TempDir();
        LoadJson(dir, "first broken");
        LoadJson(dir, "second broken");

        Assert.Equal("second broken", File.ReadAllText(dir.File("settings.json.bak")));
    }

    // ---- 値の補正 ----

    [Fact]
    public void Out_of_range_values_are_corrected_to_defaults()
    {
        using var dir = new TempDir();

        var settings = LoadJson(dir, """{"Api": 99, "SampleRate": 12345, "LatencyMs": -4}""");

        Assert.Equal(ApiKind.WasapiShared, settings.Api);
        Assert.Equal(AppSettings.DefaultSampleRate, settings.SampleRate);
        Assert.Equal(AppSettings.DefaultLatencyMs, settings.LatencyMs);
        Assert.NotNull(settings.LoadMessage);
        Assert.False(File.Exists(dir.File("settings.json.bak"))); // 一部の補正ではファイルを退避しない
    }

    [Fact]
    public void Overlong_device_names_are_dropped()
    {
        using var dir = new TempDir();
        string json = JsonSerializer.Serialize(new { InputDevice = new string('x', AppSettings.MaxDeviceNameLength + 1), OutputDevice = "ok" });

        var settings = LoadJson(dir, json);

        Assert.Null(settings.InputDevice);
        Assert.Equal("ok", settings.OutputDevice);
    }

    [Fact]
    public void Null_plugin_list_and_null_entries_do_not_crash()
    {
        using var dir = new TempDir();

        var settings = LoadJson(dir, """{"Plugins": [null, {"Path": "C:\\a.dll"}]}""");
        Assert.Single(settings.Plugins);
        Assert.Equal(@"C:\a.dll", settings.Plugins[0].Path);

        var nullList = LoadJson(dir, """{"Plugins": null}""");
        Assert.Empty(nullList.Plugins);
    }

    // ---- プラグイン一覧の上限 ----

    [Fact]
    public void Plugin_count_is_capped()
    {
        using var dir = new TempDir();
        var plugins = Enumerable.Range(0, AppSettings.MaxPlugins + 36).Select(i => new { Path = $@"C:\vst\p{i}.dll" });
        string json = JsonSerializer.Serialize(new { Plugins = plugins });

        var settings = LoadJson(dir, json);

        Assert.Equal(AppSettings.MaxPlugins, settings.Plugins.Count);
        Assert.Equal(@"C:\vst\p0.dll", settings.Plugins[0].Path);                          // 先頭から順に残る
        Assert.Equal($@"C:\vst\p{AppSettings.MaxPlugins - 1}.dll", settings.Plugins[^1].Path);
        Assert.Contains("上限", TestSupport.ReadLog());
    }

    [Fact]
    public void Entries_with_bad_paths_are_dropped_and_do_not_count_toward_the_limit()
    {
        using var dir = new TempDir();
        var good = new { Path = @"C:\vst\good.dll" };
        var plugins = new object[]
        {
            new { Path = "" },
            new { Path = "   " },
            new { Path = new string('a', AppSettings.MaxPathLength + 1) },
            new { Path = "C:\\bad\0path.dll" },
            good,
        };

        var settings = LoadJson(dir, JsonSerializer.Serialize(new { Plugins = plugins }));

        Assert.Single(settings.Plugins);
        Assert.Equal(good.Path, settings.Plugins[0].Path);
    }

    [Fact]
    public void Oversized_plugin_state_is_discarded_but_the_plugin_is_kept()
    {
        var settings = new AppSettings
        {
            Plugins =
            {
                new PluginSetting { Path = @"C:\vst\big.dll", State = new string('A', AppSettings.MaxStateBase64Chars + 1) },
                new PluginSetting { Path = @"C:\vst\ok.dll", State = new string('A', 1000) },
            },
        };
        var warnings = new List<string>();

        settings.Sanitize(warnings);

        Assert.Equal(2, settings.Plugins.Count);
        Assert.Null(settings.Plugins[0].State);
        Assert.Equal(1000, settings.Plugins[1].State!.Length);
        Assert.Single(warnings);
    }

    [Fact]
    public void State_limit_matches_the_base64_length_of_the_byte_limit()
    {
        // 上限ちょうどのバイト列を Base64 にしても、文字数の上限を超えない
        int encoded = (AppSettings.MaxStateBytes + 2) / 3 * 4;
        Assert.Equal(encoded, AppSettings.MaxStateBase64Chars);
        Assert.Equal(encoded, Convert.ToBase64String(new byte[AppSettings.MaxStateBytes]).Length);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(2, 2)]
    [InlineData(1_000_000, AppSettings.MaxLoadFailures)]
    public void Fail_count_is_clamped(int stored, int expected)
    {
        using var dir = new TempDir();
        string json = JsonSerializer.Serialize(new { Plugins = new[] { new { Path = @"C:\vst\a.dll", FailCount = stored } } });

        var settings = LoadJson(dir, json);

        Assert.Equal(expected, settings.Plugins[0].FailCount);
    }

    [Fact]
    public void The_selectable_buffer_sizes_are_the_expected_ones_in_ascending_order()
    {
        // 0 は PortAudio に「できるだけ小さく」を任せる値。200〜500ms は大きなバッファ
        Assert.Equal(new[] { 0, 5, 10, 20, 30, 50, 100, 200, 300, 400, 500 }, AppSettings.AllowedLatenciesMs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    [InlineData(300)]
    [InlineData(400)]
    [InlineData(500)]
    public void The_added_buffer_sizes_are_accepted_when_loading_without_correction(int ms)
    {
        using var dir = new TempDir();

        var settings = LoadJson(dir, $$"""{"LatencyMs": {{ms}}}""");

        Assert.Equal(ms, settings.LatencyMs);
        Assert.Null(settings.LoadMessage);   // 「選択肢にありません」と補正されない
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(600)]
    public void Buffer_sizes_outside_the_list_are_still_corrected(int ms)
    {
        using var dir = new TempDir();

        var settings = LoadJson(dir, $$"""{"LatencyMs": {{ms}}}""");

        Assert.Equal(AppSettings.DefaultLatencyMs, settings.LatencyMs);
        Assert.NotNull(settings.LoadMessage);
    }

    [Fact]
    public void The_selectable_cushion_extras_are_auto_and_the_requested_values()
    {
        Assert.Equal(new[] { 0, 5, 10, 20, 30, 40, 50, 100 }, AppSettings.AllowedCushionExtrasMs);
    }

    [Fact]
    public void Cushion_extra_defaults_to_auto_when_absent()
    {
        using var dir = new TempDir();

        var settings = LoadJson(dir, "{}");

        Assert.Equal(0, settings.CushionExtraMs);
        Assert.Null(settings.LoadMessage);
    }

    [Fact]
    public void Every_allowed_cushion_extra_survives_loading_without_correction()
    {
        using var dir = new TempDir();
        foreach (int ms in AppSettings.AllowedCushionExtrasMs)
        {
            var settings = LoadJson(dir, $$"""{"CushionExtraMs": {{ms}}}""");
            Assert.Equal(ms, settings.CushionExtraMs);
            Assert.Null(settings.LoadMessage);
        }
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(1000)]
    public void Cushion_extras_outside_the_list_are_corrected_to_auto(int ms)
    {
        using var dir = new TempDir();

        var settings = LoadJson(dir, $$"""{"CushionExtraMs": {{ms}}}""");

        Assert.Equal(0, settings.CushionExtraMs);
        Assert.NotNull(settings.LoadMessage);
    }

    [Fact]
    public void Sample_formats_default_to_auto_for_input_and_output()
    {
        using var dir = new TempDir();

        var settings = LoadJson(dir, "{}");

        Assert.Equal(FormatChoice.Auto, settings.InputFormat);
        Assert.Equal(FormatChoice.Auto, settings.OutputFormat);
        Assert.Null(settings.LoadMessage);
    }

    [Fact]
    public void Input_and_output_sample_formats_are_stored_independently()
    {
        using var dir = new TempDir();

        var settings = LoadJson(dir, """{"InputFormat": 4, "OutputFormat": 3}""");

        Assert.Equal(FormatChoice.Int16, settings.InputFormat);
        Assert.Equal(FormatChoice.Int24, settings.OutputFormat);
        Assert.Null(settings.LoadMessage);
    }

    [Fact]
    public void Every_sample_format_survives_loading_without_correction()
    {
        using var dir = new TempDir();
        foreach (var f in Enum.GetValues<FormatChoice>())
        {
            var settings = LoadJson(dir, $$"""{"InputFormat": {{(int)f}}, "OutputFormat": {{(int)f}}}""");
            Assert.Equal(f, settings.InputFormat);
            Assert.Equal(f, settings.OutputFormat);
            Assert.Null(settings.LoadMessage);
        }
    }

    [Fact]
    public void Invalid_sample_formats_are_corrected_to_auto_only_for_the_bad_side()
    {
        using var dir = new TempDir();

        var settings = LoadJson(dir, """{"InputFormat": 99, "OutputFormat": 4}""");

        Assert.Equal(FormatChoice.Auto, settings.InputFormat);
        Assert.Equal(FormatChoice.Int16, settings.OutputFormat);
        Assert.NotNull(settings.LoadMessage);
    }

    [Fact]
    public void Dither_mode_defaults_to_tpdf_when_absent()
    {
        using var dir = new TempDir();

        var settings = LoadJson(dir, "{}");

        Assert.Equal(DitherMode.Tpdf, settings.DitherMode);
        Assert.Null(settings.LoadMessage);
    }

    [Fact]
    public void The_shaped_dither_mode_is_a_valid_setting_and_is_stored_as_3()
    {
        using var dir = new TempDir();

        var s = LoadJson(dir, """{"DitherMode": 3}""");

        Assert.Equal(DitherMode.Shaped, s.DitherMode);
        Assert.Equal(3, (int)DitherMode.Shaped);
        Assert.Equal(0, (int)DitherMode.Tpdf);     // 既存の設定の値は変わらない (古い設定が別の方式に化けない)
        Assert.Equal(1, (int)DitherMode.HpTpdf);
        Assert.Equal(2, (int)DitherMode.Off);
        Assert.Null(s.LoadMessage);
    }

    [Fact]
    public void Every_dither_mode_survives_loading_without_correction()
    {
        using var dir = new TempDir();
        foreach (var m in Enum.GetValues<DitherMode>())
        {
            var settings = LoadJson(dir, $$"""{"DitherMode": {{(int)m}}}""");
            Assert.Equal(m, settings.DitherMode);
            Assert.Null(settings.LoadMessage);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void Invalid_dither_modes_are_corrected_to_tpdf(int value)
    {
        using var dir = new TempDir();

        var settings = LoadJson(dir, $$"""{"DitherMode": {{value}}}""");

        Assert.Equal(DitherMode.Tpdf, settings.DitherMode);
        Assert.NotNull(settings.LoadMessage);
    }

    [Fact]
    public void Recording_settings_default_to_Int24_the_default_folder_and_no_dry()
    {
        using var dir = new TempDir();

        var s = LoadJson(dir, "{}");

        Assert.Equal(RecordDepth.Int24, s.RecordDepth);
        Assert.Null(s.RecordFolder);
        Assert.False(s.RecordDry);
        Assert.Null(s.LoadMessage);
    }

    [Fact]
    public void The_default_record_folder_is_Koromogae_under_Music()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Koromogae"), AppSettings.DefaultRecordFolder);
    }

    [Fact]
    public void Recording_settings_survive_loading_without_correction()
    {
        using var dir = new TempDir();

        var s = LoadJson(dir, """{"RecordFolder": "D:\\Recordings", "RecordDepth": 2, "RecordDry": true}""");

        Assert.Equal(@"D:\Recordings", s.RecordFolder);
        Assert.Equal(RecordDepth.Int16, s.RecordDepth);
        Assert.True(s.RecordDry);
        Assert.Null(s.LoadMessage);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    public void An_invalid_record_depth_is_corrected_to_Int24(int value)
    {
        using var dir = new TempDir();

        var s = LoadJson(dir, $$"""{"RecordDepth": {{value}}}""");

        Assert.Equal(RecordDepth.Int24, s.RecordDepth);
        Assert.NotNull(s.LoadMessage);
    }

    [Fact]
    public void A_blank_record_folder_means_the_default()
    {
        using var dir = new TempDir();

        var s = LoadJson(dir, """{"RecordFolder": "   "}""");

        Assert.Null(s.RecordFolder);
        Assert.Null(s.LoadMessage);       // 空は異常ではない
    }

    [Fact]
    public void A_record_folder_is_trimmed()
    {
        using var dir = new TempDir();

        Assert.Equal(@"D:\Rec", LoadJson(dir, """{"RecordFolder": "  D:\\Rec  "}""").RecordFolder);
    }

    [Fact]
    public void A_too_long_or_invalid_record_folder_is_replaced_by_the_default_and_reported()
    {
        using var dir = new TempDir();
        string tooLong = new string('a', AppSettings.MaxPathLength + 1);

        var longOne = LoadJson(dir, "{\"RecordFolder\": \"" + tooLong + "\"}");
        var invalid = LoadJson(dir, "{\"RecordFolder\": \"D:\\\\bad\\u0000name\"}");

        Assert.Null(longOne.RecordFolder);
        Assert.NotNull(longOne.LoadMessage);
        Assert.Null(invalid.RecordFolder);
        Assert.NotNull(invalid.LoadMessage);
    }

    [Fact]
    public void The_record_format_defaults_to_wav_and_the_flac_level_to_5()
    {
        using var dir = new TempDir();

        var s = LoadJson(dir, "{}");

        Assert.Equal(RecordFormat.Wav, s.RecordFormat);
        Assert.Equal(5, s.FlacLevel);
        Assert.Equal(5, AppSettings.DefaultFlacLevel);
        Assert.Null(s.LoadMessage);
    }

    [Fact]
    public void Flac_settings_survive_loading_without_correction()
    {
        using var dir = new TempDir();

        var s = LoadJson(dir, """{"RecordFormat": 1, "FlacLevel": 8, "RecordDepth": 2}""");

        Assert.Equal(RecordFormat.Flac, s.RecordFormat);
        Assert.Equal(8, s.FlacLevel);
        Assert.Equal(RecordDepth.Int16, s.RecordDepth);
        Assert.Null(s.LoadMessage);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void Every_flac_level_from_0_to_8_is_accepted(int level)
    {
        using var dir = new TempDir();

        var s = LoadJson(dir, $$"""{"FlacLevel": {{level}}}""");

        Assert.Equal(level, s.FlacLevel);
        Assert.Null(s.LoadMessage);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    [InlineData(100)]
    public void A_flac_level_outside_0_to_8_is_corrected_to_the_default(int level)
    {
        using var dir = new TempDir();

        var s = LoadJson(dir, $$"""{"FlacLevel": {{level}}}""");

        Assert.Equal(5, s.FlacLevel);
        Assert.NotNull(s.LoadMessage);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(99)]
    public void An_invalid_record_format_is_corrected_to_wav(int value)
    {
        using var dir = new TempDir();

        var s = LoadJson(dir, $$"""{"RecordFormat": {{value}}}""");

        Assert.Equal(RecordFormat.Wav, s.RecordFormat);
        Assert.NotNull(s.LoadMessage);
    }

    [Fact]
    public void Flac_with_float32_is_corrected_to_int24_because_flac_cannot_store_float()
    {
        using var dir = new TempDir();

        var s = LoadJson(dir, """{"RecordFormat": 1, "RecordDepth": 1}""");

        Assert.Equal(RecordFormat.Flac, s.RecordFormat);
        Assert.Equal(RecordDepth.Int24, s.RecordDepth);
        Assert.NotNull(s.LoadMessage);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    public void Loading_applies_the_same_format_and_depth_rule_as_the_screen(int format, int depth)
    {
        // 設定ファイルの補正と画面の選択肢は、同じ規則 (RecordFormatRules) で決まる。食い違うと、読み込んだ設定が画面で選べない値になる
        using var dir = new TempDir();

        var s = LoadJson(dir, $$"""{"RecordFormat": {{format}}, "RecordDepth": {{depth}}}""");

        Assert.Equal(RecordFormatRules.CoerceDepth((RecordFormat)format, (RecordDepth)depth), s.RecordDepth);
        Assert.Equal(RecordFormatRules.AllowsDepth((RecordFormat)format, (RecordDepth)depth), s.LoadMessage == null);
    }

    [Fact]
    public void Every_invalid_enum_value_is_corrected_to_its_default_constant()
    {
        // 既定値は AppSettings.Default* の 1 か所だけ。プロパティの初期値と補正先が食い違わないことを確かめる
        using var dir = new TempDir();

        var s = LoadJson(dir, """{"Api": 99, "InputFormat": 99, "OutputFormat": 99, "DitherMode": 99, "RecordDepth": 99, "RecordFormat": 99}""");

        Assert.Equal(AppSettings.DefaultApi, s.Api);
        Assert.Equal(AppSettings.DefaultFormat, s.InputFormat);
        Assert.Equal(AppSettings.DefaultFormat, s.OutputFormat);
        Assert.Equal(AppSettings.DefaultDitherMode, s.DitherMode);
        Assert.Equal(AppSettings.DefaultRecordDepth, s.RecordDepth);
        Assert.Equal(AppSettings.DefaultRecordFormat, s.RecordFormat);
        Assert.Contains("6 件", s.LoadMessage);

        var fresh = new AppSettings();
        Assert.Equal(fresh.Api, s.Api);
        Assert.Equal(fresh.DitherMode, s.DitherMode);
        Assert.Equal(fresh.RecordDepth, s.RecordDepth);
        Assert.Equal(fresh.RecordFormat, s.RecordFormat);
        Assert.Equal(fresh.InputFormat, s.InputFormat);
    }

    [Fact]
    public void Wav_with_float32_is_left_as_it_is()
    {
        using var dir = new TempDir();

        var s = LoadJson(dir, """{"RecordFormat": 0, "RecordDepth": 1}""");

        Assert.Equal(RecordDepth.Float32, s.RecordDepth);
        Assert.Null(s.LoadMessage);
    }

    [Fact]
    public void Every_allowed_sample_rate_and_latency_survives_loading()
    {
        using var dir = new TempDir();
        foreach (int rate in AppSettings.AllowedSampleRates)
            Assert.Equal(rate, LoadJson(dir, $$"""{"SampleRate": {{rate}}}""").SampleRate);
        foreach (int ms in AppSettings.AllowedLatenciesMs)
            Assert.Equal(ms, LoadJson(dir, $$"""{"LatencyMs": {{ms}}}""").LatencyMs);
    }
}
